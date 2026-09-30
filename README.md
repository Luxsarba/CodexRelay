# CodexRelay

Telegram-бот для локального Codex App Server на Windows. .NET 8, Telegram.Bot 22.7.2, long polling. Использует тот же codex.exe, который установлен с Codex Desktop; создаёт отдельный процесс с транспортом stdio://. Входящие порты не открываются. General — панель управления; каждый рабочий Telegram topic закреплён за одним Codex thread. Проект можно выбрать или не указывать.

## Быстрый старт

Нужны Windows, .NET 8 SDK, установленный Codex Desktop с входом в аккаунт и Telegram-бот. Приложение использует локальный Codex App Server через stdio.

1. Создайте бота через BotFather и узнайте свой числовой Telegram user ID. Создайте приватную супергруппу с включёнными Topics. Дайте боту права администратора **Manage Topics** (`can_manage_topics`) и отключите Privacy Mode, чтобы он видел обычные сообщения.
2. Склонируйте проект и откройте каталог:

```powershell
git clone https://github.com/Luxsarba/CodexRelay.git
cd CodexRelay
```

3. В `appsettings.json` замените пути `S:\Projects\Codex\...` на существующие абсолютные пути своего компьютера. `Relay:ProjectCreationRoot` — каталог, внутри которого бот создаёт новые проекты; каталог должен существовать. `Relay:Projects` — уже существующие проекты. Если их пока нет, укажите `"Projects": {}`. Чаты без проекта используют папку `Документы\Codex`; её можно изменить через `Relay:ProjectlessChatRoot`.
4. Создайте локальный файл с секретами и запустите бота:

```powershell
Copy-Item .env.example .env
notepad .env
dotnet run
```

В `.env` заполните `Relay__BotToken` и `Relay__OwnerUserId`. Этот файл исключён из Git. При запуске бот читает установку Codex Desktop; при необходимости задайте `Relay__CodexExecutable` полным путём к `codex.exe`.

## Работа

В **General** напишите `/link`, чтобы выбрать существующий Desktop thread, или `/new`, чтобы создать новый. Бот предложит выбрать проект, создать проект либо работать без проекта. Новый проект получает каталог с латинским именем в `Relay:ProjectCreationRoot`; название проекта в Telegram может быть на любом языке. Для каждого следующего чата существующего проекта бот по умолчанию создаёт отдельный подкаталог с латинским именем. Чат без проекта получает отдельный каталог `Документы\Codex\<дата>\new-chat` (или `new-chat-2` и далее), как у локальных чатов Codex Desktop. Корень можно изменить через `Relay:ProjectlessChatRoot`. Каталог можно изменить кнопкой «Каталог». Затем задайте название чата и нажмите «Создать». В рабочем topic отправляйте задачи обычным текстом.

В General доступны inline кнопки для выбора проекта и чата, состояния бота и лимитов Codex. Главный экран показывает число рабочих тем и очередь; выбор проекта и настройка чата оформлены как два шага. Кнопка «Создать чат» появляется только после заполнения названия и каталога. «Работа бота» показывает число проектов и рабочих topics, активную задачу и длину очереди. `/usage` показывает лимиты аккаунта с текстовой шкалой остатка и временем сброса, а также статистику токенов. В рабочем topic кнопки открывают последний ответ или создают ветку. Интерфейс использует единые заголовки, короткие пояснения, раскрываемые сведения о привязке и кнопки без эмоджи; статус задачи редактируется в одном сообщении. Финальные ответы и `/last` отображают заголовки, списки, код, жирный текст и веб-ссылки из Markdown Codex с безопасным HTML-экранированием. Длинные ответы приходят файлом.

При привязке бот показывает последний итоговый ответ Codex из завершённого turn. В рабочем topic `/status` показывает его начало, а `/last` — полный ответ (длинный ответ приходит файлом). Команды, изменения файлов и промежуточные сообщения не подменяют итоговый ответ. Если Desktop занял thread, команда `/fork` в этом topic создаёт новый Codex thread с историей до последнего завершённого turn и отдельный Telegram topic. Ожидающие задачи переносятся в новую ветку; исходная привязка сохраняется. Незавершённый turn в ветку не попадает.

Codex допускает одного записывающего владельца thread. Если выбранный thread открыт в Codex Desktop, запустите бота из отдельного PowerShell или Планировщика заданий и закройте Desktop перед `/link`: отдельный App Server бота не сможет выполнить `thread/resume`, пока Desktop владеет thread. Бот проверяет это до создания Telegram topic. Если topic уже привязан, новая задача сохраняется в очереди и запускается после закрытия Desktop; бот повторяет попытку примерно раз в 30 секунд. Для перехода обратно остановите бота и откройте thread в Desktop.

Переменные окружения Windows имеют приоритет над `.env`. Для постоянного запуска положите `.env` рядом с опубликованным exe и запустите его через Планировщик заданий. Боту нужен доступ к интернету для исходящих запросов к Telegram и Codex.

В Windows можно зарегистрировать запуск при входе пользователя (из PowerShell):

```powershell
$projectDir = (Get-Location).Path
dotnet build
$launcher = Join-Path $projectDir 'run-relay-hidden.vbs'
$action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\wscript.exe') -Argument ('//B //Nologo "' + $launcher + '"') -WorkingDirectory $projectDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$watchTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName 'CodexRelay' -Action $action -Trigger @($trigger, $watchTrigger) -Principal $principal -Settings $settings -Force
Start-ScheduledTask -TaskName 'CodexRelay'

dotnet build .\Tray\CodexRelay.Tray.csproj
$trayExe = Join-Path $projectDir 'Tray\bin\Debug\net8.0-windows\CodexRelay.Tray.exe'
$trayAction = New-ScheduledTaskAction -Execute $trayExe -Argument ('--root "' + $projectDir + '"')
$trayTrigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$traySettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'CodexRelayTray' -Action $trayAction -Trigger $trayTrigger -Principal $principal -Settings $traySettings -Force
Start-ScheduledTask -TaskName 'CodexRelayTray'
```

Планировщик запускает бота без окна PowerShell через run-relay-hidden.vbs при входе и повторяет запуск каждые 5 минут, если процесс завершился. Пока бот работает, повторный запуск игнорируется. При сбое Планировщик также делает до 10 попыток с интервалом в минуту. Бот продолжает работать при переходе на батарею и после блокировки экрана. Задание использует интерактивный профиль Windows для доступа к Codex и диску S:; после выхода из учётной записи оно возобновится при следующем входе.

В системном трее появляется значок Codex Relay: зелёный — бот получает ответы Telegram, жёлтый — запускается или потерял связь с Telegram, красный — остановлен. Через меню значка можно запустить или перезапустить бота и открыть журнал. Состояние определяется по сигналу от работающего процесса; зелёный значок подтверждает связь с Telegram, но не доступность сервиса Codex для каждой задачи.

Лог: `logs/relay.log`. Проверка: `Get-ScheduledTask -TaskName CodexRelay`. Остановка: `Stop-ScheduledTask -TaskName CodexRelay`. Периодический триггер запустит бот снова; для постоянной остановки отключите задание командой Disable-ScheduledTask -TaskName CodexRelay.

Проверка App Server без токена:

```powershell
dotnet run --project .\CodexRelay.csproj -- --check-app-server
```

Если поиск бинарника Desktop не сработал, задайте Relay__CodexExecutable полным путём к codex.exe.

## Команды

В **General**:

- `/projects` — разрешённые проекты.
- `/link` — выбрать проект или «Без проекта», затем существующий Codex thread; после выбора создаётся topic с названием thread. `/link <проект>` сразу открывает проект.
- `/new` — выбрать существующий проект, создать новый с каталогом внутри `Relay:ProjectCreationRoot` либо выбрать «Без проекта». Чаты без проекта не добавляются в список проектов бота.
- `/new <проект>` — сразу открыть форму выбранного проекта: название (1–128 символов), отдельный каталог внутри проекта, затем новый Codex thread и одноимённый topic. Кнопка «Каталог» позволяет указать другой существующий или новый каталог внутри проекта: `.` для корня проекта, относительный подкаталог или полный путь.
- `/status` — состояние бота: проекты, рабочие topics, активная задача и очередь.
- `/usage` — лимиты Codex и статистика токенов аккаунта.

В **рабочем topic**:

- Обычный текст — задача в закреплённом thread.
- `/status` — проект, thread, модель Codex и текущая задача.
- `/usage` — лимиты и статистика токенов аккаунта.
- `/last` — последний итоговый ответ Codex в thread.
- `/log` — полный журнал последней задачи: команды, вывод, этапы, предупреждения и ошибки. Доступен также кнопкой «Журнал».
- `/fork` — ветка от последнего завершённого turn в новом Codex thread и Telegram topic.
- `/steer <текст>` — уточнить активную задачу. Во время работы можно просто написать обычное сообщение.
- `/queue <текст>` — поставить отдельную задачу после текущей.
- `/stop` — прервать текущий turn.

Ранее созданные вручную topics по-прежнему можно привязать через `/threads <проект>` или `/bind <проект> <threadId>` внутри такого topic.

Каждый turn создаёт одно сообщение статуса, которое редактируется по ходу работы и содержит кнопки подтверждений. Статус показывает модель, текущую команду, каталог, последний вывод, этап работы и раскрываемую недавнюю историю. Полный журнал доступен по /log во время работы и после завершения; последний журнал сохраняется после перезапуска. Модель берётся из последнего контекста привязанного Codex thread; для нового thread до первого turn показывается модель по умолчанию из конфигурации, если она доступна. Переключение модели Codex отображается в статусе. Если App Server оборвёт соединение, статус и финальное сообщение покажут ошибку, а запланированная задача перезапустит бот. После двух минут без событий статус предупредит о задержке. По завершении отправляется один финальный ответ; длинный ответ приходит одним файлом. Обычное сообщение в той же теме во время активного turn передаётся через `turn/steer` как уточнение; число принятых уточнений видно в статусе. `/steer <текст>` делает то же явно. `/queue <текст>` ставит отдельную задачу после текущей. Сообщения в других темах и задачи при занятом Desktop thread сохраняются в очередь. Если уточнение уже нельзя принять, бот явно сообщает об этом и ставит текст в очередь. Бот одновременно выполняет один turn.

## Безопасность и ограничения

Бот принимает сообщения только от указанного user ID в супергруппе. Перед созданием topic он проверяет `can_manage_topics`; привязку записывает в relay-state.json только после успешного создания topic. Один Codex thread нельзя закрепить за двумя topics. Новые проекты создаются с безопасным латинским именем каталога внутри `Relay:ProjectCreationRoot` и сохраняются в relay-state.json; каталог с занятым именем не используется повторно. Для нового чата существующего проекта каталог создаётся после нажатия «Создать»; если `thread/start` не удался, пустой каталог удаляется. Чаты без проекта сохраняют пустое имя проекта и рабочий каталог под `Relay:ProjectlessChatRoot`; привязка переживает перезапуск, а `/fork` продолжает работать. Рабочий каталог и название новых чатов сохраняются вместе с привязкой; старые привязки продолжают использовать корень проекта. Рабочий каталог должен находиться внутри выбранного проекта либо корня чатов без проекта. Если при `/new` или `/fork` создание topic не удалось, новый Codex thread остаётся без привязки: его можно выбрать позже через `/link`. App Server запускается через stdio:// с approvalPolicy=on-request и sandbox=workspace-write. Не задавайте danger-full-access в локальной конфигурации Codex. Бот хранит привязки, очередь и offset Telegram в relay-state.json; токен не сохраняет. После перезапуска незавершённый turn не восстанавливается автоматически; новые сообщения продолжают закреплённый thread.

Usage читается через `account/rateLimits/read` и `account/usage/read` Codex App Server. Если он не авторизован, бот покажет причину; выполните `codex login` на компьютере, где работает бот. Для API-key-only авторизации статистика ChatGPT токенов недоступна.
