using System.Text.Json;

namespace CodexRelay;

public sealed class RelayOptions
{
    public string BotToken { get; init; } = "";
    public long OwnerUserId { get; init; }
    public Dictionary<string, string> Projects { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string ProjectCreationRoot { get; init; } = @"S:\Projects\Codex";
    public string ProjectlessChatRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex");
    public string StateFile { get; init; } = "relay-state.json";
    public string? CodexExecutable { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BotToken)) throw new InvalidOperationException("Relay:BotToken is required.");
        if (OwnerUserId <= 0) throw new InvalidOperationException("Relay:OwnerUserId is required.");
        if (!Path.IsPathFullyQualified(ProjectCreationRoot) || !Directory.Exists(ProjectCreationRoot))
            throw new InvalidOperationException($"Invalid project creation root: {ProjectCreationRoot}");
        if (!Path.IsPathFullyQualified(ProjectlessChatRoot))
            throw new InvalidOperationException($"Invalid projectless chat root: {ProjectlessChatRoot}");
        foreach (var (name, path) in Projects)
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                throw new InvalidOperationException($"Project {name} has invalid path: {path}");
    }

    public string ResolveCodex()
    {
        if (!string.IsNullOrWhiteSpace(CodexExecutable)) return CodexExecutable;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        var match = Directory.Exists(root)
            ? Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return match ?? throw new FileNotFoundException("Codex Desktop executable not found. Set Relay:CodexExecutable.");
    }
}

public sealed class RelayState
{
    public Dictionary<string, string> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TopicBinding> Topics { get; set; } = new();
    public List<PendingTask> PendingTasks { get; set; } = new();
    public string? SelectedProject { get; set; }
    public Dictionary<string, string> Threads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int UpdateOffset { get; set; }
}

public sealed record TopicBinding(string Project, string ThreadId, string? WorkingDirectory = null,
    string? PendingName = null);
public sealed record PendingTask(long ChatId, int TopicId, string Text, int StatusMessageId = 0);

public sealed class StateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public RelayState State { get; }

    public StateStore(string path)
    {
        _path = Path.GetFullPath(path);
        State = File.Exists(_path)
            ? JsonSerializer.Deserialize<RelayState>(File.ReadAllText(_path)) ?? new RelayState()
            : new RelayState();
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        finally { _gate.Release(); }
    }
}
