using System.Text.Json;

namespace CodexRelay;

internal sealed class TurnTrace
{
    private readonly List<string> _entries = new();
    public IReadOnlyList<string> Entries => _entries;
    public DateTime LastEventAt { get; private set; } = DateTime.UtcNow;

    public void Add(string label, string detail = "")
    {
        LastEventAt = DateTime.UtcNow;
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + label;
        if (!string.IsNullOrWhiteSpace(detail)) line += "\n" + detail.Trim();
        _entries.Add(line);
        if (_entries.Count > 200) _entries.RemoveAt(0);
    }

    public void Touch() => LastEventAt = DateTime.UtcNow;
    public string FullText => string.Join("\n\n", _entries);
    public string Recent(int maxChars)
    {
        var entries = _entries.TakeLast(8).ToList();
        while (entries.Count > 1 && string.Join("\n\n", entries).Length > maxChars)
            entries.RemoveAt(0);
        var result = string.Join("\n\n", entries);
        return result.Length <= maxChars ? result : result[..(maxChars - 1)] + "…";
    }
}

internal static class ThreadModel
{
    public static string Resolve(string threadId)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var sessions = Path.Combine(home, "sessions");
            if (Directory.Exists(sessions))
            {
                var path = Directory.EnumerateFiles(sessions, "*" + threadId + "*.jsonl", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (path != null)
                {
                    string model = "";
                    string effort = "";
                    foreach (var line in File.ReadLines(path))
                    {
                        if (!line.Contains("turn_context", StringComparison.Ordinal)) continue;
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var type) && type.GetString() == "turn_context" &&
                            root.TryGetProperty("payload", out var payload))
                        {
                            if (payload.TryGetProperty("model", out var m)) model = m.GetString() ?? model;
                            if (payload.TryGetProperty("effort", out var e)) effort = e.GetString() ?? effort;
                        }
                    }
                    if (model.Length > 0) return effort.Length > 0 ? model + " · " + effort : model;
                }
            }
            var config = Path.Combine(home, "config.toml");
            if (File.Exists(config))
            {
                var model = File.ReadLines(config).FirstOrDefault(x => x.TrimStart().StartsWith("model = ", StringComparison.Ordinal));
                if (model != null)
                {
                    var value = model.Split('=', 2)[1].Trim().Trim('"');
                    if (value.Length > 0) return value + " (по умолчанию)";
                }
            }
        }
        catch (Exception) { }
        return "не определена";
    }
}
