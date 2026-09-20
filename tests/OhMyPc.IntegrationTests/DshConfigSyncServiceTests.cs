using OhMyPc.Infrastructure.Dsh;
using YamlDotNet.RepresentationModel;

namespace OhMyPc.IntegrationTests;

public sealed class DshConfigSyncServiceTests
{
    private const string LocalSettings = """
        ui-onboarding:
          welcomeNoticeVersion: 2026-08-13.1
        llm-pi-ai:
          providers:
            direct-a:
              displayName: 自建-Anthropic
              apiKeyEnv: DIRECT_A_API_KEY
              api: anthropic-messages
              baseURL: 'https://api.example.com'
              models:
                [
                  { id: m1, name: m1, contextWindow: 1000000, maxTokens: 128000, input: [ text ] }
                ]
        agent-default-model:
          provider: direct-a
          model: m1
        notify-ompc:
          enabled: true
        """;

    private const string RemoteSettings = """
        llm-pi-ai:
          providers:
            direct-old:
              displayName: 旧供应商
              apiKeyEnv: OLD_KEY
              models: []
        extra-plugin:
          maxUses: 5
        """;

    private const string LocalCredentials = """
        version: 1
        refs:
          DIRECT_A_API_KEY: sk-aaa
          UNRELATED_KEY: sk-bbb
        records:
          client-1:
            kind: grant
        """;

    [Fact]
    public void CollectApiKeyEnvs_ExtractsProviderEnvNames()
    {
        var names = DshConfigSyncService.CollectApiKeyEnvs(DshConfigSyncService.LoadMapping(LocalSettings));

        var env = Assert.Single(names);
        Assert.Equal("DIRECT_A_API_KEY", env);
    }

    [Fact]
    public void MergeSettings_ModelOverwritesModelKeysAndKeepsRemoteOthers()
    {
        var merged = DshConfigSyncService.MergeSettingsText(LocalSettings, RemoteSettings, includeModel: true, includePluginSettings: false);
        var root = DshConfigSyncService.LoadMapping(merged);

        // 模型键来自本地
        Assert.True((bool)HasProvider(root, "direct-a"));
        Assert.False((bool)HasProvider(root, "direct-old"));
        Assert.Equal("direct-a", Scalar(root, "agent-default-model:provider"));
        // 远端其他内容保留，且不带入本地未勾选的键
        Assert.Equal("5", Scalar(root, "extra-plugin:maxUses"));
        Assert.Null(Scalar(root, "notify-ompc:enabled"));
        Assert.Null(Scalar(root, "ui-onboarding:welcomeNoticeVersion"));
    }

    [Fact]
    public void MergeSettings_PluginSettingsSyncsOtherKeysOnly()
    {
        var merged = DshConfigSyncService.MergeSettingsText(LocalSettings, RemoteSettings, includeModel: false, includePluginSettings: true);
        var root = DshConfigSyncService.LoadMapping(merged);

        // 模型键不覆盖（保留远端），其他键来自本地
        Assert.True((bool)HasProvider(root, "direct-old"));
        Assert.Equal("true", Scalar(root, "notify-ompc:enabled"));
        Assert.Null(Scalar(root, "agent-default-model:provider"));
    }

    [Fact]
    public void MergeSettings_CreatesRemoteWhenMissing()
    {
        var merged = DshConfigSyncService.MergeSettingsText(LocalSettings, null, includeModel: true, includePluginSettings: true);
        var root = DshConfigSyncService.LoadMapping(merged);

        Assert.True((bool)HasProvider(root, "direct-a"));
        Assert.Equal("true", Scalar(root, "notify-ompc:enabled"));
    }

    [Fact]
    public void MergeCredentials_TakesReferencedSubsetAndPreservesRemoteRecords()
    {
        var remoteCredentials = """
            version: 1
            refs:
              OLD_KEY: sk-old
              DIRECT_A_API_KEY: sk-stale
            records:
              remote-client:
                kind: grant
            """;

        var merged = DshConfigSyncService.MergeCredentialsText(
            LocalCredentials, remoteCredentials, new HashSet<string> { "DIRECT_A_API_KEY" });
        var root = DshConfigSyncService.LoadMapping(merged);

        Assert.Equal("sk-aaa", Scalar(root, "refs:DIRECT_A_API_KEY"));
        Assert.Null(Scalar(root, "refs:UNRELATED_KEY"));
        Assert.Null(Scalar(root, "refs:OLD_KEY"));
        Assert.NotNull(root.Children["records"]);
        Assert.Equal("1", Scalar(root, "version"));
    }

    [Fact]
    public void MergeCredentials_BuildsSkeletonWhenRemoteMissing()
    {
        var merged = DshConfigSyncService.MergeCredentialsText(
            LocalCredentials, null, new HashSet<string> { "DIRECT_A_API_KEY" });
        var root = DshConfigSyncService.LoadMapping(merged);

        Assert.Equal("sk-aaa", Scalar(root, "refs:DIRECT_A_API_KEY"));
        Assert.Equal("1", Scalar(root, "version"));
        Assert.NotNull(root.Children["records"]);
    }

    [Fact]
    public void DiscoverItems_ListsModelPluginSettingsAndPatchFiles()
    {
        var home = Path.Combine(Path.GetTempPath(), $"oh-my-pc-dshhome-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
            Directory.CreateDirectory(Path.Combine(home, "profiles", "empty"));
            File.WriteAllText(Path.Combine(home, "settings.yaml"), "llm-pi-ai: {}");
            File.WriteAllText(Path.Combine(home, "profiles", "web", "cordis.patch.yml"), "[]");

            var items = DshConfigSyncService.DiscoverItems(home);

            Assert.Equal(["model", "plugin-settings", "patch:web"], items.Select(x => x.Id).ToArray());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void DiscoverItems_ReturnsEmptyWithoutDshHome()
    {
        Assert.Empty(DshConfigSyncService.DiscoverItems(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")));
    }

    private static object HasProvider(YamlMappingNode root, string providerId)
    {
        var providers = DshConfigSyncService.LoadMapping(
            DshConfigSyncService.Emit(root)).Children[new YamlScalarNode("llm-pi-ai")] is YamlMappingNode llm
            && llm.Children[new YamlScalarNode("providers")] is YamlMappingNode mapping
                ? mapping
                : null;
        return providers?.Children.ContainsKey(new YamlScalarNode(providerId)) ?? false;
    }

    private static string? Scalar(YamlMappingNode root, string path)
    {
        YamlNode node = DshConfigSyncService.LoadMapping(DshConfigSyncService.Emit(root));
        foreach (var segment in path.Split(':'))
        {
            if (node is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(segment), out var child))
            {
                node = child;
            }
            else
            {
                return null;
            }
        }

        return node is YamlScalarNode scalar ? scalar.Value : null;
    }
}
