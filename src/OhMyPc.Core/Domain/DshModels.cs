namespace OhMyPc.Core.Domain;

public enum DshAuthKind
{
    Key,
    Password
}

public enum DshRunState
{
    Stopped,
    Starting,
    Running,
    /// <summary>端口有 web 在响应，但不是本应用拉起的进程，只读展示。</summary>
    External
}

/// <summary>一台 SSH 远端服务器上待管理的 DSH 实例；密码不入此定义，经 IAppStore 单独加密存取。</summary>
public sealed class DshServerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string UserName { get; set; } = "root";
    public DshAuthKind AuthKind { get; set; } = DshAuthKind.Key;
    /// <summary>私钥文件路径；空表示按 ~/.ssh 默认键序（id_ed25519/id_ecdsa/id_rsa）尝试。</summary>
    public string? KeyPath { get; set; }
    /// <summary>远端 dsh web 监听端口（仅绑回环）。</summary>
    public int RemotePort { get; set; } = 3080;
    /// <summary>隧道在本机占用的回环端口。</summary>
    public int LocalPort { get; set; } = 13080;
    /// <summary>已确认的服务器主机密钥指纹（SHA256:...），空表示尚未信任。</summary>
    public string? HostKeyFingerprint { get; set; }
    public string? Note { get; set; }
    /// <summary>
    /// 配置同步选择（逗号分隔的条目 id，如 "model,plugin-settings,patch:web"）：
    /// null 表示从未配置过同步；非空时「打开/启动」前自动推送所选段到远端。
    /// </summary>
    public string? ConfigSyncSelection { get; set; }
}

/// <summary>本机 dsh web 实例的运行快照。</summary>
public sealed record DshInstanceSnapshot
{
    public DshRunState State { get; init; } = DshRunState.Stopped;
    public int Port { get; init; }
    public string? Version { get; init; }
    /// <summary>带 token 的面板地址（仅本应用拉起的实例能拿到）。</summary>
    public string? PanelUrl { get; init; }
    public string? Error { get; init; }
}

/// <summary>远端 DSH 的探测结果：SSH 可达性、已安装版本与 web 存活。</summary>
public sealed class DshProbeResult
{
    public bool Reachable { get; init; }
    public string? InstalledVersion { get; init; }
    public bool WebRunning { get; init; }
    public string? Error { get; init; }
}

/// <summary>拉起远端 dsh web 的参数；TrustedLocalPort 用于隧道后 Host 头变化的 /api 信任域。</summary>
public sealed record DshLaunchOptions(int RemotePort, int? TrustedLocalPort);

/// <summary>一条活跃隧道的展示信息。</summary>
/// <summary>一条活跃隧道的展示信息；UpstreamPort 是隧道在本机绑定的内部端口（浏览器实际访问 LocalPort 上的代理）。</summary>
public sealed record DshTunnelHandle(string ServerId, string ServerName, int LocalPort, int RemotePort, int UpstreamPort);
