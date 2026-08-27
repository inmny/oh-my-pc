using System.Text.Json.Nodes;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.CliProxy;
using Xunit;

namespace OhMyPc.IntegrationTests;

public sealed class CliProxyConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cliproxy-test-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_root, "config.yaml");

    [Fact]
    public async Task EnsureConfig_CreatesMinimalConfig()
    {
        var store = CreateStore();
        var created = await store.EnsureConfigAsync();

        Assert.True(created);
        Assert.True(File.Exists(ConfigPath));
        var content = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("host: 127.0.0.1", content);
        Assert.Contains("port: 8317", content);
        Assert.Contains("strategy: round-robin", content);
        Assert.False(await store.EnsureConfigAsync());
    }

    [Fact]
    public async Task LoadReadsProvidersAndRouting()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ConfigPath, BuildFixture());
        var snapshot = await CreateStore().LoadAsync();

        Assert.Equal(2, snapshot.Providers.Count);
        var claude = Assert.Single(snapshot.Providers, p => p.Kind == ProxyProviderKind.Claude);
        Assert.Equal("key-zhipu", claude.ApiKey);
        var glm = Assert.Single(claude.Models);
        Assert.Equal("glm-5.3", glm.Name);
        Assert.Equal("GLM-5.3", glm.Alias);
        Assert.Equal(["max"], glm.ThinkingLevels);
        var codex = Assert.Single(snapshot.Providers, p => p.Kind == ProxyProviderKind.Codex);
        Assert.Equal(10, codex.Priority);
        Assert.Equal(ProxyCatalog.StrategyFillFirst, snapshot.Routing.Strategy);
        Assert.Equal(3, snapshot.Routing.RequestRetry);
        Assert.Equal(["123456"], snapshot.Access.ApiKeys);
    }

    [Fact]
    public async Task SavePreservesUnknownFieldsAndAppliesManagedChanges()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ConfigPath, BuildFixture());
        var store = CreateStore();
        var snapshot = await store.LoadAsync();

        snapshot.Routing.Strategy = ProxyCatalog.StrategyRoundRobin;
        snapshot.Access.ApiKeys = ["abcdef"];
        snapshot.Providers.RemoveAll(p => p.Kind == ProxyProviderKind.Claude);
        snapshot.Providers.First(p => p.Kind == ProxyProviderKind.Codex).Models[0].MaxContextLength = 100000;
        snapshot.Providers.Add(new ProxyProviderConfig
        {
            Kind = ProxyProviderKind.Codex,
            ApiKey = "key-new",
            BaseUrl = "https://relay.example.com",
            Models = [new ProxyModelConfig { Name = "new-model", ThinkingLevels = ["high"] }]
        });
        await store.SaveAsync(snapshot);

        var content = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("commercial-mode: false", content);
        Assert.Contains("weight: 5", content);
        Assert.Contains("display-name: GPT Terra", content);
        // excluded-models 不再由本应用管理，但文件中已有的键原样保留
        Assert.Contains("gpt-5.4-mini", content);
        Assert.Contains("max-context-length: 100000", content);
        Assert.DoesNotContain("claude-api-key", content);
        Assert.Contains("key-new", content);
        Assert.Contains("strategy: round-robin", content);
        Assert.Contains("abcdef", content);
        Assert.DoesNotContain("123456", content);

        var reloaded = await store.LoadAsync();
        Assert.Equal(2, reloaded.Providers.Count);
        Assert.Equal(100000, reloaded.Providers[0].Models[0].MaxContextLength);
        var added = reloaded.Providers.Single(p => p.ApiKey == "key-new");
        Assert.Equal(["high"], added.Models[0].ThinkingLevels);
    }

    [Fact]
    public async Task SyncZcode_SplitsProtocolsAndRemovesMisplacedModels()
    {
        Directory.CreateDirectory(_root);
        var desktopConfig = Path.Combine(_root, "zcode-config.json");
        var cliConfig = Path.Combine(_root, "zcode-cli-config.json");
        await File.WriteAllTextAsync(desktopConfig, BuildZcodeFixture());
        await File.WriteAllTextAsync(cliConfig, BuildZcodeCliFixture());
        var configurator = new CliProxyClientConfigurator(
            zcodeDesktopConfig: desktopConfig,
            zcodeCliConfig: cliConfig,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Zcode,
            ProviderId = "cli-proxy-api",
            BaseUrl = "http://127.0.0.1:8317",
            ApiKey = "abc",
            Models =
            [
                // Claude 上游模型 → anthropic provider；Codex 上游模型 → openai(responses) provider
                new ClientSyncModel(new ProxyModelConfig { Name = "glm-5.3", Alias = "GLM-5.3", ThinkingLevels = ["max"] }, ProxyProviderKind.Claude),
                new ClientSyncModel(new ProxyModelConfig { Name = "gpt-5.6-terra", ThinkingLevels = ["low", "max"] }, ProxyProviderKind.Codex)
            ],
            DefaultModelId = "gpt-5.6-terra"
        });

        Assert.Equal("cpa-gui", result.ProviderId);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(desktopConfig))!.AsObject();
        // 默认模型属于 Codex，应指向 openai 协议的 provider
        Assert.Equal("cli-proxy-api-codex/gpt-5.6-terra", (string?)root["model"]);

        var anthropic = root["provider"]!["cpa-gui"]!.AsObject();
        Assert.Equal("anthropic", (string?)anthropic["kind"]);
        Assert.Equal("abc", (string?)anthropic["options"]!["apiKey"]);
        var anthropicModels = anthropic["models"]!.AsObject();
        Assert.NotNull(anthropicModels["GLM-5.3"]);
        Assert.Null(anthropicModels["gpt-5.6-terra"]);
        // 早期错误同步塞进 anthropic 条目的 codex 模型应被清理
        Assert.Null(anthropicModels["gpt-5.6-luna"]);

        var codex = root["provider"]!["cli-proxy-api-codex"]!.AsObject();
        Assert.Equal("openai", (string?)codex["kind"]);
        var codexModel = codex["models"]!["gpt-5.6-terra"]!.AsObject();
        Assert.Equal(["low", "max"],
            codexModel["reasoning"]!["variants"]!.AsArray().Select(node => (string?)node));

        var untouched = root["provider"]!["builtin:bigmodel"]!.AsObject();
        Assert.Equal("https://open.bigmodel.cn/api/anthropic", (string?)untouched["options"]!["baseURL"]);

        var cliRoot = JsonNode.Parse(await File.ReadAllTextAsync(cliConfig))!.AsObject();
        Assert.Equal("anthropic-messages", (string?)cliRoot["provider"]!["cpa-gui"]!["apiFormat"]);
        Assert.Equal("openai-responses", (string?)cliRoot["provider"]!["cli-proxy-api-codex"]!["apiFormat"]);
    }

    [Fact]
    public async Task SyncDsh_PreservesCostAndUpdatesCredentials()
    {
        Directory.CreateDirectory(_root);
        var settings = Path.Combine(_root, "settings.yaml");
        var credentials = Path.Combine(_root, ".credentials.yaml");
        await File.WriteAllTextAsync(settings, BuildDshFixture());
        await File.WriteAllTextAsync(credentials, "version: 1\nrefs:\n  OTHER_KEY: 'x'\n");
        var configurator = new CliProxyClientConfigurator(
            zcodeDesktopConfig: Path.Combine(_root, "missing-zcode.json"),
            zcodeCliConfig: Path.Combine(_root, "missing-zcode-cli.json"),
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: settings,
            dshCredentials: credentials);

        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Dsh,
            ProviderId = "cli-proxy-api",
            BaseUrl = "http://127.0.0.1:8317",
            ApiKey = "654321",
            Models =
            [
                // Claude 上游 → completions 组；Codex 上游 → responses 组
                new ClientSyncModel(
                    new ProxyModelConfig
                    {
                        Name = "glm-5.3-flash",
                        Alias = "GLM-5.3-Flash",
                        InputModalities = ["text", "image", "video"],
                        ThinkingLevels = ["max"]
                    },
                    ProxyProviderKind.Claude),
                new ClientSyncModel(
                    new ProxyModelConfig
                    {
                        Name = "gpt-5.6-sol",
                        ThinkingLevels = ["low", "max"],
                        Cost = new ProxyModelCost { Input = 5m, Output = 30m, CacheRead = 0.5m, CacheWrite = 6.25m }
                    },
                    ProxyProviderKind.Codex)
            ],
            DefaultModelId = "GLM-5.3-Flash",
            DefaultEffort = "max"
        });

        Assert.Equal("easy-cliproxyapi", result.ProviderId);
        var content = await File.ReadAllTextAsync(settings);
        // 完全覆盖：旧条目内容（含 EasyCPA 时代的遗留键与计划外模型）不保留
        Assert.DoesNotContain("legacy-from-easycpa", content);
        Assert.DoesNotContain("legacy-model-key", content);
        Assert.DoesNotContain("tiers", content);
        Assert.DoesNotContain("EasyCLIProxyAPI", content);
        Assert.DoesNotContain("glm-5.2", content);
        // dsh 模态枚举只有 text/image，video 必须被过滤
        Assert.DoesNotContain("video", content);
        Assert.Contains("cacheRead: 0.5", content);
        Assert.Contains("cacheWrite: 6.25", content);
        Assert.Contains("reasoningEfforts", content);
        // 双协议组：Claude 上游 → anthropic-messages（baseURL 为网关根地址），Codex 上游 → responses（带 /v1）
        Assert.Contains("easy-cliproxyapi:", content);
        Assert.Contains("api: anthropic-messages", content);
        Assert.Contains("baseURL: http://127.0.0.1:8317" + Environment.NewLine, content);
        Assert.Contains("cli-proxy-api-responses:", content);
        Assert.Contains("api: openai-responses", content);
        Assert.Contains("baseURL: http://127.0.0.1:8317/v1", content);
        Assert.Contains("id: GLM-5.3-Flash", content);
        Assert.Contains("id: gpt-5.6-sol", content);
        // 默认模型属于 Claude 上游，指向 completions 组
        Assert.Contains("provider: easy-cliproxyapi", content);
        Assert.Contains("model: GLM-5.3-Flash", content);
        Assert.Contains("reasoningEffort: max", content);
        Assert.Contains("OTHER_KEY", await File.ReadAllTextAsync(credentials));
        // 纯数字密钥必须带引号写入，否则 YAML 解析为整数，dsh 凭据加载器要求字符串
        Assert.Contains("EASYCLIPROXYAPI_API_KEY: '654321'", await File.ReadAllTextAsync(credentials));
        // YAML 输出不带文档结束标记 "..."
        Assert.False((await File.ReadAllTextAsync(settings)).TrimEnd().EndsWith("..."));
        Assert.False((await File.ReadAllTextAsync(credentials)).TrimEnd().EndsWith("..."));
    }

    [Fact]
    public async Task SyncOpencode_WritesDecimalCostWithoutError()
    {
        Directory.CreateDirectory(_root);
        var opencodeConfig = Path.Combine(_root, "opencode.json");
        var configurator = new CliProxyClientConfigurator(
            zcodeDesktopConfig: Path.Combine(_root, "missing-zcode.json"),
            zcodeCliConfig: Path.Combine(_root, "missing-zcode-cli.json"),
            opencodeConfig: opencodeConfig,
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Opencode,
            ProviderId = "cli-proxy-api",
            BaseUrl = "http://127.0.0.1:8317",
            ApiKey = "abc",
            Models = [new ClientSyncModel(
                new ProxyModelConfig
                {
                    Name = "GLM-5.3",
                    ThinkingLevels = ["max"],
                    Cost = new ProxyModelCost { Input = 1.4m, Output = 4.4m, CacheRead = 0.26m }
                },
                ProxyProviderKind.Claude)],
            DefaultModelId = "GLM-5.3"
        });

        Assert.Equal("cli-proxy-api", result.ProviderId);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(opencodeConfig))!.AsObject();
        Assert.Equal("cli-proxy-api/GLM-5.3", (string?)root["model"]);
        var model = root["provider"]!["cli-proxy-api"]!["models"]!["GLM-5.3"]!.AsObject();
        Assert.Equal(1.4m, (decimal?)model["cost"]!["input"]);
        Assert.Equal(0.26m, (decimal?)model["cost"]!["cache_read"]);
        Assert.Equal("http://127.0.0.1:8317/v1", (string?)root["provider"]!["cli-proxy-api"]!["options"]!["baseURL"]);
    }

    [Fact]
    public async Task SavePersistsRemarkAndCostAndRemovesThemWhenCleared()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ConfigPath, BuildFixture());
        var store = CreateStore();
        var snapshot = await store.LoadAsync();

        var codex = snapshot.Providers.Single(p => p.Kind == ProxyProviderKind.Codex);
        codex.Remark = "Input 中转";
        codex.Models[0].Cost = new ProxyModelCost { Input = 5m, Output = 30m, CacheRead = 0.5m };
        await store.SaveAsync(snapshot);

        var content = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("remark: Input 中转", content);
        Assert.Contains("cache-read: 0.5", content);
        Assert.DoesNotContain("cache-write", content);
        // 纯数字下游密钥保存后保持带引号（字符串类型）
        Assert.Contains("- '123456'", content);

        var reloaded = await store.LoadAsync();
        var reloadedCodex = reloaded.Providers.Single(p => p.Kind == ProxyProviderKind.Codex);
        Assert.Equal("Input 中转", reloadedCodex.Remark);
        Assert.Equal(5m, reloadedCodex.Models[0].Cost!.Input);
        Assert.Equal(0.5m, reloadedCodex.Models[0].Cost!.CacheRead);

        reloadedCodex.Remark = null;
        reloadedCodex.Models[0].Cost = null;
        await store.SaveAsync(reloaded);
        var cleared = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("remark:", cleared);
        Assert.DoesNotContain("cost:", cleared);
    }

    private CliProxyConfigStore CreateStore() =>
        new(ConfigPath, Path.Combine(_root, "oauth"));

    private string BuildFixture() =>
        $"""
        host: 127.0.0.1
        port: 8317
        auth-dir: ..\oauth
        commercial-mode: false
        api-keys:
          - '123456'
        request-retry: 3
        max-retry-interval: 30
        routing:
          strategy: fill-first
          session-affinity: false
        claude-api-key:
          - api-key: 'key-zhipu'
            base-url: 'https://open.bigmodel.cn/api/anthropic'
            models:
              - name: glm-5.3
                alias: GLM-5.3
                display-name: GLM 5.3
                thinking:
                  levels:
                    - max
        codex-api-key:
          - api-key: 'key-input'
            base-url: 'https://ai.input.im'
            weight: 5
            priority: 10
            models:
              - name: gpt-5.6-terra
                display-name: GPT Terra
                thinking:
                  levels:
                    - low
                    - max
            excluded-models:
              - gpt-5.4-mini
        """;

    private static string BuildZcodeFixture() =>
        """
        {
          "provider": {
            "cpa-gui": {
              "name": "EasyCLIProxyAPI",
              "kind": "anthropic",
              "source": "custom",
              "enabled": true,
              "options": { "apiKey": "123456", "baseURL": "http://127.0.0.1:8317" },
              "models": {
                "gpt-5.6-terra": {
                  "name": "gpt-5.6-terra",
                  "zcode": { "modified": true, "priority": 99 }
                },
                "gpt-5.6-luna": { "name": "gpt-5.6-luna" }
              }
            },
            "builtin:bigmodel": {
              "kind": "anthropic",
              "options": { "baseURL": "https://open.bigmodel.cn/api/anthropic" }
            }
          },
          "model": "cpa-gui/gpt-5.6-terra"
        }
        """;

    private static string BuildZcodeCliFixture() =>
        """
        {
          "provider": {
            "cpa-gui": {
              "kind": "anthropic",
              "options": { "apiKey": "123456", "baseURL": "http://127.0.0.1:8317" }
            }
          }
        }
        """;

    private static string BuildDshFixture() =>
        """
        llm-pi-ai:
          providers:
            easy-cliproxyapi:
              displayName: EasyCLIProxyAPI
              apiKeyEnv: EASYCLIPROXYAPI_API_KEY
              api: openai-completions
              baseURL: http://127.0.0.1:8317/v1
              legacy-from-easycpa: keep-me-out
              models:
                - id: gpt-5.6-sol
                  name: gpt-5.6-sol
                  legacy-model-key: keep-me-out
                  cost:
                    input: 5
                    tiers:
                      - inputTokensAbove: 272000
                        input: 10
                - id: glm-5.2
                  name: glm-5.2
        agent-default-model:
          provider: zai-coding-cn
          model: glm-5.2
          reasoningEffort: max
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
