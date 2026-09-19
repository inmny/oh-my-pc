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
        var providerConfig = Path.Combine(_root, "provider_config.json");
        await File.WriteAllTextAsync(providerConfig, BuildZcodeFixture());
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: providerConfig,
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
                // Claude 上游模型 → anthropic-messages 规则；Codex 上游模型 → openai-responses 规则
                new ClientSyncModel(new ProxyModelConfig { Name = "glm-5.3", Alias = "GLM-5.3", ThinkingLevels = ["max"], MaxContextLength = 200000 }, ProxyProviderKind.Claude),
                new ClientSyncModel(new ProxyModelConfig { Name = "gpt-5.6-terra", ThinkingLevels = ["low", "max"], MaxContextLength = 100000 }, ProxyProviderKind.Codex)
            ]
        });

        Assert.Equal("cpa-gui", result.ProviderId);
        Assert.Equal(2, result.ModelCount);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(providerConfig))!["config"]!.AsObject();
        var rules = config["providerConfigRules"]!["providerRules"]!.AsArray();

        // anthropic-messages 规则复用旧 cpa-gui 条目：别名作为模型 id，计划外模型被清理
        var anthropic = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == "cpa-gui");
        Assert.Equal("abc", (string?)anthropic["config"]!["access"]!["apiKey"]);
        Assert.Equal("http://127.0.0.1:8317", (string?)anthropic["config"]!["api"]!["baseUrl"]);
        Assert.Equal(["GLM-5.3"], anthropic["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));

        var responses = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == "cli-proxy-api-codex");
        // openai 系端点在 /v1 下：responses 规则的地址必须自带 /v1
        Assert.Equal("openai-responses", (string?)responses["config"]!["api"]!["type"]);
        Assert.Equal("http://127.0.0.1:8317/v1", (string?)responses["config"]!["api"]!["baseUrl"]);
        Assert.Equal(["gpt-5.6-terra"], responses["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));

        // 模型上下文覆盖规则同步更新；计划外的 luna 规则被清理
        var modelRules = config["modelConfigRules"]!["providerModelRules"]!.AsArray();
        Assert.Equal(200000, (int?)modelRules.OfType<JsonObject>().Single(rule => (string?)rule["modelId"] == "GLM-5.3")["config"]!["properties"]!["contextWindow"]);
        Assert.Equal(100000, (int?)modelRules.OfType<JsonObject>().Single(rule => (string?)rule["modelId"] == "gpt-5.6-terra")["config"]!["properties"]!["contextWindow"]);
        Assert.Null(modelRules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["modelId"] == "gpt-5.6-luna"));

        // 用户自建规则与文件其余键（排序、手动规则、默认模型选择）原样保留
        Assert.NotNull(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == "my-own-provider"));
        Assert.Equal(["cpa-gui"], config["providerOrder"]!.AsArray().Select(node => (string?)node));
        Assert.NotNull(config["modelConfigRules"]!["manualProviderModelRules"]);
        Assert.Equal("my-own-provider", (string?)config["defaultModelSelection"]!["providerId"]);
    }

    [Fact]
    public async Task SyncWorkbuddy_GatewayFlattensModelsAndPreservesUserEntries()
    {
        Directory.CreateDirectory(_root);
        var modelsFile = Path.Combine(_root, "models.json");
        await File.WriteAllTextAsync(modelsFile, """
            [
              { "id": "stale-model", "name": "stale-model", "vendor": "oh-my-pc", "url": "http://127.0.0.1:8317/v1", "apiKey": "old" },
              { "id": "user-model", "name": "user-model", "vendor": "user", "url": "https://api.example.com/v1", "apiKey": "own" }
            ]
            """);
        var modelsPath = modelsFile;
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
            workbuddyModels: modelsPath,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Workbuddy,
            ProviderId = "cli-proxy-api",
            BaseUrl = "http://127.0.0.1:8317",
            ApiKey = "123456",
            Models =
            [
                // 网关模式保留别名作为客户端侧 id
                new ClientSyncModel(
                    new ProxyModelConfig { Name = "glm-5.3", Alias = "GLM-5.3", ThinkingLevels = ["max"], MaxContextLength = 200000, InputModalities = ["text", "image"] },
                    ProxyProviderKind.Claude),
                new ClientSyncModel(
                    new ProxyModelConfig { Name = "gpt-5.6-terra", Alias = "GLM-5.3" },
                    ProxyProviderKind.Codex)
            ]
        });

        Assert.Equal("cli-proxy-api", result.ProviderId);
        // 两个上游的别名相同 → 扁平清单按 id 去重
        Assert.Equal(1, result.ModelCount);
        var models = JsonNode.Parse(await File.ReadAllTextAsync(modelsFile))!.AsArray();
        var ours = models.OfType<JsonObject>().Where(entry => (string?)entry["vendor"] == "oh-my-pc").ToList();
        var entry = Assert.Single(ours);
        Assert.Equal("GLM-5.3", (string?)entry["id"]);
        Assert.Equal("http://127.0.0.1:8317/v1", (string?)entry["url"]);
        Assert.Equal("123456", (string?)entry["apiKey"]);
        Assert.True((bool?)entry["supportsReasoning"]);
        Assert.True((bool?)entry["supportsImages"]);
        Assert.Equal(200000, (int?)entry["maxInputTokens"]);
        // 计划外的本应用条目移除，用户手加的条目原样保留
        Assert.Null(models.OfType<JsonObject>().FirstOrDefault(item => (string?)item["id"] == "stale-model"));
        Assert.NotNull(models.OfType<JsonObject>().FirstOrDefault(item => (string?)item["id"] == "user-model"));
        _ = modelsPath;
    }

    [Fact]
    public async Task SyncWorkbuddy_DirectWritesUpstreamUrlsAndKeys()
    {
        Directory.CreateDirectory(_root);
        var modelsFile = Path.Combine(_root, "models.json");
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
            workbuddyModels: modelsFile,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Workbuddy,
            BaseUrl = "http://127.0.0.1:8317",
            Upstreams =
            [
                new ClientSyncUpstream(
                    "or-key|https://openrouter.ai/api/v1", "OpenRouter", "https://openrouter.ai/api/v1", "sk-or",
                    ProxyProviderKind.OpenAiCompatible,
                    [new ProxyModelConfig { Name = "org/kimi-k2", Alias = "kimi" }])
            ]
        });

        Assert.Equal("direct", result.ProviderId);
        Assert.Equal(1, result.ModelCount);
        var models = JsonNode.Parse(await File.ReadAllTextAsync(modelsFile))!.AsArray();
        var entry = Assert.Single(models.OfType<JsonObject>());
        // 直连用上游真实地址、密钥与真实模型名（别名剥离）；chat 端点在 /v1 下
        Assert.Equal("org/kimi-k2", (string?)entry["id"]);
        Assert.Equal("https://openrouter.ai/api/v1", (string?)entry["url"]);
        Assert.Equal("sk-or", (string?)entry["apiKey"]);
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
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
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
            ]
        });

        Assert.Equal("easy-cliproxyapi", result.ProviderId);
        var content = await File.ReadAllTextAsync(settings);
        // 完全覆盖：旧条目内容（含 EasyCPA 时代的遗留键与计划外模型）不保留
        Assert.DoesNotContain("legacy-from-easycpa", content);
        Assert.DoesNotContain("legacy-model-key", content);
        Assert.DoesNotContain("tiers", content);
        Assert.DoesNotContain("EasyCLIProxyAPI", content);
        // glm-5.2 仅允许出现在保留的 agent-default-model（model: 键）里，不得混入 provider 模型列表
        Assert.DoesNotContain("id: glm-5.2", content);
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
        // 默认模型不再由同步管理：agent-default-model 保持用户在 dsh 里的原选择
        Assert.Contains("provider: zai-coding-cn", content);
        Assert.Contains("model: glm-5.2", content);
        Assert.DoesNotContain("provider: easy-cliproxyapi", content);
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
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
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
                ProxyProviderKind.Claude)]
        });

        Assert.Equal("cli-proxy-api", result.ProviderId);
        Assert.Equal(1, result.ModelCount);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(opencodeConfig))!.AsObject();
        Assert.Null(root["model"]);
        var model = root["provider"]!["cli-proxy-api"]!["models"]!["GLM-5.3"]!.AsObject();
        Assert.Equal(1.4m, (decimal?)model["cost"]!["input"]);
        Assert.Equal(0.26m, (decimal?)model["cost"]!["cache_read"]);
        Assert.Equal("http://127.0.0.1:8317/v1", (string?)root["provider"]!["cli-proxy-api"]!["options"]!["baseURL"]);
    }

    [Fact]
    public async Task LoadAndSave_RoundTripsOpenAiCompatProviders()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ConfigPath, """
            host: 127.0.0.1
            port: 8317
            openai-compatibility:
              - name: OpenRouter
                base-url: 'https://openrouter.ai/api/v1'
                request-log: true
                api-key-entries:
                  - api-key: 'sk-or-1'
                  - api-key: 'sk-or-2'
                models:
                  - name: org/kimi-k2
                    alias: kimi-k2
            """);
        var store = CreateStore();
        var snapshot = await store.LoadAsync();

        var compat = snapshot.Providers.Single(p => p.Kind == ProxyProviderKind.OpenAiCompatible);
        Assert.Equal("https://openrouter.ai/api/v1", compat.BaseUrl);
        // 取首个 api-key 作为 provider 密钥
        Assert.Equal("sk-or-1", compat.ApiKey);
        Assert.Equal("OpenRouter", compat.Remark);
        var kimi = Assert.Single(compat.Models);
        Assert.Equal("org/kimi-k2", kimi.Name);
        Assert.Equal("kimi-k2", kimi.Alias);

        // 改名后保存：未知键（request-log）与第二个 api-key 条目保留，name 取 remark
        compat.Remark = "OR 中转";
        await store.SaveAsync(snapshot);
        var content = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("name: OR 中转", content);
        Assert.Contains("request-log: true", content);
        Assert.Contains("sk-or-2", content);

        // 清空该类型后整段移除
        snapshot.Providers.Remove(compat);
        await store.SaveAsync(snapshot);
        var cleared = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("openai-compatibility:", cleared);
    }


    [Fact]
    public async Task SyncZcode_DirectOpenAiCompatibleUsesChatProtocol()
    {
        Directory.CreateDirectory(_root);
        var providerConfig = Path.Combine(_root, "provider_config.json");
        await File.WriteAllTextAsync(providerConfig, "{}");
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: providerConfig,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var upstream = new ClientSyncUpstream(
            "or-key|https://openrouter.ai/api/v1", "OpenRouter", "https://openrouter.ai/api/v1", "sk-or",
            ProxyProviderKind.OpenAiCompatible,
            [new ProxyModelConfig { Name = "org/kimi-k2", Alias = "kimi-k2" }]);
        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Zcode,
            BaseUrl = "http://127.0.0.1:8317",
            Upstreams = [upstream]
        });

        Assert.Equal(1, result.ModelCount);
        var rules = JsonNode.Parse(await File.ReadAllTextAsync(providerConfig))!["config"]!["providerConfigRules"]!["providerRules"]!.AsArray();
        var rule = rules.OfType<JsonObject>().Single();
        // 兼容上游走 chat/completions 协议：地址带 /v1，模型用真实名
        Assert.Equal(CliProxyClientConfigurator.DirectId("or-key|https://openrouter.ai/api/v1"), (string?)rule["providerId"]);
        Assert.Equal("openai-chat-completions", (string?)rule["config"]!["api"]!["type"]);
        Assert.Equal("https://openrouter.ai/api/v1", (string?)rule["config"]!["api"]!["baseUrl"]);
        Assert.Equal(["org/kimi-k2"], rule["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));
    }

    [Fact]
    public async Task SyncDsh_GatewayWritesCompletionsGroupForOpenAiCompatible()
    {
        Directory.CreateDirectory(_root);
        var settings = Path.Combine(_root, "settings.yaml");
        var credentials = Path.Combine(_root, ".credentials.yaml");
        // block 风格的既有条目（flow 风格 {} 会让 YamlDotNet 把整段输出成单行 flow）
        await File.WriteAllTextAsync(settings, """
            llm-pi-ai:
              providers:
                legacy-gateway:
                  displayName: legacy
                  apiKeyEnv: LEGACY_API_KEY
                  api: openai-completions
                  baseURL: http://127.0.0.1:8317/v1
                  models: []
            """);
        await File.WriteAllTextAsync(credentials, "version: 1\nrefs:\n  LEGACY_API_KEY: old\n");
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: settings,
            dshCredentials: credentials);

        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Dsh,
            ProviderId = "cli-proxy-api",
            BaseUrl = "http://127.0.0.1:8317",
            ApiKey = "123456",
            Models = [new ClientSyncModel(
                new ProxyModelConfig { Name = "deepseek-v4-flash", Alias = "DeepSeek-V4-Flash" },
                ProxyProviderKind.OpenAiCompatible)]
        });

        Assert.Equal(1, result.ModelCount);
        var content = await File.ReadAllTextAsync(settings);
        // 兼容组写入 openai-completions 协议组；空模型的 anthropic 槽位消费掉 legacy 条目后按空组移除
        Assert.Contains("cli-proxy-api-completions:", content);
        Assert.Contains("api: openai-completions", content);
        Assert.Contains("baseURL: http://127.0.0.1:8317/v1", content);
        // 网关模式保留别名作为客户端侧 id
        Assert.Contains("id: DeepSeek-V4-Flash", content);
        Assert.DoesNotContain("legacy-gateway", content);
        // anthropic/responses 组无计划内模型且不预存：不写条目
        Assert.DoesNotContain("anthropic-messages", content);
        Assert.DoesNotContain("openai-responses", content);
    }


    [Fact]
    public async Task SyncZcode_SubsetScopeClearsEmptyProtocolGroup()
    {
        Directory.CreateDirectory(_root);
        var providerConfig = Path.Combine(_root, "provider_config.json");
        await File.WriteAllTextAsync(providerConfig, BuildZcodeFixture());
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: providerConfig,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        // 范围只含 Codex 上游：anthropic-messages 规则保留但清单清空
        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Zcode,
            ProviderId = "cli-proxy-api",
            BaseUrl = "http://127.0.0.1:8317",
            ApiKey = "abc",
            Models = [new ClientSyncModel(
                new ProxyModelConfig { Name = "gpt-5.6-sol", Alias = "GPT-5.6-Sol", MaxContextLength = 272000 },
                ProxyProviderKind.Codex)]
        });

        Assert.Equal(1, result.ModelCount);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(providerConfig))!["config"]!.AsObject();
        var rules = config["providerConfigRules"]!["providerRules"]!.AsArray();
        var anthropic = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == "cpa-gui");
        Assert.Empty(anthropic["config"]!["personalModelIds"]!.AsArray());
        var responses = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == "cli-proxy-api-codex");
        Assert.Equal(["GPT-5.6-Sol"], responses["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));
    }


    [Fact]
    public async Task SyncZcode_DirectWritesUpstreamEntriesAndRemovesGateway()
    {
        Directory.CreateDirectory(_root);
        var providerConfig = Path.Combine(_root, "provider_config.json");
        await File.WriteAllTextAsync(providerConfig, """
            {
              "schemaVersion": 1,
              "config": {
                "providerConfigRules": {
                  "providerRules": [
                    {
                      "providerId": "cli-proxy-api-codex",
                      "providerName": "CLIProxyAPI Codex",
                      "enabled": true,
                      "config": {
                        "group": "standard-personal",
                        "access": { "type": "api-key", "apiKey": "123456" },
                        "api": { "type": "openai-responses", "baseUrl": "http://127.0.0.1:8317/v1" }
                      }
                    },
                    {
                      "providerId": "cpa-gui",
                      "providerName": "EasyCLIProxyAPI",
                      "enabled": true,
                      "config": {
                        "group": "standard-personal",
                        "access": { "type": "api-key", "apiKey": "123456" },
                        "api": { "type": "anthropic-messages", "baseUrl": "http://127.0.0.1:8317" },
                        "personalModelIds": ["GLM-5.3"],
                        "modelOrder": ["GLM-5.3"]
                      }
                    },
                    {
                      "providerId": "my-own-provider",
                      "providerName": "自建",
                      "enabled": true,
                      "config": {
                        "group": "standard-personal",
                        "access": { "type": "api-key", "apiKey": "own" },
                        "api": { "type": "openai-chat-completions", "baseUrl": "https://api.example.com/v1" }
                      }
                    }
                  ]
                },
                "modelConfigRules": {
                  "providerModelRules": [
                    { "modelId": "GLM-5.3", "providerId": "cpa-gui", "config": { "properties": { "contextWindow": 40960 } } }
                  ],
                  "manualProviderModelRules": []
                },
                "defaultModelSelection": { "providerId": "cpa-gui", "modelId": "GLM-5.3" }
              }
            }
            """);
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: providerConfig,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var claudeKey = "glm-key|https://open.bigmodel.cn/api/anthropic";
        var codexKey = "input-key|https://ai.input.im";
        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Zcode,
            BaseUrl = "http://127.0.0.1:8317",
            Upstreams =
            [
                new ClientSyncUpstream(
                    claudeKey, "智谱", "https://open.bigmodel.cn/api/anthropic", "glm-secret",
                    ProxyProviderKind.Claude,
                    [new ProxyModelConfig { Name = "glm-5.3", Alias = "GLM-5.3", ThinkingLevels = ["max"] }]),
                new ClientSyncUpstream(
                    codexKey, "Input-Plus", "https://ai.input.im", "input-secret",
                    ProxyProviderKind.Codex,
                    [new ProxyModelConfig { Name = "gpt-5.6-terra", Alias = "GPT-5.6-Terra" }])
            ]
        });

        Assert.Equal("direct", result.ProviderId);
        Assert.Equal(2, result.ModelCount);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(providerConfig))!["config"]!.AsObject();
        var rules = config["providerConfigRules"]!["providerRules"]!.AsArray();
        // 网关规则被移除，用户自建规则保留；默认模型指向被移除的 cpa-gui → 悬空引用一并清除
        Assert.Null(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == "cpa-gui"));
        Assert.Null(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == "cli-proxy-api-codex"));
        Assert.NotNull(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == "my-own-provider"));
        Assert.Null(config["defaultModelSelection"]);
        // 被移除 provider 的模型覆盖规则一并清理
        Assert.Empty(config["modelConfigRules"]!["providerModelRules"]!.AsArray());

        var claudeId = CliProxyClientConfigurator.DirectId(claudeKey);
        var codexId = CliProxyClientConfigurator.DirectId(codexKey);
        var claude = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == claudeId);
        Assert.Equal("anthropic-messages", (string?)claude["config"]!["api"]!["type"]);
        Assert.Equal("智谱", (string?)claude["providerName"]);
        Assert.Equal("glm-secret", (string?)claude["config"]!["access"]!["apiKey"]);
        // anthropic SDK 自拼 /v1/messages：上游根地址原样写入；模型用上游真实名（别名剥离）
        Assert.Equal("https://open.bigmodel.cn/api/anthropic", (string?)claude["config"]!["api"]!["baseUrl"]);
        Assert.Equal(["glm-5.3"], claude["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));

        var codex = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == codexId);
        // openai 系端点在 /v1 下：input.im 的 responses 端点带 /v1
        Assert.Equal("openai-responses", (string?)codex["config"]!["api"]!["type"]);
        Assert.Equal("https://ai.input.im/v1", (string?)codex["config"]!["api"]!["baseUrl"]);
        Assert.Equal("input-secret", (string?)codex["config"]!["access"]!["apiKey"]);
        Assert.Equal(["gpt-5.6-terra"], codex["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));
    }


    [Fact]
    public async Task SyncZcode_DirectResyncRemovesUncheckedUpstream()
    {
        Directory.CreateDirectory(_root);
        var providerConfig = Path.Combine(_root, "provider_config.json");
        await File.WriteAllTextAsync(providerConfig, "{}");
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: providerConfig,
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var first = new ProxyModelConfig { Name = "model-a", MaxContextLength = 100000 };
        var second = new ProxyModelConfig { Name = "model-b", MaxContextLength = 200000 };
        var upstreamA = new ClientSyncUpstream("key-a|https://a.example.com", "A", "https://a.example.com", "secret-a", ProxyProviderKind.Codex, [first]);
        var upstreamB = new ClientSyncUpstream("key-b|https://b.example.com", "B", "https://b.example.com", "secret-b", ProxyProviderKind.Codex, [second]);
        await configurator.SyncAsync(new ClientSyncPlan { Client = ProxyClientKind.Zcode, BaseUrl = "http://127.0.0.1:8317", Upstreams = [upstreamA, upstreamB] });

        var idA = CliProxyClientConfigurator.DirectId("key-a|https://a.example.com");
        var idB = CliProxyClientConfigurator.DirectId("key-b|https://b.example.com");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(providerConfig))!.AsObject();
        var rules = root["config"]!["providerConfigRules"]!["providerRules"]!.AsArray();
        Assert.NotNull(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == idA));
        Assert.NotNull(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == idB));
        // 默认模型指向 B
        root["config"]!["defaultModelSelection"] = new JsonObject { ["providerId"] = idB, ["modelId"] = "model-b" };
        await File.WriteAllTextAsync(providerConfig, root.ToJsonString());

        // 取消勾选 B 后重新同步：B 的规则、模型覆盖规则与指向它的默认模型都要移除
        await configurator.SyncAsync(new ClientSyncPlan { Client = ProxyClientKind.Zcode, BaseUrl = "http://127.0.0.1:8317", Upstreams = [upstreamA] });

        root = JsonNode.Parse(await File.ReadAllTextAsync(providerConfig))!.AsObject();
        rules = root["config"]!["providerConfigRules"]!["providerRules"]!.AsArray();
        var ruleA = rules.OfType<JsonObject>().Single(rule => (string?)rule["providerId"] == idA);
        Assert.Equal(["model-a"], ruleA["config"]!["personalModelIds"]!.AsArray().Select(node => (string?)node));
        Assert.Null(rules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["providerId"] == idB));
        var modelRules = root["config"]!["modelConfigRules"]!["providerModelRules"]!.AsArray();
        Assert.NotNull(modelRules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["modelId"] == "model-a"));
        Assert.Null(modelRules.OfType<JsonObject>().FirstOrDefault(rule => (string?)rule["modelId"] == "model-b"));
        Assert.Null(root["config"]!["defaultModelSelection"]);
    }

    [Fact]
    public async Task SyncOpencode_DirectNormalizesV1BaseUrls()
    {
        Directory.CreateDirectory(_root);
        var opencodeConfig = Path.Combine(_root, "opencode.json");
        await File.WriteAllTextAsync(opencodeConfig, """
            {
              "provider": {
                "cli-proxy-api": {
                  "npm": "@ai-sdk/openai-compatible",
                  "options": { "apiKey": "123456", "baseURL": "http://127.0.0.1:8317/v1" }
                }
              }
            }
            """);
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
            opencodeConfig: opencodeConfig,
            dshSettings: Path.Combine(_root, "missing-dsh.yaml"),
            dshCredentials: Path.Combine(_root, "missing-cred.yaml"));

        var hejuKey = "heju-key|https://www.hejuapi.com/v1";
        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Opencode,
            BaseUrl = "http://127.0.0.1:8317",
            Upstreams =
            [
                new ClientSyncUpstream(
                    hejuKey, "Heju", "https://www.hejuapi.com/v1", "heju-secret",
                    ProxyProviderKind.Codex,
                    [new ProxyModelConfig { Name = "gpt-5.6-sol", Alias = "GPT-5.6-Sol" }])
            ]
        });

        Assert.Equal(1, result.ModelCount);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(opencodeConfig))!.AsObject();
        Assert.Null(root["provider"]!["cli-proxy-api"]);
        var provider = root["provider"]![CliProxyClientConfigurator.DirectId(hejuKey)]!.AsObject();
        // 已带 /v1 的地址原样保留；chat 端点在 /v1/chat/completions
        Assert.Equal("https://www.hejuapi.com/v1", (string?)provider["options"]!["baseURL"]);
        Assert.Equal("heju-secret", (string?)provider["options"]!["apiKey"]);
        Assert.NotNull(provider["models"]!["gpt-5.6-sol"]);
    }

    [Fact]
    public async Task SyncDsh_DirectWritesPerUpstreamGroupsAndCredentials()
    {
        Directory.CreateDirectory(_root);
        var settings = Path.Combine(_root, "settings.yaml");
        var credentials = Path.Combine(_root, ".credentials.yaml");
        await File.WriteAllTextAsync(settings, BuildDshFixture());
        await File.WriteAllTextAsync(credentials, "version: 1\nrefs:\n  OTHER_KEY: 'x'\n");
        var configurator = new CliProxyClientConfigurator(
            zcodeProviderConfig: Path.Combine(_root, "missing-zcode.json"),
            opencodeConfig: Path.Combine(_root, "missing-opencode.json"),
            dshSettings: settings,
            dshCredentials: credentials);

        var glmKey = "glm-key|https://open.bigmodel.cn/api/anthropic";
        var hejuKey = "heju-key|https://www.hejuapi.com/v1";
        var result = await configurator.SyncAsync(new ClientSyncPlan
        {
            Client = ProxyClientKind.Dsh,
            BaseUrl = "http://127.0.0.1:8317",
            Upstreams =
            [
                new ClientSyncUpstream(
                    glmKey, "智谱", "https://open.bigmodel.cn/api/anthropic", "glm-secret",
                    ProxyProviderKind.Claude,
                    [new ProxyModelConfig { Name = "glm-5.3-flash", Alias = "GLM-5.3-Flash" }]),
                new ClientSyncUpstream(
                    hejuKey, "Heju", "https://www.hejuapi.com/v1", "heju-secret",
                    ProxyProviderKind.Codex,
                    [new ProxyModelConfig { Name = "gpt-5.6-sol" }])
            ]
        });

        Assert.Equal(2, result.ModelCount);
        var content = await File.ReadAllTextAsync(settings);
        // 网关组移除，直连组按协议写入
        Assert.DoesNotContain("easy-cliproxyapi:", content);
        Assert.Contains("api: anthropic-messages", content);
        Assert.Contains("baseURL: https://open.bigmodel.cn/api/anthropic", content);
        Assert.Contains("api: openai-responses", content);
        Assert.Contains("baseURL: https://www.hejuapi.com/v1", content);
        Assert.Contains("id: glm-5.3-flash", content);
        Assert.Contains("id: gpt-5.6-sol", content);
        // 默认模型保持用户的原选择
        Assert.Contains("provider: zai-coding-cn", content);

        var claudeId = CliProxyClientConfigurator.DirectId(glmKey);
        var codexId = CliProxyClientConfigurator.DirectId(hejuKey);
        var creds = await File.ReadAllTextAsync(credentials);
        Assert.DoesNotContain("EASYCLIPROXYAPI_API_KEY", creds);
        Assert.Contains("OTHER_KEY", creds);
        Assert.Contains($"DIRECT_{claudeId["direct-".Length..].ToUpperInvariant()}_API_KEY: glm-secret", creds);
        Assert.Contains($"DIRECT_{codexId["direct-".Length..].ToUpperInvariant()}_API_KEY: heju-secret", creds);
    }

    [Fact]
    public async Task SavePersistsRemarkAndAliasAndBuildsAliasTable()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ConfigPath, BuildFixture());
        var store = CreateStore();
        var snapshot = await store.LoadAsync();

        var codex = snapshot.Providers.Single(p => p.Kind == ProxyProviderKind.Codex);
        codex.Remark = "Input 中转";
        await store.SaveAsync(snapshot);

        var content = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("remark: Input 中转", content);
        // cost 键不再由本应用读写（费率统一来自 models.dev）；fixture 中已有的 cost 原样保留
        Assert.DoesNotContain("cache-read", content);
        // 纯数字下游密钥保存后保持带引号（字符串类型）
        Assert.Contains("- '123456'", content);

        var reloaded = await store.LoadAsync();
        var reloadedCodex = reloaded.Providers.Single(p => p.Kind == ProxyProviderKind.Codex);
        Assert.Equal("Input 中转", reloadedCodex.Remark);
        // 别名表用于用量统计的名称归一（fixture 中 GLM 带别名）
        Assert.Equal("glm-5.3", reloaded.AliasToName["GLM-5.3"]);

        reloadedCodex.Remark = null;
        await store.SaveAsync(reloaded);
        var cleared = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("remark:", cleared);
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
          "schemaVersion": 1,
          "config": {
            "providerConfigRules": {
              "providerRules": [
                {
                  "providerId": "cpa-gui",
                  "providerName": "EasyCLIProxyAPI",
                  "enabled": true,
                  "config": {
                    "group": "standard-personal",
                    "access": { "type": "api-key", "apiKey": "123456" },
                    "api": { "type": "anthropic-messages", "baseUrl": "http://127.0.0.1:8317" },
                    "personalModelIds": ["gpt-5.6-terra", "gpt-5.6-luna"],
                    "modelOrder": ["gpt-5.6-terra", "gpt-5.6-luna"]
                  }
                },
                {
                  "providerId": "my-own-provider",
                  "providerName": "自建",
                  "enabled": true,
                  "config": {
                    "group": "standard-personal",
                    "access": { "type": "api-key", "apiKey": "own" },
                    "api": { "type": "openai-chat-completions", "baseUrl": "https://api.example.com/v1" },
                    "personalModelIds": ["own-model"],
                    "modelOrder": ["own-model"]
                  }
                }
              ]
            },
            "modelConfigRules": {
              "providerModelRules": [
                { "modelId": "gpt-5.6-luna", "providerId": "cpa-gui", "config": { "properties": { "contextWindow": 40960 } } }
              ],
              "manualProviderModelRules": []
            },
            "providerOrder": ["cpa-gui"],
            "defaultModelSelection": { "providerId": "my-own-provider", "modelId": "own-model" }
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
