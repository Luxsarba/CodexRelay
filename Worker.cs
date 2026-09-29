using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CodexRelay;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly RelayOptions _options;
    private readonly StateStore _store;
    private readonly TelegramBotClient _bot;
    private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Approval> _approvals = new();
    private readonly Dictionary<string, ThreadMenu> _menus = new();
    private readonly Dictionary<long, NewDraft> _newDrafts = new();
    private readonly Dictionary<long, int> _projectPrompts = new();
    private readonly HashSet<string> _loadedThreads = new();
    private AppServerClient? _server;
    private Run? _run;
    private DateTime _lastQueueRetryAt;

    public Worker(ILogger<Worker> log, ILogger<AppServerClient> serverLog, RelayOptions options)
    {
        _log = log;
        _options = options;
        _options.Validate();
        _store = new StateStore(options.StateFile);
        foreach (var (name, path) in _store.State.Projects)
        {
            if (Path.IsPathFullyQualified(path) && IsWithinProject(path, options.ProjectCreationRoot))
                _options.Projects.TryAdd(name, path);
            else _log.LogWarning("Ignoring saved project outside creation root: {Project}", name);
        }
        _bot = new TelegramBotClient(options.BotToken);
        ServerLog = serverLog;
    }

    private ILogger<AppServerClient> ServerLog { get; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _server = new AppServerClient(ServerLog, _options.ResolveCodex());
        _server.Message += msg => { _events.Writer.TryWrite(msg); return Task.CompletedTask; };
        await _server.InitializeAsync(ct);
        _ = ProcessEvents(ct);
        await _bot.DeleteWebhook(dropPendingUpdates: false, cancellationToken: ct);
        await StartQueued(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdates(offset: _store.State.UpdateOffset, timeout: 25,
                    allowedUpdates: new[] { UpdateType.Message, UpdateType.CallbackQuery }, cancellationToken: ct);
                foreach (var update in updates)
                {
                    await _gate.WaitAsync(ct);
                    try
                    {
                        _store.State.UpdateOffset = update.Id + 1;
                        try { await HandleUpdate(update, ct); }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Update {Id} failed", update.Id);
                            var message = update.Message ?? update.CallbackQuery?.Message;
                            if (message != null && TryAddress(message, out var address))
                                await Send(address, "Ошибка: " + ex.Message, ct);
                        }
                        await _store.SaveAsync();
                    }
                    finally { _gate.Release(); }
                }
                if (_run == null && _store.State.PendingTasks.Count > 0 &&
                    DateTime.UtcNow - _lastQueueRetryAt >= TimeSpan.FromSeconds(30))
                {
                    await _gate.WaitAsync(ct);
                    try { await StartQueued(ct); }
                    finally { _gate.Release(); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Telegram polling failed");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleUpdate(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            if (callback.From.Id != _options.OwnerUserId || callback.Message == null ||
                !TryAddress(callback.Message, out var callbackAddress)) return;
            await HandleCallback(callback, callbackAddress, ct);
            return;
        }
        var message = update.Message;
        if (message?.From?.Id != _options.OwnerUserId || !TryAddress(message, out var address)) return;
        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;
        if (input.StartsWith('/')) await Command(address, input, ct);
        else if (address.IsGeneral)
        {
            if (_projectPrompts.ContainsKey(address.ChatId)) await HandleProjectName(address, input, ct);
            else await HandleNewDraftText(address, input, ct);
        }
        else await StartTask(address, input, ct);
    }

    private async Task Command(TopicAddress address, string input, CancellationToken ct)
    {
        var words = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = words[0].Split('@')[0].ToLowerInvariant();
        var arg = words.Length > 1 ? words[1].Trim() : "";
        switch (cmd)
        {
            case "/start":
            case "/help":
            case "/menu":
                if (address.IsGeneral) await ShowGeneralMenu(address, null, ct);
                else await SendUi(address,
                    "<b>Рабочая тема</b>\n<i>Чат Codex</i>\n\n" +
                    "<blockquote>Просто напишите задачу. Ответ Codex появится в этой теме.</blockquote>\n\n" +
                    "<b>Управление</b>\n" +
                    "<code>/status</code>  Информация о чате\n" +
                    "<code>/last</code>  Последний ответ\n" +
                    "<code>/fork</code>  Ветка чата\n" +
                    "<code>/usage</code>  Лимиты Codex\n" +
                    "<code>/stop</code>  Остановить задачу", ct, TopicButtons());
                break;
            case "/projects":
                await SendUi(address, ProjectsMarkup(), ct,
                    address.IsGeneral ? GeneralButtons() : null);
                break;
            case "/link":
                if (!address.IsGeneral)
                {
                    await Send(address, "/link используйте в General.", ct);
                    break;
                }
                if (arg.Length == 0)
                {
                    await ShowProjectPicker(address, null, createThread: false, ct);
                    break;
                }
                var linkProject = arg;
                if (!_options.Projects.ContainsKey(linkProject))
                {
                    await Send(address, "Укажите проект: /link <проект>. Список: /projects", ct);
                    break;
                }
                await EnsureCanManageTopics(address.ChatId, ct);
                await ShowThreads(address, linkProject, null, null, createTopic: true, ct);
                break;
            case "/new":
                if (!address.IsGeneral)
                {
                    await Send(address, "/new используйте в General.", ct);
                    break;
                }
                if (arg.Length == 0)
                {
                    _projectPrompts.Remove(address.ChatId);
                    await ShowProjectPicker(address, null, createThread: true, ct);
                    break;
                }
                if (!_options.Projects.ContainsKey(arg))
                {
                    await Send(address, "Проект не найден. Используйте /new и выберите «Создать проект».", ct);
                    break;
                }
                await StartNewDraft(address, arg, null, ct);
                break;
            case "/threads":
                if (address.IsGeneral)
                {
                    await Send(address, "В General используйте /link <проект> или /new <проект>.", ct);
                    break;
                }
                if (_store.State.Topics.ContainsKey(address.Key))
                {
                    await Send(address, "Этот topic уже привязан. /status", ct);
                    break;
                }
                var project = arg.Length == 0 && _options.Projects.Count == 1 ? _options.Projects.Keys.First() : arg;
                if (!_options.Projects.ContainsKey(project))
                {
                    await Send(address, "Укажите проект: /threads <проект>. Список: /projects", ct);
                    break;
                }
                await ShowThreads(address, project, null, null, createTopic: false, ct);
                break;
            case "/bind":
                if (address.IsGeneral)
                {
                    await Send(address, "В General используйте /link <проект>.", ct);
                    break;
                }
                var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                await Send(address, parts.Length == 2
                    ? await Bind(address, parts[0], parts[1], ct)
                    : "Формат: /bind <проект> <threadId>", ct);
                break;
            case "/status":
                if (address.IsGeneral)
                {
                    await SendUi(address, GeneralStatusMarkup(address.ChatId), ct, GeneralButtons());
                    break;
                }
                if (!_store.State.Topics.TryGetValue(address.Key, out var binding))
                    await Send(address, "Topic не привязан. /threads <проект>", ct);
                else
                    await SendUi(address, $"<b>{(_run?.Address == address ? "Задача выполняется" : "Чат готов")}</b>\n" +
                        $"<i>{H(ProjectLabel(binding.Project))}</i>\n\n" +
                        $"<b>В очереди</b>  {_store.State.PendingTasks.Count(task => task.ChatId == address.ChatId && task.TopicId == address.TopicId)}\n\n" +
                        "<b>Последний ответ</b>\n" + AnswerBlock(await LastAnswer(binding.ThreadId, ct), 2100) +
                        "\n\n<blockquote expandable><b>Привязка</b>\n" +
                        $"Thread: {H(binding.ThreadId)}\n" +
                        $"Каталог: {H(binding.WorkingDirectory ?? ProjectRoot(binding.Project))}</blockquote>",
                        ct, TopicButtons());
                break;
            case "/usage":
                await SendUi(address, await UsageMarkup(ct), ct,
                    address.IsGeneral ? GeneralButtons() : TopicButtons());
                break;
            case "/last":
                if (address.IsGeneral || !_store.State.Topics.TryGetValue(address.Key, out var lastBinding))
                    await Send(address, "Команда доступна в привязанном рабочем topic.", ct);
                else
                    await SendLastAnswer(address, lastBinding.ThreadId, ct);
                break;
            case "/fork":
                if (address.IsGeneral || !_store.State.Topics.TryGetValue(address.Key, out var forkBinding))
                    await Send(address, "Команда доступна в привязанном рабочем topic.", ct);
                else if (_run?.Address == address)
                    await Send(address, "Дождитесь завершения текущей задачи и повторите /fork.", ct);
                else
                    await ForkThreadTopic(address, forkBinding, ct);
                break;
            case "/stop":
                if (_run?.Address != address || _run.TurnId is not { } turnId)
                {
                    await Send(address, "Активной задачи в этом topic нет.", ct);
                    break;
                }
                _run.Stopping = true;
                await _server!.CallAsync("turn/interrupt", new { threadId = _run.ThreadId, turnId }, ct);
                await SetStatus(_run, "Остановка запрошена", ct, force: true);
                break;
            default:
                await Send(address, "Неизвестная команда. /help", ct);
                break;
        }
    }

    private static InlineKeyboardMarkup GeneralButtons() => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Создать чат", "g:n"),
            InlineKeyboardButton.WithCallbackData("Подключить чат", "g:l")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Проекты", "g:p"),
            InlineKeyboardButton.WithCallbackData("Работа бота", "g:s")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Лимиты Codex", "g:u")
        }
    });

    private static InlineKeyboardMarkup GeneralBackButton() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("В меню", "g:h") }
    });

    private string ProjectsMarkup() => "<b>Проекты</b>\n<i>Рабочие пространства</i>\n\n" +
        (_options.Projects.Count == 0 ? "Пока нет проектов." :
            string.Join("\n\n", _options.Projects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select((p, index) => $"<b>{index + 1:00}  {H(p.Key)}</b>\n<code>{H(p.Value)}</code>"))) +
        $"\n\n<b>Без проекта</b>\n<code>{H(_options.ProjectlessChatRoot)}</code>";

    private string GeneralStatusMarkup(long chatId)
    {
        var topics = _store.State.Topics.Keys.Count(key =>
            key.StartsWith(chatId + ":", StringComparison.Ordinal));
        var queued = _store.State.PendingTasks.Count(task => task.ChatId == chatId);
        var active = _run is { } run && run.Address.ChatId == chatId
            ? $"topic <code>{run.Address.TopicId}</code>"
            : "нет";
        return "<b>Работа бота</b>\n<i>Сводка на сейчас</i>\n\n" +
            $"<b>{topics}</b> рабочих тем  ·  <b>{_options.Projects.Count}</b> проектов\n" +
            $"<b>{queued}</b> задач в очереди\n\n" +
            $"<b>Сейчас</b>  {(active == "нет" ? "свободен" : "задача в " + active)}\n\n" +
            "<blockquote>Лимиты и токены аккаунта — в разделе «Лимиты Codex».</blockquote>";
    }

    private async Task<string> UsageMarkup(CancellationToken ct)
    {
        try
        {
            var account = await _server!.CallAsync("account/read", new { refreshToken = false }, ct);
            if (!account.TryGetProperty("account", out var identity) ||
                identity.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return "<b>Лимиты Codex</b>\n\nCodex App Server не авторизован. " +
                    "Войдите в ChatGPT через <code>codex login</code> на компьютере с ботом.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read Codex account");
            return "<b>Лимиты Codex</b>\n\nНе удалось проверить авторизацию: " +
                H(Clip(ex.Message, 180));
        }
        var limitsTask = RateLimitsMarkup(ct);
        var tokensTask = TokenUsageMarkup(ct);
        await Task.WhenAll(limitsTask, tokensTask);
        return "<b>Лимиты Codex</b>\n<i>Аккаунт и использование</i>\n\n" +
            limitsTask.Result + "\n\n" + tokensTask.Result;
    }

    private async Task<string> RateLimitsMarkup(CancellationToken ct)
    {
        try
        {
            var response = await _server!.CallAsync("account/rateLimits/read", new { }, ct);
            var buckets = new List<(string Name, JsonElement Value)>();
            if (response.TryGetProperty("rateLimitsByLimitId", out var byId) &&
                byId.ValueKind == JsonValueKind.Object)
                buckets.AddRange(byId.EnumerateObject().Select(p => (p.Name, p.Value)));
            else if (response.TryGetProperty("rateLimits", out var single) &&
                single.ValueKind == JsonValueKind.Object)
                buckets.Add((GetString(single, "limitId") is { Length: > 0 } id ? id : "Codex", single));
            var lines = new List<string>();
            foreach (var (key, bucket) in buckets.Take(8))
            {
                var name = GetString(bucket, "limitName");
                if (name.Length == 0) name = key;
                var windows = new List<string>();
                foreach (var field in new[] { "primary", "secondary" })
                    if (bucket.TryGetProperty(field, out var window) && window.ValueKind == JsonValueKind.Object)
                        windows.Add(FormatLimitWindow(window));
                if (windows.Count > 0)
                    lines.Add($"<b>{H(name)}</b>\n" + string.Join("\n", windows));
            }
            return "<b>Лимиты аккаунта</b>\n" +
                (lines.Count == 0 ? "Данные не предоставлены." : string.Join("\n\n", lines));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read Codex rate limits");
            return "<b>Лимиты аккаунта</b>\nНедоступны: " + H(Clip(ex.Message, 180));
        }
    }

    private static string FormatLimitWindow(JsonElement window)
    {
        if (!window.TryGetProperty("usedPercent", out var usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number || !usedElement.TryGetDouble(out var used))
            return "нет данных";
        var duration = window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number && durationElement.TryGetInt32(out var mins)
            ? mins : 0;
        var period = duration switch
        {
            >= 1440 when duration % 1440 == 0 => $"{duration / 1440} д",
            >= 60 when duration % 60 == 0 => $"{duration / 60} ч",
            > 0 => $"{duration} мин",
            _ => "период"
        };
        var remaining = Math.Clamp(100 - used, 0, 100);
        var reset = "";
        if (window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number && resetElement.TryGetInt64(out var seconds))
        {
            try { reset = ", сброс " + DateTimeOffset.FromUnixTimeSeconds(seconds)
                .ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.GetCultureInfo("ru-RU")); }
            catch (ArgumentOutOfRangeException) { }
        }
        var filled = Math.Clamp((int)Math.Round(remaining / 10, MidpointRounding.AwayFromZero), 0, 10);
        var bar = new string('█', filled) + new string('░', 10 - filled);
        return $"<code>{bar}</code>  {period}: <b>{remaining:0.#}%</b>{reset}";
    }

    private async Task<string> TokenUsageMarkup(CancellationToken ct)
    {
        try
        {
            var response = await _server!.CallAsync("account/usage/read", new { }, ct);
            var lines = new List<string>();
            if (response.TryGetProperty("summary", out var summary) &&
                summary.ValueKind == JsonValueKind.Object &&
                summary.TryGetProperty("lifetimeTokens", out var lifetime) &&
                lifetime.ValueKind == JsonValueKind.Number && lifetime.TryGetInt64(out var total))
                lines.Add($"Всего: <b>{total:N0}</b> токенов");
            if (response.TryGetProperty("dailyUsageBuckets", out var daily) &&
                daily.ValueKind == JsonValueKind.Array)
            {
                var latest = daily.EnumerateArray()
                    .Where(day => day.ValueKind == JsonValueKind.Object &&
                        day.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number)
                    .OrderByDescending(day => GetString(day, "startDate"))
                    .FirstOrDefault();
                if (latest.ValueKind == JsonValueKind.Object &&
                    latest.GetProperty("tokens").TryGetInt64(out var count))
                    lines.Add($"{H(GetString(latest, "startDate"))}: <b>{count:N0}</b> токенов");
            }
            return "<b>Токены аккаунта</b>\n" +
                (lines.Count == 0 ? "Данные не предоставлены." : string.Join("\n", lines));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read Codex token usage");
            return "<b>Токены аккаунта</b>\nНедоступны: " + H(Clip(ex.Message, 180));
        }
    }

    private static InlineKeyboardMarkup TopicButtons() => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Показать ответ", "t:l"),
            InlineKeyboardButton.WithCallbackData("Создать ветку", "t:f")
        }
    });

    private async Task ShowGeneralMenu(TopicAddress address, int? editMessageId, CancellationToken ct)
    {
        _projectPrompts.Remove(address.ChatId);
        _newDrafts.Remove(address.ChatId);
        var topics = _store.State.Topics.Keys.Count(key =>
            key.StartsWith(address.ChatId + ":", StringComparison.Ordinal));
        var queued = _store.State.PendingTasks.Count(task => task.ChatId == address.ChatId);
        var message = "<b>Codex Relay</b>\n<i>Управление чатами</i>\n\n" +
            "<blockquote>Одна тема Telegram — один чат Codex. Напишите задачу в рабочей теме, чтобы продолжить его.</blockquote>\n" +
            $"<b>{topics}</b> рабочих тем  ·  <b>{queued}</b> в очереди\n\n" +
            "Создайте чат или подключите существующий.";
        if (editMessageId is int id)
            await _bot.EditMessageText(address.ChatId, id, message,
                parseMode: ParseMode.Html, replyMarkup: GeneralButtons(), cancellationToken: ct);
        else
            await _bot.SendMessage(address.ChatId, message,
                parseMode: ParseMode.Html, replyMarkup: GeneralButtons(), cancellationToken: ct);
    }

    private async Task ShowProjectPicker(TopicAddress address, int? messageId, bool createThread,
        CancellationToken ct)
    {
        var projects = ProjectNames();
        var fingerprint = ProjectFingerprint(projects);
        var action = createThread ? "n" : "l";
        var buttons = projects.Select((name, index) => new[]
        {
            InlineKeyboardButton.WithCallbackData(Clip(name, 45), $"g:{action}:{index}:{fingerprint}")
        }).ToList();
        buttons.Insert(0, new[] { InlineKeyboardButton.WithCallbackData("Без проекта", createThread ? "g:z" : "g:q") });
        if (createThread)
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Создать проект", "g:c") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("В меню", "g:h") });
        var text = (createThread ? "<b>Новый чат</b>" : "<b>Подключить чат</b>") +
            "\n<i>1 / 2 · Рабочее пространство</i>\n\n" +
            (createThread
                ? "Выберите, где Codex будет работать с файлами."
                : "Выберите, где находится существующий чат Codex.") +
            $"\n\n<b>Без проекта</b>\n<code>{H(_options.ProjectlessChatRoot)}</code>\n\n" +
            $"<b>Проекты</b>  {projects.Length}\n" +
            (projects.Length == 0 ? "Пока нет проектов." :
                string.Join("  ·  ", projects.Take(6).Select(H)) +
                (projects.Length > 6 ? $"  ·  ещё {projects.Length - 6}" : "")) +
            "\n\n<blockquote>Выберите вариант кнопкой ниже.</blockquote>";
        var markup = new InlineKeyboardMarkup(buttons);
        if (messageId is int id)
            await _bot.EditMessageText(address.ChatId, id, text,
                parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
        else
            await _bot.SendMessage(address.ChatId, text,
                parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
    }

    private string[] ProjectNames() => _options.Projects.Keys
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string ProjectFingerprint(string[] projects) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", projects))))[..8];

    private async Task HandleGeneralCallback(CallbackQuery callback, TopicAddress address,
        string[] parts, CancellationToken ct)
    {
        if (!address.IsGeneral || parts.Length < 2 || callback.Message == null)
        {
            await _bot.AnswerCallbackQuery(callback.Id, "Кнопка доступна в General.", cancellationToken: ct);
            return;
        }
        var projects = ProjectNames();
        string? selected = null;
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[2], out var index) || index < 0 || index >= projects.Length ||
                parts[3] != ProjectFingerprint(projects))
            {
                await _bot.AnswerCallbackQuery(callback.Id, "Список проектов изменился. Откройте /menu.", cancellationToken: ct);
                return;
            }
            selected = projects[index];
        }
        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
        var messageId = callback.Message.MessageId;
        switch (parts[1])
        {
            case "h" when parts.Length == 2:
                _projectPrompts.Remove(address.ChatId);
                await ShowGeneralMenu(address, messageId, ct);
                break;
            case "p" when parts.Length == 2:
                await _bot.EditMessageText(address.ChatId, messageId,
                    ProjectsMarkup(),
                    parseMode: ParseMode.Html, replyMarkup: GeneralBackButton(), cancellationToken: ct);
                break;
            case "s" when parts.Length == 2:
                await _bot.EditMessageText(address.ChatId, messageId,
                    GeneralStatusMarkup(address.ChatId),
                    parseMode: ParseMode.Html, replyMarkup: GeneralBackButton(), cancellationToken: ct);
                break;
            case "u" when parts.Length == 2:
                await _bot.EditMessageText(address.ChatId, messageId,
                    await UsageMarkup(ct),
                    parseMode: ParseMode.Html, replyMarkup: GeneralBackButton(), cancellationToken: ct);
                break;
            case "c" when parts.Length == 2:
                _newDrafts.Remove(address.ChatId);
                _projectPrompts[address.ChatId] = messageId;
                await _bot.EditMessageText(address.ChatId, messageId,
                    "<b>Новый проект</b>\n<i>1 / 2 · Название проекта</i>\n\n" +
                    "<blockquote>Отправьте название обычным сообщением. Можно на русском.</blockquote>\n\n" +
                    $"<b>Каталог проектов</b>\n<code>{H(_options.ProjectCreationRoot)}</code>\n" +
                    "Имя папки бот создаст латинницей.",
                    parseMode: ParseMode.Html, replyMarkup: GeneralBackButton(), cancellationToken: ct);
                break;
            case "z" when parts.Length == 2:
                await StartNewDraft(address, "", messageId, ct);
                break;
            case "q" when parts.Length == 2:
                await EnsureCanManageTopics(address.ChatId, ct);
                await ShowThreads(address, "", null, messageId, createTopic: true, ct);
                break;
            case "l" when parts.Length is 2 or 4:
            case "n" when parts.Length is 2 or 4:
                if (selected == null && (projects.Length != 1 || parts[1] == "n"))
                {
                    await ShowProjectPicker(address, messageId, parts[1] == "n", ct);
                    break;
                }
                var project = selected ?? projects[0];
                if (parts[1] == "l")
                {
                    await EnsureCanManageTopics(address.ChatId, ct);
                    await ShowThreads(address, project, null, messageId, createTopic: true, ct);
                }
                else
                {
                    await StartNewDraft(address, project, messageId, ct);
                }
                break;
            default:
                await ShowGeneralMenu(address, messageId, ct);
                break;
        }
    }

    private async Task HandleProjectName(TopicAddress address, string input, CancellationToken ct)
    {
        if (!_projectPrompts.TryGetValue(address.ChatId, out var messageId)) return;
        var name = CleanTitle(input);
        if (name.Length is < 1 or > 64)
        {
            await Send(address, "Название проекта должно содержать от 1 до 64 символов.", ct);
            return;
        }
        if (_options.Projects.ContainsKey(name))
        {
            await Send(address, "Проект с таким названием уже есть. Выберите его через /new.", ct);
            return;
        }
        var root = Path.GetFullPath(_options.ProjectCreationRoot);
        var slug = ProjectSlug(name);
        var path = NextFreeDirectory(root, slug);
        if (!IsWithinProject(path, root))
            throw new InvalidOperationException("Каталог проекта вышел за разрешённый корень.");
        Directory.CreateDirectory(path);
        _store.State.Projects[name] = path;
        _options.Projects[name] = path;
        try { await _store.SaveAsync(); }
        catch
        {
            _store.State.Projects.Remove(name);
            _options.Projects.Remove(name);
            try { Directory.Delete(path); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not remove unused project directory {Path}", path); }
            throw;
        }
        _projectPrompts.Remove(address.ChatId);
        await StartNewDraft(address, name, messageId, ct, name, newProject: true);
    }

    private static string NextFreeDirectory(string root, string slug)
    {
        var path = Path.Combine(root, slug);
        for (var number = 2; Directory.Exists(path) || File.Exists(path); number++)
            path = Path.Combine(root, $"{slug}-{number}");
        return path;
    }

    private static string ProjectSlug(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var part = c switch
            {
                >= 'a' and <= 'z' or >= '0' and <= '9' => c.ToString(),
                'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d",
                'е' or 'э' => "e", 'ё' => "yo", 'ж' => "zh", 'з' => "z", 'и' or 'й' => "i",
                'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p",
                'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u", 'ф' => "f", 'х' => "kh",
                'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "shch", 'ы' => "y",
                'ю' => "yu", 'я' => "ya", 'ъ' or 'ь' => "",
                'і' => "i", 'ї' => "yi", 'є' => "ye", 'ґ' => "g", 'ў' => "u",
                _ => "-"
            };
            if (part == "-")
            {
                if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            }
            else slug.Append(part);
        }
        var result = slug.ToString().Trim('-');
        if (result.Length > 48) result = result[..48].TrimEnd('-');
        if (result.Length == 0) result = "project-" + Guid.NewGuid().ToString("N")[..8];
        if (new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5",
                "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5",
                "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(result)) result = "project-" + result;
        return result;
    }

    private async Task StartNewDraft(TopicAddress address, string project, int? editMessageId,
        CancellationToken ct, string? initialName = null, bool newProject = false)
    {
        var root = project.Length == 0
            ? Path.Combine(ProjectRoot(project), DateTime.Now.ToString("yyyy-MM-dd"))
            : ProjectRoot(project);
        var draft = new NewDraft(project, newProject ? root : null) { AutoDirectory = !newProject };
        draft.Name = initialName;
        _newDrafts[address.ChatId] = draft;
        if (editMessageId is int id) draft.MessageId = id;
        else
        {
            var message = await _bot.SendMessage(address.ChatId, "Подготовка нового чата…", cancellationToken: ct);
            draft.MessageId = message.MessageId;
        }
        await ShowNewDraft(address, draft, ct);
    }

    private async Task ShowNewDraft(TopicAddress address, NewDraft draft, CancellationToken ct)
    {
        var ready = draft.Name != null && draft.Directory != null;
        var hint = draft.Awaiting == "directory"
            ? "<blockquote>Отправьте путь обычным сообщением. <code>.</code> — каталог по умолчанию; можно указать подкаталог или полный путь внутри разрешённого корня.</blockquote>"
            : draft.Awaiting == "name"
                ? "<blockquote>Отправьте название чата обычным сообщением.</blockquote>"
                : ready
                    ? "<blockquote>Всё готово. Создайте чат кнопкой ниже.</blockquote>"
                    : "<blockquote>Задайте название. Каталог появится автоматически.</blockquote>";
        var text = "<b>Новый чат</b>\n<i>2 / 2 · Настройка</i>\n\n" +
            $"<b>Пространство</b>  {H(ProjectLabel(draft.Project))}\n\n" +
            $"<b>Название</b>\n{H(draft.Name ?? "Не задано")}" +
            $"\n\n<b>Рабочий каталог</b>\n<code>{H(draft.Directory ?? "После выбора названия")}</code>" +
            (draft.AutoDirectory && draft.Directory != null ? "\n<i>Новый каталог создаст бот</i>" : "") +
            "\n\n" + hint;
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("Изменить название", "w:n"),
                InlineKeyboardButton.WithCallbackData("Изменить каталог", "w:d") }
        };
        if (ready) rows.Add(new[] { InlineKeyboardButton.WithCallbackData("Создать чат", "w:c") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("Отмена", "w:x") });
        var buttons = new InlineKeyboardMarkup(rows);
        await _bot.EditMessageText(address.ChatId, draft.MessageId, text,
            parseMode: ParseMode.Html, replyMarkup: buttons, cancellationToken: ct);
    }

    private async Task HandleNewDraftText(TopicAddress address, string input, CancellationToken ct)
    {
        if (!_newDrafts.TryGetValue(address.ChatId, out var draft) || draft.Awaiting == null) return;
        if (draft.Awaiting == "directory")
        {
            var root = draft.Project.Length == 0
                ? Path.Combine(ProjectRoot(draft.Project), DateTime.Now.ToString("yyyy-MM-dd"))
                : ProjectRoot(draft.Project);
            string path;
            try { path = Path.GetFullPath(Path.IsPathFullyQualified(input) ? input : Path.Combine(root, input)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                await Send(address, "Неверный путь: " + ex.Message, ct);
                return;
            }
            if (!IsWithinProject(path, ProjectRoot(draft.Project)))
            {
                await Send(address, "Выберите каталог внутри разрешённого корня. Его можно изменить в appsettings.json.", ct);
                return;
            }
            draft.Directory = path;
            draft.AutoDirectory = false;
        }
        else
        {
            var name = CleanTitle(input);
            if (name.Length is < 1 or > 128)
            {
                await Send(address, "Название должно содержать от 1 до 128 символов.", ct);
                return;
            }
            draft.Name = name;
            if (draft.AutoDirectory)
                draft.Directory = NextFreeDirectory(
                    draft.Project.Length == 0
                        ? Path.Combine(ProjectRoot(draft.Project), DateTime.Now.ToString("yyyy-MM-dd"))
                        : ProjectRoot(draft.Project),
                    draft.Project.Length == 0 ? "new-chat" : ProjectSlug(name));
        }
        draft.Awaiting = null;
        await ShowNewDraft(address, draft, ct);
    }

    private async Task HandleNewDraftCallback(CallbackQuery callback, TopicAddress address,
        string action, CancellationToken ct)
    {
        if (!address.IsGeneral || callback.Message == null ||
            !_newDrafts.TryGetValue(address.ChatId, out var draft) ||
            draft.MessageId != callback.Message.MessageId)
        {
            await _bot.AnswerCallbackQuery(callback.Id, "Форма устарела. Используйте /new.", cancellationToken: ct);
            return;
        }
        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
        if (action == "x")
        {
            _newDrafts.Remove(address.ChatId);
            await ShowGeneralMenu(address, draft.MessageId, ct);
            return;
        }
        if (action is "d" or "n")
        {
            draft.Awaiting = action == "d" ? "directory" : "name";
            await ShowNewDraft(address, draft, ct);
            return;
        }
        if (action != "c") return;
        if (draft.Directory == null || draft.Name == null)
        {
            await ShowNewDraft(address, draft, ct);
            return;
        }
        // Recheck the directory at creation time; a draft may outlive filesystem changes.
        if (!IsWithinProject(draft.Directory, ProjectRoot(draft.Project)))
        {
            draft.Directory = null;
            draft.Awaiting = "directory";
            await ShowNewDraft(address, draft, ct);
            return;
        }
        await _bot.EditMessageText(address.ChatId, draft.MessageId, "<b>Новый чат</b>\nСоздаётся…",
            parseMode: ParseMode.Html, cancellationToken: ct);
        try
        {
            await EnsureCanManageTopics(address.ChatId, ct);
            if (draft.AutoDirectory && (Directory.Exists(draft.Directory) || File.Exists(draft.Directory)))
                draft.Directory = NextFreeDirectory(Path.GetDirectoryName(draft.Directory)!,
                    Path.GetFileName(draft.Directory));
            var directoryCreated = !Directory.Exists(draft.Directory);
            if (directoryCreated) Directory.CreateDirectory(draft.Directory);
            string result;
            try { result = await NewThreadTopic(address.ChatId, draft.Project, draft.Directory, draft.Name, ct); }
            catch
            {
                if (directoryCreated)
                    try { Directory.Delete(draft.Directory); }
                    catch (Exception ex) { _log.LogWarning(ex, "Could not remove unused chat directory {Path}", draft.Directory); }
                throw;
            }
            _newDrafts.Remove(address.ChatId);
            await _bot.EditMessageText(address.ChatId, draft.MessageId,
                "<b>Новый чат</b>\n" + H(result), parseMode: ParseMode.Html,
                replyMarkup: GeneralBackButton(), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            draft.Awaiting = null;
            await ShowNewDraft(address, draft, ct);
            await Send(address, "Не удалось создать чат: " + ex.Message, ct);
        }
    }

    private async Task ShowThreads(TopicAddress address, string project, string? cursor, int? editMessageId,
        bool createTopic, CancellationToken ct)
    {
        var path = ProjectRoot(project);
        var response = await _server!.CallAsync("thread/list",
            new { limit = 20, cursor, sortKey = "recency_at", sortDirection = "desc",
                sourceKinds = new[] { "cli", "vscode", "appServer", "unknown" } }, ct);
        var threads = response.GetProperty("data").EnumerateArray()
            .Where(t => Path.IsPathFullyQualified(GetString(t, "cwd")) &&
                IsWithinProject(GetString(t, "cwd"), path) &&
                (project.Length != 0 || !_options.Projects.Values.Any(root =>
                    IsWithinProject(GetString(t, "cwd"), root))))
            .Select(t => new ThreadChoice(GetString(t, "id"),
                GetString(t, "name") is { Length: > 0 } name ? name : GetString(t, "preview")))
            .Where(t => t.Id.Length > 0 && !_store.State.Topics.Values.Any(b => b.ThreadId == t.Id))
            .ToList();
        var next = GetString(response, "nextCursor");
        if (threads.Count == 0 && next.Length == 0)
        {
            var empty = createTopic
                ? "Свободных чатов нет. Создайте новый через /new или в Codex Desktop."
                : "Существующих свободных чатов нет. Создайте чат в Codex Desktop и повторите /threads.";
            var emptyScreen = "<b>Нет доступных чатов</b>\n" +
                $"<i>{H(ProjectLabel(project))}</i>\n\n<blockquote>{H(empty)}</blockquote>";
            if (editMessageId is int messageId)
                await _bot.EditMessageText(address.ChatId, messageId, emptyScreen,
                    parseMode: ParseMode.Html, replyMarkup: address.IsGeneral ? GeneralBackButton() : null, cancellationToken: ct);
            else await SendUi(address, emptyScreen, ct);
            return;
        }
        var key = Guid.NewGuid().ToString("N")[..10];
        _menus[key] = new ThreadMenu(address, project, threads, next, createTopic);
        var lines = threads.Select((t, i) =>
            $"<code>{i + 1:00} / {H(t.Id[..Math.Min(8, t.Id.Length)])}</code>  " +
            $"<b>{H(Clip(t.Title.Replace('\n', ' '), 72))}</b>");
        var text = $"<b>{(createTopic ? "Подключить чат" : "Выбрать чат")}</b>\n" +
            $"<i>2 / 2 · {H(ProjectLabel(project))}</i>\n\n" +
            (threads.Count == 0 ? "На этой странице свободных чатов нет." : string.Join("\n\n", lines)) +
            (createTopic
                ? "\n\n<blockquote>После выбора бот создаст отдельную тему Telegram.</blockquote>"
                : "\n\n<blockquote>Выбранный чат закрепится за этой темой.</blockquote>");
        var buttons = threads.Select((t, i) => new[] { InlineKeyboardButton.WithCallbackData($"{i + 1:00}  {Clip(t.Title.Replace('\n', ' '), 48)}", $"b:{key}:{i}") }).ToList();
        if (next.Length > 0) buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Показать ещё", $"p:{key}") });
        if (address.IsGeneral) buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("В меню", "g:h") });
        var markup = new InlineKeyboardMarkup(buttons);
        if (editMessageId is int editId)
            await _bot.EditMessageText(address.ChatId, editId, text,
                parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
        else
            await _bot.SendMessage(address.ChatId, text,
                messageThreadId: address.IsGeneral ? null : address.TopicId,
                parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
    }

    private async Task<string> Bind(TopicAddress address, string project, string threadId, CancellationToken ct)
    {
        if (address.IsGeneral) return "General используется только для /link и /new.";
        if (_store.State.Topics.ContainsKey(address.Key)) return "Этот topic уже привязан. /status";
        if (!_options.Projects.TryGetValue(project, out var path)) return "Неизвестный проект. /projects";
        if (!Guid.TryParse(threadId, out _)) return "Неверный thread ID.";
        if (IsBound(threadId))
            return "Этот thread уже привязан к другому topic.";
        var result = await _server!.CallAsync("thread/read", new { threadId, includeTurns = false }, ct);
        var thread = result.GetProperty("thread");
        var actual = GetString(thread, "cwd");
        if (!Path.IsPathFullyQualified(actual) || !IsWithinProject(actual, path))
            return $"Thread относится к другому каталогу: {actual}";
        var load = await LoadThread(actual, threadId, ct);
        if (load.Error is { } loadError) return loadError;
        _store.State.Topics[address.Key] = new TopicBinding(project, GetString(thread, "id"), actual);
        await _store.SaveAsync();
        return $"Topic закреплён за проектом {project}\nThread: {threadId}\nПоследний ответ Codex:\n{Clip(await LastAnswer(threadId, ct), 3000)}";
    }

    private async Task<string> LinkThreadTopic(long chatId, string project, ThreadChoice choice, CancellationToken ct)
    {
        if (IsBound(choice.Id)) return "Этот thread уже привязан к другому topic.";
        await EnsureCanManageTopics(chatId, ct);
        var result = await _server!.CallAsync("thread/read", new { threadId = choice.Id, includeTurns = false }, ct);
        var thread = result.GetProperty("thread");
        var path = GetString(thread, "cwd");
        if (!Path.IsPathFullyQualified(path) || !IsWithinProject(path, ProjectRoot(project)) ||
            (project.Length == 0 && _options.Projects.Values.Any(root => IsWithinProject(path, root))))
            return "Thread больше не относится к выбранному проекту.";
        var load = await LoadThread(path, choice.Id, ct);
        if (load.Error is { } loadError)
            return loadError + "\nПоследний ответ Codex:\n" +
                Clip(await LastAnswer(choice.Id, ct), 3000);
        var title = GetString(thread, "name");
        if (title.Length == 0) title = GetString(thread, "preview");
        if (title.Length == 0) title = choice.Title;
        var lastAnswer = await LastAnswer(choice.Id, ct);
        var created = await CreateTopicAndBind(chatId, project, choice.Id, title, path, ct,
            $"Последний ответ Codex:\n{Clip(lastAnswer, 3000)}");
        return created.Summary;
    }

    private async Task ForkThreadTopic(TopicAddress sourceAddress, TopicBinding binding, CancellationToken ct)
    {
        if (!TryProjectRoot(binding.Project, out var projectPath))
        {
            await Send(sourceAddress, "Проект удалён из appsettings.json: " + binding.Project, ct);
            return;
        }
        await EnsureCanManageTopics(sourceAddress.ChatId, ct);
        var read = await _server!.CallAsync("thread/read",
            new { threadId = binding.ThreadId, includeTurns = false }, ct);
        var source = read.GetProperty("thread");
        var path = GetString(source, "cwd");
        if (!Path.IsPathFullyQualified(path) || !IsWithinProject(path, projectPath))
        {
            await Send(sourceAddress, "Каталог исходного thread не совпадает с проектом.", ct);
            return;
        }
        var turns = await RecentTurns(binding.ThreadId, ct);
        var lastTurnId = turns.Reverse()
            .Where(t => GetString(t, "status") is "completed" or "failed" or "interrupted")
            .Select(t => GetString(t, "id"))
            .FirstOrDefault(id => id.Length > 0);
        if (turns.Length > 0 && lastTurnId == null)
        {
            await Send(sourceAddress, "В thread пока нет завершённого turn для ветвления.", ct);
            return;
        }
        object forkParams = lastTurnId == null
            ? new { threadId = binding.ThreadId, cwd = path,
                approvalPolicy = "on-request", sandbox = "workspace-write" }
            : new { threadId = binding.ThreadId, lastTurnId, cwd = path,
                approvalPolicy = "on-request", sandbox = "workspace-write" };
        var forked = await _server.CallAsync("thread/fork", forkParams, ct);
        var id = forked.GetProperty("thread").GetProperty("id").GetString()!;
        _loadedThreads.Add(id);
        var title = GetString(source, "name");
        if (title.Length == 0) title = GetString(source, "preview");
        if (title.Length == 0) title = ProjectLabel(binding.Project);
        TopicCreation created;
        try
        {
            created = await CreateTopicAndBind(sourceAddress.ChatId, binding.Project, id,
                title + " · ветка", path, ct,
                $"Ветка от thread: {binding.ThreadId}\nПоследний ответ Codex до ветвления:\n{Clip(DescribeLastAnswer(turns), 2800)}");
        }
        catch (Exception ex)
        {
            await Send(sourceAddress, $"Создан Codex thread: {id}\nTopic создать не удалось: {ex.Message}\nПривязка не сохранена. После исправления ошибки используйте /link{(binding.Project.Length == 0 ? "" : " " + binding.Project)} в General.", ct);
            return;
        }
        int moved;
        try { moved = await TransferPendingTasks(sourceAddress, created.Address, ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not transfer all pending tasks to forked topic {TopicId}", created.Address.TopicId);
            await Send(sourceAddress, created.Summary +
                "\nЧасть ожидающих задач не удалось перенести. Проверьте статусы в обоих topics.", ct);
            return;
        }
        await Send(sourceAddress, created.Summary +
            (moved == 0 ? "" : $"\nПеренесено ожидающих задач: {moved}"), ct);
        if (moved > 0) await StartQueued(ct);
    }

    private async Task<string> NewThreadTopic(long chatId, string project, string directory,
        string name, CancellationToken ct)
    {
        await EnsureCanManageTopics(chatId, ct);
        var started = await _server!.CallAsync("thread/start",
            new { cwd = directory, approvalPolicy = "on-request", sandbox = "workspace-write" }, ct);
        var id = started.GetProperty("thread").GetProperty("id").GetString()!;
        _loadedThreads.Add(id);
        string? pendingName = null;
        try
        {
            try { await _server.CallAsync("thread/name/set", new { threadId = id, name }, ct); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not name newly started thread {ThreadId}; will retry after first turn", id);
                pendingName = name;
            }
            var created = await CreateTopicAndBind(chatId, project, id, name, directory, ct,
                pendingName: pendingName);
            return created.Summary + (pendingName == null ? "" :
                "\nНазвание Codex будет применено после первого ответа.");
        }
        catch (Exception ex)
        {
            return $"Codex thread создан: {id}\nTopic или привязку создать не удалось: {ex.Message}\nПривязка не сохранена. После исправления ошибки используйте /link{(project.Length == 0 ? "" : " " + project)}.";
        }
    }

    private async Task<TopicCreation> CreateTopicAndBind(long chatId, string project, string threadId,
        string title, string directory, CancellationToken ct, string? historyNote = null,
        string? pendingName = null)
    {
        if (IsBound(threadId)) throw new InvalidOperationException("Этот thread уже привязан к другому topic.");
        await EnsureCanManageTopics(chatId, ct);
        var topicName = TopicName(title, project, threadId);
        var topic = await _bot.CreateForumTopic(chatId, topicName, cancellationToken: ct);
        if (topic.MessageThreadId <= 1)
            throw new InvalidOperationException("Telegram вернул неверный ID нового topic.");
        var address = new TopicAddress(chatId, topic.MessageThreadId);
        _store.State.Topics[address.Key] = new TopicBinding(project, threadId, directory, pendingName);
        try { await _store.SaveAsync(); }
        catch
        {
            _store.State.Topics.Remove(address.Key);
            throw;
        }
        string? welcomeError = null;
        try
        {
            await SendUi(address, $"<b>{H(topicName)}</b>\n" +
                $"<i>Подключён к Codex · {H(ProjectLabel(project))}</i>\n\n" +
                "<blockquote>Напишите задачу обычным сообщением. Этот чат продолжится здесь.</blockquote>" +
                (historyNote == null ? "" : "\n\n<b>Контекст чата</b>\n" + AnswerBlock(historyNote, 2100)) +
                "\n\n<blockquote expandable><b>Привязка</b>\n" +
                $"Thread: {H(threadId)}\nКаталог: {H(directory)}</blockquote>",
                ct, TopicButtons());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not send binding status to topic {TopicId}", address.TopicId);
            welcomeError = "\nНе удалось отправить стартовое сообщение: " + ex.Message;
        }
        return new TopicCreation(address,
            $"Создан topic «{topicName}»\nПроект: {ProjectLabel(project)}\nКаталог: {directory}\nThread: {threadId}{welcomeError}");
    }

    private async Task<int> TransferPendingTasks(TopicAddress source, TopicAddress destination, CancellationToken ct)
    {
        var moved = 0;
        for (var i = 0; i < _store.State.PendingTasks.Count; i++)
        {
            var pending = _store.State.PendingTasks[i];
            if (pending.ChatId != source.ChatId || pending.TopicId != source.TopicId) continue;
            var status = await _bot.SendMessage(destination.ChatId,
                TaskStatusMarkup("Перенесено из исходного чата. Ожидает запуска."),
                messageThreadId: destination.TopicId, parseMode: ParseMode.Html, cancellationToken: ct);
            _store.State.PendingTasks[i] = new PendingTask(destination.ChatId, destination.TopicId,
                pending.Text, status.MessageId);
            await _store.SaveAsync();
            moved++;
            if (pending.StatusMessageId != 0)
                try
                {
                    await _bot.EditMessageText(source.ChatId, pending.StatusMessageId,
                        TaskStatusMarkup("Задача перенесена в новую ветку."),
                        parseMode: ParseMode.Html, cancellationToken: ct);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Could not update transferred task status"); }
        }
        return moved;
    }

    private async Task EnsureCanManageTopics(long chatId, CancellationToken ct)
    {
        var me = await _bot.GetMe(cancellationToken: ct);
        var member = await _bot.GetChatMember(chatId, me.Id, cancellationToken: ct);
        if (member is ChatMemberOwner or ChatMemberAdministrator { CanManageTopics: true }) return;
        throw new InvalidOperationException("Боту нужны права администратора can_manage_topics в этой супергруппе.");
    }

    private bool IsBound(string threadId) => _store.State.Topics.Values.Any(b =>
        string.Equals(b.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));

    private async Task<JsonElement[]> RecentTurns(string threadId, CancellationToken ct)
    {
        var result = await _server!.CallAsync("thread/read",
            new { threadId, includeTurns = true }, ct);
        var thread = result.GetProperty("thread");
        return thread.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array
            ? turns.EnumerateArray().Select(t => t.Clone()).ToArray()
            : Array.Empty<JsonElement>();
    }

    private async Task<string> LastAnswer(string threadId, CancellationToken ct)
    {
        try { return DescribeLastAnswer(await RecentTurns(threadId, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read last answer for thread {ThreadId}", threadId);
            return "не удалось прочитать историю: " + ex.Message;
        }
    }

    private async Task SendLastAnswer(TopicAddress address, string threadId, CancellationToken ct)
    {
        var answer = await LastAnswer(threadId, ct);
        var markup = "<b>Последний ответ</b>\n<i>Codex · завершённая задача</i>\n\n" +
            TelegramMarkup.RenderAnswer(answer);
        if (markup.Length <= 3900)
        {
            await SendUi(address, markup, ct);
            return;
        }
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(answer));
        await _bot.SendDocument(address.ChatId, InputFile.FromStream(stream, "last-answer.txt"),
            caption: "Последний ответ Codex", messageThreadId: address.TopicId,
            cancellationToken: ct);
    }

    private static string DescribeLastAnswer(JsonElement[] turns)
    {
        foreach (var turn in turns.Reverse())
        {
            if (GetString(turn, "status") != "completed") continue;
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var messages = items.EnumerateArray()
                .Where(item => GetString(item, "type") == "agentMessage" &&
                    !string.IsNullOrWhiteSpace(GetString(item, "text")))
                .ToArray();
            var final = messages.LastOrDefault(item => GetString(item, "phase") == "final_answer");
            if (final.ValueKind == JsonValueKind.Object) return GetString(final, "text").Trim();
            if (messages.Length > 0) return GetString(messages[^1], "text").Trim();
        }
        return "завершённых ответов пока нет";
    }

    private static string CleanTitle(string title) =>
        string.Join(' ', new string(title.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string TopicName(string title, string project, string threadId)
    {
        var cleaned = CleanTitle(title);
        if (cleaned.Length == 0) cleaned = $"{ProjectLabel(project)} · {threadId[..8]}";
        if (cleaned.Length <= 128) return cleaned;
        var length = char.IsHighSurrogate(cleaned[126]) ? 126 : 127;
        return cleaned[..length] + "…";
    }

    private async Task HandleCallback(CallbackQuery callback, TopicAddress address, CancellationToken ct)
    {
        var parts = callback.Data?.Split(':');
        if (parts is { Length: >= 2 } && parts[0] == "g")
        {
            await HandleGeneralCallback(callback, address, parts, ct);
            return;
        }
        if (parts is { Length: 2 } && parts[0] == "w")
        {
            await HandleNewDraftCallback(callback, address, parts[1], ct);
            return;
        }
        if (parts is { Length: 2 } && parts[0] == "t")
        {
            await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            if (!_store.State.Topics.TryGetValue(address.Key, out var binding))
            {
                await Send(address, "Этот topic не привязан к Codex thread.", ct);
                return;
            }
            if (parts[1] == "l") await SendLastAnswer(address, binding.ThreadId, ct);
            else if (parts[1] == "f")
            {
                if (_run?.Address == address)
                    await Send(address, "Дождитесь завершения текущей задачи и повторите.", ct);
                else await ForkThreadTopic(address, binding, ct);
            }
            return;
        }
        if (parts?.Length == 3 && parts[0] == "a")
        {
            await HandleApproval(callback, address, parts, ct);
            return;
        }
        if (parts is { Length: >= 2 } && _menus.TryGetValue(parts[1], out var menu) && menu.Address == address &&
            !_store.State.Topics.ContainsKey(address.Key))
        {
            if (parts[0] == "b" && parts.Length == 3 && int.TryParse(parts[2], out var index) &&
                index >= 0 && index < menu.Threads.Count)
            {
                var result = menu.CreateTopic
                    ? await LinkThreadTopic(address.ChatId, menu.Project, menu.Threads[index], ct)
                    : await Bind(address, menu.Project, menu.Threads[index].Id, ct);
                await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
                await _bot.EditMessageText(address.ChatId, callback.Message!.MessageId,
                    "<b>Привязка</b>\n" + H(result), parseMode: ParseMode.Html,
                    replyMarkup: address.IsGeneral ? GeneralBackButton() : null, cancellationToken: ct);
                _menus.Remove(parts[1]);
                return;
            }
            if (parts[0] == "p" && menu.NextCursor.Length > 0)
            {
                await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
                await ShowThreads(address, menu.Project, menu.NextCursor, callback.Message!.MessageId,
                    menu.CreateTopic, ct);
                _menus.Remove(parts[1]);
                return;
            }
        }
        await _bot.AnswerCallbackQuery(callback.Id, "Кнопка устарела.", cancellationToken: ct);
    }

    private async Task<bool> StartTask(TopicAddress address, string text, CancellationToken ct,
        int statusMessageId = 0, PendingTask? queued = null)
    {
        if (!_store.State.Topics.TryGetValue(address.Key, out var binding))
        {
            await TaskStatus(address, statusMessageId, "Topic не привязан. /threads <проект>", ct);
            return true;
        }
        if (_run != null)
        {
            statusMessageId = await TaskStatus(address, statusMessageId, "В очереди", ct);
            _store.State.PendingTasks.Add(new PendingTask(address.ChatId, address.TopicId, text, statusMessageId));
            await _store.SaveAsync();
            return false;
        }
        if (!TryProjectRoot(binding.Project, out var path))
        {
            await TaskStatus(address, statusMessageId, "Проект удалён из appsettings.json: " + binding.Project, ct);
            return true;
        }
        var directory = binding.WorkingDirectory ?? path;
        if (!IsWithinProject(directory, path) || !Directory.Exists(directory))
        {
            await TaskStatus(address, statusMessageId, "Рабочий каталог недоступен: " + directory, ct);
            return true;
        }
        var load = await LoadThread(directory, binding.ThreadId, ct);
        if (load.Busy)
        {
            if (queued == null || statusMessageId == 0)
                statusMessageId = await TaskStatus(address, statusMessageId,
                    "Thread открыт в Codex Desktop. Задача сохранена и запустится после закрытия Desktop. /fork — продолжить в новой ветке.", ct);
            if (queued == null)
            {
                _store.State.PendingTasks.Add(new PendingTask(address.ChatId, address.TopicId, text, statusMessageId));
                await _store.SaveAsync();
            }
            else if (queued.StatusMessageId == 0)
            {
                var index = _store.State.PendingTasks.IndexOf(queued);
                if (index >= 0)
                {
                    _store.State.PendingTasks[index] = queued with { StatusMessageId = statusMessageId };
                    await _store.SaveAsync();
                }
            }
            return false;
        }
        if (load.Error is { } loadError)
        {
            await TaskStatus(address, statusMessageId, loadError, ct);
            return true;
        }
        var run = new Run(address, binding.Project, binding.ThreadId);
        _run = run;
        try
        {
            run.StatusMessageId = await TaskStatus(address, statusMessageId, "Выполняется", ct);
            run.StatusText = TaskStatusMarkup("Выполняется");
            var result = await _server!.CallAsync("turn/start", new { threadId = binding.ThreadId,
                input = new[] { new { type = "text", text } } }, ct);
            run.TurnId = result.GetProperty("turn").GetProperty("id").GetString();
            return true;
        }
        catch (Exception ex)
        {
            await SetStatus(run, "Не удалось запустить", ct, force: true);
            _run = null;
            await Send(address, "Ошибка: " + ex.Message, ct);
            return true;
        }
    }

    private async Task<int> TaskStatus(TopicAddress address, int messageId, string status, CancellationToken ct)
    {
        var markup = TaskStatusMarkup(status);
        if (messageId != 0)
        {
            try { await _bot.EditMessageText(address.ChatId, messageId, markup,
                parseMode: ParseMode.Html, cancellationToken: ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not edit task status"); }
            return messageId;
        }
        var message = await _bot.SendMessage(address.ChatId, markup,
            messageThreadId: address.TopicId, parseMode: ParseMode.Html, cancellationToken: ct);
        return message.MessageId;
    }

    private async Task ProcessEvents(CancellationToken ct)
    {
        await foreach (var msg in _events.Reader.ReadAllAsync(ct))
        {
            await _gate.WaitAsync(ct);
            try { await HandleEvent(msg, ct); }
            catch (Exception ex) { _log.LogError(ex, "Codex event handling failed"); }
            finally { _gate.Release(); }
        }
    }

    private async Task HandleEvent(JsonElement msg, CancellationToken ct)
    {
        if (!msg.TryGetProperty("method", out var methodElement) ||
            !msg.TryGetProperty("params", out var p)) return;
        var method = methodElement.GetString();
        if (msg.TryGetProperty("id", out var requestId))
        {
            if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval")
                await RequestApproval(requestId, method!, p, ct);
            else await _server!.RejectAsync(requestId, "CodexRelay does not support this request", ct);
            return;
        }
        if (_run == null || !Matches(_run, p)) return;
        var run = _run;
        if (run.Stopping && method != "turn/completed") return;
        switch (method)
        {
            case "turn/started":
                run.TurnId = p.GetProperty("turn").GetProperty("id").GetString();
                break;
            case "item/started":
                var item = p.GetProperty("item");
                if (GetString(item, "type") == "commandExecution")
                    await SetStatus(run, "Выполняю команду", ct);
                else if (GetString(item, "type") == "fileChange")
                    await SetStatus(run, "Изменяю файлы", ct);
                break;
            case "item/completed":
                item = p.GetProperty("item");
                var type = GetString(item, "type");
                if (type == "agentMessage" &&
                    GetString(item, "phase") is "final_answer" or "")
                    run.Answer = GetString(item, "text");
                else if (type == "commandExecution")
                {
                    var command = GetString(item, "command");
                    if (IsTestCommand(command))
                        run.Tests.Add($"{command}: exit {GetString(item, "exitCode")}");
                }
                else if (type == "fileChange" && item.TryGetProperty("changes", out var changes))
                    foreach (var change in changes.EnumerateArray()) run.Files.Add(GetString(change, "path"));
                break;
            case "error":
                if (p.TryGetProperty("error", out var error))
                {
                    run.Error = GetString(error, "message");
                    await SetStatus(run, "Ошибка: " + run.Error, ct, force: true);
                }
                break;
            case "turn/completed":
                var turn = p.GetProperty("turn");
                _run = null;
                try
                {
                    await Complete(run, GetString(turn, "status"),
                        turn.TryGetProperty("error", out var err) ? GetString(err, "message") : run.Error, ct);
                    if (_store.State.Topics.TryGetValue(run.Address.Key, out var finishedBinding) &&
                        finishedBinding.PendingName is { } name)
                    {
                        try
                        {
                            await _server!.CallAsync("thread/name/set", new { threadId = run.ThreadId, name }, ct);
                            _store.State.Topics[run.Address.Key] = finishedBinding with { PendingName = null };
                            await _store.SaveAsync();
                        }
                        catch (Exception ex) { _log.LogWarning(ex, "Could not name thread {ThreadId}", run.ThreadId); }
                    }
                }
                finally { await StartQueued(ct); }
                break;
        }
    }

    private async Task<bool> SetStatus(Run run, string progress, CancellationToken ct, bool force = false)
    {
        if (!force && DateTime.UtcNow - run.LastStatusAt < TimeSpan.FromSeconds(8)) return true;
        if (run.StatusMessageId == 0) return false;
        var pending = _approvals.Where(x => x.Value.ThreadId == run.ThreadId).ToArray();
        var text = TaskStatusMarkup(Clip(progress, 600));
        if (pending.Length > 0)
            text += "\n\n<b>Нужно подтверждение</b>\n<blockquote expandable>" +
                string.Join("\n\n", pending.Take(4).Select(x => H(Clip(x.Value.Detail, 400)))) +
                "</blockquote>";
        if (text == run.StatusText && !force) return true;
        var buttons = pending.Select(x => new[]
        {
            InlineKeyboardButton.WithCallbackData("Разрешить", $"a:{x.Key}:y"),
            InlineKeyboardButton.WithCallbackData("Отклонить", $"a:{x.Key}:n")
        }).ToArray();
        try
        {
            await _bot.EditMessageText(run.Address.ChatId, run.StatusMessageId, text,
                parseMode: ParseMode.Html,
                replyMarkup: buttons.Length == 0 ? null : new InlineKeyboardMarkup(buttons), cancellationToken: ct);
            run.StatusText = text;
            run.LastStatusAt = DateTime.UtcNow;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "Could not update topic status"); return false; }
    }

    private async Task Complete(Run run, string status, string error, CancellationToken ct)
    {
        await SetStatus(run, status == "completed" ? "Готово" : "Завершено: " + status, ct, force: true);
        var answer = string.IsNullOrWhiteSpace(run.Answer) ? "(нет ответа)" : run.Answer;
        var title = status == "completed" ? "Готово" : "Завершено: " + status;
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(error)) details.Add("Ошибка: " + error);
        if (run.Tests.Count > 0) details.Add("Проверки: " + string.Join("; ", run.Tests));
        if (run.Files.Count > 0) details.Add("Файлы: " + string.Join(", ", run.Files.Order()));
        var markup = $"<b>{H(title)}</b>\n<i>Ответ Codex</i>\n\n" +
            TelegramMarkup.RenderAnswer(answer);
        if (details.Count > 0)
            markup += "\n\n<blockquote expandable><b>Детали выполнения</b>\n" +
                H(string.Join("\n", details)) + "</blockquote>";
        if (markup.Length <= 3900) await SendUi(run.Address, markup, ct);
        else
        {
            var document = title + "\n\n" + answer +
                (details.Count == 0 ? "" : "\n\n" + string.Join("\n", details));
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
            await _bot.SendDocument(run.Address.ChatId, InputFile.FromStream(stream, "answer.txt"),
                caption: title, messageThreadId: run.Address.TopicId, cancellationToken: ct);
        }
    }

    private async Task RequestApproval(JsonElement id, string method, JsonElement p, CancellationToken ct)
    {
        if (_run == null || _run.Stopping || !Matches(_run, p))
        {
            if (method == "item/permissions/requestApproval")
                await _server!.RespondAsync(id, new { permissions = new { }, scope = "turn" }, ct);
            else await _server!.RespondAsync(id, new { decision = "decline" }, ct);
            return;
        }
        var key = Guid.NewGuid().ToString("N")[..12];
        var detail = method.Contains("commandExecution")
            ? $"Команда: {GetString(p, "command")}\nКаталог: {GetString(p, "cwd")}"
            : method.Contains("permissions")
                ? $"Разрешения: {p.GetProperty("permissions")}\nПричина: {GetString(p, "reason")}"
                : $"Изменение файлов. {GetString(p, "reason")}\nRoot: {GetString(p, "grantRoot")}";
        _approvals[key] = new Approval(id.Clone(), method, _run.ThreadId, _run.TurnId ?? "", detail,
            p.TryGetProperty("permissions", out var requested) ? requested.Clone() : default);
        if (!await SetStatus(_run, "Ожидание подтверждения", ct, force: true))
        {
            _approvals.Remove(key);
            if (method == "item/permissions/requestApproval")
                await _server!.RespondAsync(id, new { permissions = new { }, scope = "turn" }, ct);
            else await _server!.RespondAsync(id, new { decision = "decline" }, ct);
        }
    }

    private async Task HandleApproval(CallbackQuery callback, TopicAddress address, string[] parts, CancellationToken ct)
    {
        if (!_approvals.TryGetValue(parts[1], out var approval) || _run?.Address != address ||
            callback.Message?.MessageId != _run.StatusMessageId || approval.ThreadId != _run.ThreadId)
        {
            await _bot.AnswerCallbackQuery(callback.Id, "Запрос уже закрыт.", cancellationToken: ct);
            return;
        }
        _approvals.Remove(parts[1]);
        var accepted = parts[2] == "y" && !_run.Stopping &&
            (approval.TurnId.Length == 0 || _run.TurnId == approval.TurnId);
        if (approval.Method == "item/permissions/requestApproval")
            await _server!.RespondAsync(approval.Id,
                new { permissions = accepted ? (object)approval.Permissions : new { }, scope = "turn" }, ct);
        else await _server!.RespondAsync(approval.Id, new { decision = accepted ? "accept" : "decline" }, ct);
        await _bot.AnswerCallbackQuery(callback.Id, accepted ? "Разрешено" : "Отклонено", cancellationToken: ct);
        await SetStatus(_run, "Выполняется", ct, force: true);
    }

    private async Task Send(TopicAddress address, string text, CancellationToken ct) =>
        await _bot.SendMessage(address.ChatId, Clip(text, 3900),
            messageThreadId: address.IsGeneral ? null : address.TopicId, cancellationToken: ct);

    private async Task SendUi(TopicAddress address, string markup, CancellationToken ct,
        InlineKeyboardMarkup? buttons = null) =>
        await _bot.SendMessage(address.ChatId, markup,
            messageThreadId: address.IsGeneral ? null : address.TopicId,
            parseMode: ParseMode.Html, replyMarkup: buttons, cancellationToken: ct);

    private static string H(string value) => WebUtility.HtmlEncode(value);

    private static string AnswerBlock(string answer, int max) =>
        "<blockquote expandable>" + H(Clip(answer, max)) + "</blockquote>";

    private static string TaskStatusMarkup(string status) =>
        "<b>Текущая задача</b>\n<i>Codex</i>\n\n<blockquote>" + H(status) + "</blockquote>";

    private async Task<(string? Error, bool Busy)> LoadThread(string directory, string threadId, CancellationToken ct)
    {
        if (_loadedThreads.Contains(threadId)) return (null, false);
        try
        {
            await _server!.CallAsync("thread/resume", new { threadId, cwd = directory,
                approvalPolicy = "on-request", sandbox = "workspace-write" }, ct);
            _loadedThreads.Add(threadId);
            return (null, false);
        }
        catch (Exception ex) when (ex.Message.Contains("active writer", StringComparison.OrdinalIgnoreCase))
        {
            return ("Этот Codex thread уже открыт другим процессом. Закройте Codex Desktop: " +
                "одновременно писать в один thread из Desktop и бота нельзя.", true);
        }
        catch (Exception ex) { return ("Не удалось открыть thread: " + ex.Message, false); }
    }

    private static bool TryAddress(Message message, out TopicAddress address)
    {
        address = default;
        if (message.Chat.Type != ChatType.Supergroup || message.Chat.IsForum != true) return false;
        address = new TopicAddress(message.Chat.Id, message.MessageThreadId ?? 1);
        return true;
    }

    private async Task StartQueued(CancellationToken ct)
    {
        _lastQueueRetryAt = DateTime.UtcNow;
        for (var i = 0; _run == null && i < _store.State.PendingTasks.Count;)
        {
            var pending = _store.State.PendingTasks[i];
            if (await StartTask(new TopicAddress(pending.ChatId, pending.TopicId), pending.Text, ct,
                pending.StatusMessageId, pending))
            {
                _store.State.PendingTasks.RemoveAt(i);
                await _store.SaveAsync();
            }
            else i++;
        }
    }

    private static bool Matches(Run run, JsonElement p) =>
        GetString(p, "threadId") == run.ThreadId &&
        (run.TurnId == null || !p.TryGetProperty("turnId", out var turn) || turn.GetString() == run.TurnId);

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string ProjectLabel(string project) => project.Length == 0 ? "Без проекта" : project;

    private bool TryProjectRoot(string project, out string path)
    {
        if (project.Length == 0)
        {
            path = _options.ProjectlessChatRoot;
            return true;
        }
        return _options.Projects.TryGetValue(project, out path!);
    }

    private string ProjectRoot(string project) => TryProjectRoot(project, out var path)
        ? Path.GetFullPath(path)
        : throw new InvalidOperationException("Неизвестный проект: " + project);

    private static bool IsWithinProject(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(basePath, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestCommand(string command) =>
        new[] { "dotnet test", "pytest", "unittest", "npm test", "npm run test", "pnpm test", "yarn test", "cargo test", "go test" }
            .Any(x => command.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : "";

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (_server != null) await _server.DisposeAsync();
        _gate.Dispose();
    }

    private readonly record struct TopicAddress(long ChatId, int TopicId)
    {
        public string Key => $"{ChatId}:{TopicId}";
        public bool IsGeneral => TopicId == 1;
    }
    private sealed record ThreadChoice(string Id, string Title);
    private sealed record TopicCreation(TopicAddress Address, string Summary);
    private sealed record ThreadMenu(TopicAddress Address, string Project, List<ThreadChoice> Threads,
        string NextCursor, bool CreateTopic);
    private sealed class NewDraft(string project, string? directory)
    {
        public string Project { get; } = project;
        public string? Directory { get; set; } = directory;
        public bool AutoDirectory { get; set; }
        public string? Name { get; set; }
        public string? Awaiting { get; set; }
        public int MessageId { get; set; }
    }
    private sealed record Approval(JsonElement Id, string Method, string ThreadId, string TurnId,
        string Detail, JsonElement Permissions);
    private sealed class Run(TopicAddress address, string project, string threadId)
    {
        public TopicAddress Address { get; } = address;
        public string Project { get; } = project;
        public string ThreadId { get; } = threadId;
        public string? TurnId { get; set; }
        public int StatusMessageId { get; set; }
        public string StatusText { get; set; } = "";
        public DateTime LastStatusAt { get; set; }
        public bool Stopping { get; set; }
        public string Answer { get; set; } = "";
        public string Error { get; set; } = "";
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Tests { get; } = new();
    }
}
