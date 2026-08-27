namespace OhMyPc.Core.Domain;

public enum ProxyProviderKind
{
    Claude,
    Codex
}

public enum ProxyProcessState
{
    Stopped,
    Starting,
    Running
}

public enum ProxyClientKind
{
    Zcode,
    Opencode,
    Dsh
}

/// <summary>CLIProxyAPI 配置中受支持的取值范围与策略常量。</summary>
public static class ProxyCatalog
{
    public const string StrategyRoundRobin = "round-robin";
    public const string StrategyWeightedRoundRobin = "weighted-round-robin";
    public const string StrategyFillFirst = "fill-first";
    /// <summary>全新配置默认使用的下游密钥，与本机既有客户端条目保持一致，无需重新同步即可继续访问。</summary>
    public const string DefaultApiKey = "123456";

    public static readonly IReadOnlyList<string> Strategies = [StrategyRoundRobin, StrategyWeightedRoundRobin, StrategyFillFirst];

    public static readonly IReadOnlyList<string> ThinkingLevels = ["off", "low", "medium", "high", "xhigh", "max"];

    public static readonly IReadOnlyList<string> Modalities = ["text", "image", "audio", "video"];
}

/// <summary>模型费用（美元 / 每百万 tokens），来源 models.dev 或手动填写。</summary>
public sealed class ProxyModelCost
{
    public decimal? Input { get; set; }
    public decimal? Output { get; set; }
    public decimal? CacheRead { get; set; }
    public decimal? CacheWrite { get; set; }

    public bool IsEmpty => Input is null && Output is null && CacheRead is null && CacheWrite is null;
}

public sealed class ProxyModelConfig
{
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public List<string> ThinkingLevels { get; set; } = [];
    public long? MaxContextLength { get; set; }
    public List<string> InputModalities { get; set; } = [];
    public List<string> OutputModalities { get; set; } = [];
    public ProxyModelCost? Cost { get; set; }

    /// <summary>客户端侧使用的模型标识：配置了别名时为别名，否则为上游名称。</summary>
    public string GetId() => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}

public sealed class ProxyProviderConfig
{
    public ProxyProviderKind Kind { get; set; }
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    /// <summary>本应用使用的显示名，存于条目 remark 键（CLIProxyAPI 忽略该键）。</summary>
    public string? Remark { get; set; }
    public int? Priority { get; set; }
    public List<ProxyModelConfig> Models { get; set; } = [];
}

public sealed class ProxyRoutingConfig
{
    public string Strategy { get; set; } = ProxyCatalog.StrategyRoundRobin;
    public bool SessionAffinity { get; set; }
    public int RequestRetry { get; set; } = 3;
    public int MaxRetryInterval { get; set; } = 30;
}

public sealed class ProxyAccessConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8317;
    public List<string> ApiKeys { get; set; } = [];

    public string GetBaseUrl() => $"http://{Host}:{Port}";
}

/// <summary>config.yaml 中由本应用管理部分的内存快照。</summary>
public sealed class ProxyConfigSnapshot
{
    public List<ProxyProviderConfig> Providers { get; set; } = [];
    public ProxyRoutingConfig Routing { get; set; } = new();
    public ProxyAccessConfig Access { get; set; } = new();
}

public sealed class ProxyServiceStatus
{
    public ProxyProcessState State { get; init; } = ProxyProcessState.Stopped;
    public string BaseUrl { get; init; } = "";
    public string? Version { get; init; }
    public int ModelCount { get; init; }
}

/// <summary>来自 models.dev 的模型元数据（已归一化到本应用的取值范围）。</summary>
public sealed class ModelMetadata
{
    public string Id { get; init; } = "";
    public long? ContextWindow { get; init; }
    public IReadOnlyList<string> InputModalities { get; init; } = [];
    public IReadOnlyList<string> OutputModalities { get; init; } = [];
    public IReadOnlyList<string> ThinkingLevels { get; init; } = [];
    public ProxyModelCost Cost { get; init; } = new();
}

public sealed class ProxyInstallOptions
{
    public bool MigrateFromEasyCpa { get; init; }
}

public sealed class ProxyInstallResult
{
    public bool Migrated { get; init; }
}

/// <summary>待同步的一个模型及其所属上游的协议类型（决定写入客户端时的 API 格式）。</summary>
public sealed record ClientSyncModel(ProxyModelConfig Config, ProxyProviderKind Kind);

public sealed class ClientSyncPlan
{
    public ProxyClientKind Client { get; init; }
    public string ProviderId { get; init; } = "cli-proxy-api";
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public IReadOnlyList<ClientSyncModel> Models { get; init; } = [];
    public string? DefaultModelId { get; init; }
    public string? DefaultEffort { get; init; }
}

public sealed class ClientSyncResult
{
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];
    public string ProviderId { get; init; } = "";
    public string? DefaultModel { get; init; }
}
