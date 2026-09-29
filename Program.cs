using CodexRelay;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

var builder = Host.CreateApplicationBuilder(args);
var envPath = Path.Combine(builder.Environment.ContentRootPath, ".env");
if (!File.Exists(envPath)) envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (File.Exists(envPath))
{
    var values = new Dictionary<string, string?>();
    foreach (var rawLine in File.ReadLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var separator = line.IndexOf('=');
        if (separator < 1) throw new FormatException($"Invalid .env line: {line}");
        var key = line[..separator].Trim().Replace("__", ":");
        var value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        values[key] = value;
    }
    var source = new MemoryConfigurationSource { InitialData = values };
    var environmentIndex = builder.Configuration.Sources.ToList()
        .FindLastIndex(item => item is EnvironmentVariablesConfigurationSource);
    builder.Configuration.Sources.Insert(environmentIndex < 0 ? builder.Configuration.Sources.Count : environmentIndex, source);
}
var options = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();

if (args.Contains("--check-app-server"))
{
    using var loggerFactory = LoggerFactory.Create(x => x.AddConsole());
    await using var server = new AppServerClient(loggerFactory.CreateLogger<AppServerClient>(), options.ResolveCodex());
    await server.InitializeAsync(CancellationToken.None);
    var project = options.Projects.Values.FirstOrDefault(Directory.Exists);
    if (project != null)
    {
        var result = await server.CallAsync("thread/list", new { cwd = project, limit = 8,
            sortKey = "recency_at", sortDirection = "desc" }, CancellationToken.None);
        var threads = result.GetProperty("data");
        Console.WriteLine("Codex App Server thread/list OK: " + threads.GetArrayLength());
        if (threads.GetArrayLength() > 0)
        {
            var id = threads[0].GetProperty("id").GetString();
            var read = await server.CallAsync("thread/read", new { threadId = id, includeTurns = false }, CancellationToken.None);
            Console.WriteLine("Codex App Server thread/read OK: " + read.GetProperty("thread").GetProperty("id").GetString());
        }
    }
    return;
}

builder.Services.AddSingleton(options);
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
