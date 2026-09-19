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

    /// <summary>zcode 桌面端新版（3.12+）个人 Provider 配置：运行时唯一读取来源。</summary>
    public static string ZcodeProviderConfig { get; } = Path.Combine(ZcodeHome, "v2", "provider_config.json");

    /// <summary>workbuddy 的用户级自定义模型清单（OpenAI Chat Completions 协议的扁平列表）。</summary>
    public static string WorkbuddyModels { get; } = Path.Combine(CliProxyPaths.UserProfile, ".workbuddy", "models.json");

    public static string OpencodeConfig { get; } = Path.Combine(CliProxyPaths.UserProfile, ".config", "opencode", "opencode.json");

    public static string DshHome { get; } = Path.Combine(CliProxyPaths.UserProfile, ".dsh");

    public static string DshSettings { get; } = Path.Combine(DshHome, "settings.yaml");

    public static string DshCredentials { get; } = Path.Combine(DshHome, ".credentials.yaml");
}
