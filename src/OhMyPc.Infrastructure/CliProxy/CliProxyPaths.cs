namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>CLIProxyAPI 的固定安装路径约定；旧 EasyCPA 路径仅用于一次性迁移探测。</summary>
public static class CliProxyPaths
{
    public static string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string InstallDirectory { get; } = Path.Combine(UserProfile, "utils", "CPA");

    public static string ExecutablePath { get; } = Path.Combine(InstallDirectory, "cli-proxy-api.exe");

    public static string ConfigPath { get; } = Path.Combine(InstallDirectory, "config.yaml");

    public static string AuthDirectory { get; } = Path.Combine(InstallDirectory, "oauth");

    public static string EasyCpaConfigPath { get; } = Path.Combine(UserProfile, "utils", "EasyCPA", "cpa-core", "config.yaml");

    public static string EasyCpaAuthDirectory { get; } = Path.Combine(UserProfile, "utils", "EasyCPA", "oauth");
}

/// <summary>zcode / opencode / dsh 三个客户端的配置文件路径。</summary>
public static class ProxyClientPaths
{
    public static string ZcodeHome { get; } = Path.Combine(CliProxyPaths.UserProfile, ".zcode");

    public static string ZcodeDesktopConfig { get; } = Path.Combine(ZcodeHome, "v2", "config.json");

    public static string ZcodeCliConfig { get; } = Path.Combine(ZcodeHome, "cli", "config.json");

    public static string OpencodeConfig { get; } = Path.Combine(CliProxyPaths.UserProfile, ".config", "opencode", "opencode.json");

    public static string DshHome { get; } = Path.Combine(CliProxyPaths.UserProfile, ".dsh");

    public static string DshSettings { get; } = Path.Combine(DshHome, "settings.yaml");

    public static string DshCredentials { get; } = Path.Combine(DshHome, ".credentials.yaml");
}
