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
    public static string? ReadLastModel(string threadId)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            foreach (var folder in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(home, folder);
                if (!Directory.Exists(root)) continue;
                var path = Directory.EnumerateFiles(root, "*" + threadId + "*.jsonl",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (path == null) continue;
                string? model = null;
                foreach (var line in File.ReadLines(path))
                {
                    if (!line.Contains("turn_context", StringComparison.Ordinal)) continue;
                    using var doc = JsonDocument.Parse(line);
                    var entry = doc.RootElement;
                    if (entry.TryGetProperty("type", out var type) &&
                        type.GetString() == "turn_context" &&
                        entry.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("model", out var value))
                        model = value.GetString() ?? model;
                }
                if (!string.IsNullOrWhiteSpace(model)) return model;
            }
        }
        catch (Exception) { }
        return null;
    }
}
