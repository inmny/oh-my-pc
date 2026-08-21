namespace OhMyPc.Infrastructure.LocalUsage;

public sealed class LocalToolDetector
{
    private readonly IReadOnlyDictionary<string, string[]> _roots = BuildRoots();
    public string DshRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh");
    public string DshSessionsRoot => Path.Combine(DshRoot, "sessions");
    public string DshSettingsPath => Path.Combine(DshRoot, "settings.yaml");

    public IReadOnlyList<string> DetectClients() => _roots
        .Where(pair => pair.Value.Any(Directory.Exists))
        .Select(pair => pair.Key)
        .OrderBy(x => x)
        .ToList();

    public IReadOnlyList<string> GetWatchRoots() => _roots
        .SelectMany(pair => pair.Value)
        .Append(DshRoot)
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IReadOnlyDictionary<string, string[]> BuildRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = [Path.Combine(home, ".claude", "projects"), Path.Combine(home, ".claude", "transcripts")],
            ["codex"] = [Path.Combine(home, ".codex", "sessions"), Path.Combine(home, ".codex", "archived_sessions")],
            ["opencode"] = [Path.Combine(home, ".local", "share", "opencode")],
            ["openclaw"] = [Path.Combine(home, ".openclaw", "agents")],
            ["hermes"] = [Path.Combine(home, ".hermes")],
            ["gemini"] = [Path.Combine(home, ".gemini")],
            ["kimi"] = [Path.Combine(home, ".kimi", "sessions"), Path.Combine(home, ".kimi-code", "sessions")],
            ["qwen"] = [Path.Combine(home, ".qwen", "projects")],
            ["grok"] = [Path.Combine(home, ".grok", "sessions")],
            ["pi"] = [Path.Combine(home, ".pi", "agent", "sessions"), Path.Combine(home, ".omp", "agent", "sessions")],
            ["cline"] = [Path.Combine(home, ".cline", "data", "sessions"), Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "tasks")],
            ["copilot"] = [Path.Combine(home, ".copilot"), Path.Combine(appData, "GitHub Copilot")],
            ["zed"] = [Path.Combine(localData, "Zed")],
            ["kiro"] = [Path.Combine(home, ".kiro"), Path.Combine(appData, "Kiro")]
        };
    }
}
