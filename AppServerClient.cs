using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexRelay;

public sealed class AppServerClient : IAsyncDisposable
{
    private readonly ILogger<AppServerClient> _log;
    private readonly Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    public event Func<JsonElement, Task>? Message;
    public event Func<Task>? Disconnected;

    public AppServerClient(ILogger<AppServerClient> log, string executable)
    {
        _log = log;
        _process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server --listen stdio://")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        }};
        if (!_process.Start()) throw new InvalidOperationException("Codex App Server did not start.");
        _ = ReadLoop();
        _ = ReadErrors();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await CallAsync("initialize", new { clientInfo = new { name = "CodexRelay", version = "0.1.0" } }, ct);
        await SendAsync(new { method = "initialized" }, ct);
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task RespondAsync(JsonElement id, object result, CancellationToken ct) =>
        SendAsync(new { id, result }, ct);

    public Task RejectAsync(JsonElement id, string message, CancellationToken ct) =>
        SendAsync(new { id, error = new { code = -32601, message } }, ct);

    private async Task SendAsync(object value, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(value);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement.Clone();
                if (message.TryGetProperty("id", out var id) &&
                    !message.TryGetProperty("method", out _) &&
                    id.ValueKind == JsonValueKind.Number &&
                    _pending.TryRemove(id.GetInt64(), out var completion))
                {
                    if (message.TryGetProperty("error", out var error))
                        completion.TrySetException(new InvalidOperationException(error.ToString()));
                    else completion.TrySetResult(message.GetProperty("result"));
                }
                else if (Message is { } handler)
                    await handler(message);
            }
        }
        catch (Exception ex) { _log.LogError(ex, "App Server stream failed"); }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("Codex App Server disconnected."));
            if (Disconnected is { } disconnected)
                try { await disconnected(); }
                catch (Exception ex) { _log.LogError(ex, "Could not report App Server disconnection"); }
        }
    }

    private async Task ReadErrors()
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
            _log.LogWarning("Codex: {Line}", line);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        _writeLock.Dispose();
    }
}
