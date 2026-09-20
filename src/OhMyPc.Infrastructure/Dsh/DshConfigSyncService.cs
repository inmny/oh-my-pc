using Microsoft.Extensions.Logging;
using OhMyPc.Core.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>一个可选同步的配置条目。</summary>
public sealed record DshConfigItem(string Id, string DisplayName, string Description);

/// <summary>
/// 把本机 DSH 配置推送到远端服务器：
/// - 模型配置：settings.yaml 的 llm-pi-ai / agent-default-model 键 + .credentials.yaml 中被 apiKeyEnv 引用的密钥子集；
/// - 其他插件配置：settings.yaml 其余顶层键（不含 onboarding 状态）；
/// - 补丁层：profiles/&lt;name&gt;/cordis.patch.yml 整文件。
/// 远端采用键级合并（保留远端其他内容）并先备份原文件；插件清单 package.json 含本机 link: 路径，不同步。
/// </summary>
public sealed class DshConfigSyncService(SshSessionPool pool, ILogger<DshConfigSyncService> logger)
{
    public const string ModelItemId = "model";
    public const string PluginSettingsItemId = "plugin-settings";

    private readonly SshCommandRunner _runner = new(pool);

    public static string LocalDshHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    /// <summary>发现本机可同步的配置条目；本机未初始化 DSH 时返回空。</summary>
    public static IReadOnlyList<DshConfigItem> DiscoverItems(string? dshHome = null)
    {
        var home = dshHome ?? LocalDshHome;
        var items = new List<DshConfigItem>();
        if (!Directory.Exists(home)) return items;

        if (File.Exists(Path.Combine(home, "settings.yaml")))
        {
            items.Add(new DshConfigItem(
                ModelItemId,
                "模型与供应商（含引用的 API 凭据）",
                "settings.yaml 的 llm-pi-ai / agent-default-model，并同步被引用的密钥"));
            items.Add(new DshConfigItem(
                PluginSettingsItemId,
                "其他插件配置",
                "settings.yaml 其余顶层键（不含界面引导状态）"));
        }

        var profiles = Path.Combine(home, "profiles");
        if (Directory.Exists(profiles))
        {
            foreach (var directory in Directory.EnumerateDirectories(profiles).Order())
            {
                var patch = Path.Combine(directory, "cordis.patch.yml");
                if (!File.Exists(patch)) continue;
                var name = Path.GetFileName(directory);
                items.Add(new DshConfigItem(
                    $"patch:{name}",
                    $"补丁层：{name}",
                    "profiles/" + name + "/cordis.patch.yml 整文件覆盖"));
            }
        }

        return items;
    }

    public async Task SyncAsync(
        DshServerDefinition server,
        IReadOnlyList<string> selectedIds,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var home = LocalDshHome;
        var wantModel = selectedIds.Contains(ModelItemId);
        var wantPluginSettings = selectedIds.Contains(PluginSettingsItemId);

        if (wantModel && !File.Exists(Path.Combine(home, "settings.yaml")))
        {
            throw new InvalidOperationException("本机未找到 ~/.dsh/settings.yaml，无法同步模型配置。");
        }

        if (wantModel || wantPluginSettings)
        {
            var localSettingsText = File.ReadAllText(Path.Combine(home, "settings.yaml"));
            var remoteSettingsText = await _runner.ReadFileAsync(server, "~/.dsh/settings.yaml", cancellationToken)
                .ConfigureAwait(false);
            await PrepareRemoteDirectoryAsync(server, cancellationToken).ConfigureAwait(false);
            await BackupRemoteFileAsync(server, "~/.dsh/settings.yaml", progress, cancellationToken).ConfigureAwait(false);
            await _runner.WriteFileAsync(
                server,
                "~/.dsh/settings.yaml",
                MergeSettingsText(localSettingsText, remoteSettingsText, wantModel, wantPluginSettings),
                cancellationToken).ConfigureAwait(false);
            progress.Report("settings.yaml 已同步。");

            if (wantModel)
            {
                await SyncCredentialsAsync(server, localSettingsText, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var id in selectedIds.Where(id => id.StartsWith("patch:", StringComparison.Ordinal)))
        {
            var profile = id["patch:".Length..];
            var localPatch = Path.Combine(home, "profiles", profile, "cordis.patch.yml");
            if (!File.Exists(localPatch)) continue;
            var remotePath = $"~/.dsh/profiles/{profile}/cordis.patch.yml";
            await _runner.RunAsync(
                server,
                $"mkdir -p {SshCommandRunner.QuoteRemotePath($"~/.dsh/profiles/{profile}")}",
                cancellationToken).ConfigureAwait(false);
            await BackupRemoteFileAsync(server, remotePath, progress, cancellationToken).ConfigureAwait(false);
            await _runner.WriteFileAsync(server, remotePath, File.ReadAllText(localPatch), cancellationToken)
                .ConfigureAwait(false);
            progress.Report($"补丁层 {profile} 已同步。");
        }
    }

    /// <summary>同步凭据：只带模型供应商引用到的 apiKeyEnv 密钥，远端其余凭据保留。</summary>
    private async Task SyncCredentialsAsync(
        DshServerDefinition server,
        string localSettingsText,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var referenced = CollectApiKeyEnvs(LoadMapping(localSettingsText));
        if (referenced.Count == 0) return;

        var credentialsPath = Path.Combine(LocalDshHome, ".credentials.yaml");
        if (!File.Exists(credentialsPath))
        {
            progress.Report("本机没有 .credentials.yaml，跳过凭据同步。");
            return;
        }

        var remoteText = await _runner.ReadFileAsync(server, "~/.dsh/.credentials.yaml", cancellationToken).ConfigureAwait(false);
        var merged = MergeCredentialsText(File.ReadAllText(credentialsPath), remoteText, referenced);

        await BackupRemoteFileAsync(server, "~/.dsh/.credentials.yaml", progress, cancellationToken).ConfigureAwait(false);
        await _runner.WriteFileAsync(server, "~/.dsh/.credentials.yaml", merged, cancellationToken).ConfigureAwait(false);
        progress.Report($"凭据已同步（{referenced.Count} 个密钥）。");
    }

    /// <summary>合并 settings.yaml：远端为底，按选择覆盖本地对应键，远端其余内容保留。</summary>
    internal static string MergeSettingsText(
        string localText, string? remoteText, bool includeModel, bool includePluginSettings)
    {
        var local = LoadMapping(localText);
        var remote = remoteText is null ? new YamlMappingNode() : LoadMapping(remoteText);
        if (includeModel)
        {
            CopyKey(local, remote, "llm-pi-ai");
            CopyKey(local, remote, "agent-default-model");
        }

        if (includePluginSettings)
        {
            foreach (var key in local.Children.Keys)
            {
                if (key is not YamlScalarNode scalar || scalar.Value is not { } keyText) continue;
                if (keyText is "llm-pi-ai" or "agent-default-model" or "ui-onboarding") continue;
                CopyKey(local, remote, keyText);
            }
        }

        return Emit(remote);
    }

    /// <summary>合并凭据文件：只取引用到的 refs 子集，补齐 version/records 骨架。</summary>
    internal static string MergeCredentialsText(
        string localText, string? remoteText, IReadOnlySet<string> referencedEnvNames)
    {
        var local = LoadMapping(localText);
        var remote = remoteText is null ? new YamlMappingNode() : LoadMapping(remoteText);
        var newRefs = new YamlMappingNode();
        if (local.Children["refs"] is YamlMappingNode localRefs)
        {
            foreach (var child in localRefs.Children)
            {
                if (child.Key is YamlScalarNode key && key.Value is { } keyName && referencedEnvNames.Contains(keyName))
                {
                    newRefs.Children.Add(child.Key, child.Value);
                }
            }
        }

        remote.Children["refs"] = newRefs;
        if (!remote.Children.ContainsKey("version")) remote.Children["version"] = "1";
        if (!remote.Children.ContainsKey("records")) remote.Children["records"] = new YamlMappingNode();
        return Emit(remote);
    }

    /// <summary>
    /// 用底层 Emitter 输出 YAML：YamlDocument(node) 构造器会给所有节点自动分配锚点（&amp;123456 key:），
    /// 导致输出连 YamlDotNet 自己都无法稳定回读；遍历节点直接发事件则完全可控。
    /// </summary>
    internal static string Emit(YamlMappingNode root)
    {
        using var writer = new StringWriter();
        var emitter = new Emitter(writer, 2, int.MaxValue);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        EmitNode(root, emitter);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return writer.ToString();
    }

    private static void EmitNode(YamlNode node, IEmitter emitter)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, mapping.Style));
                foreach (var pair in mapping.Children)
                {
                    EmitNode(pair.Key, emitter);
                    EmitNode(pair.Value, emitter);
                }

                emitter.Emit(new MappingEnd());
                break;
            case YamlSequenceNode sequence:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, sequence.Style));
                foreach (var child in sequence.Children)
                {
                    EmitNode(child, emitter);
                }

                emitter.Emit(new SequenceEnd());
                break;
            case YamlScalarNode scalar:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, scalar.Value, scalar.Style, isPlainImplicit: true, isQuotedImplicit: false));
                break;
            default:
                throw new InvalidOperationException($"不支持的 YAML 节点类型：{node.GetType().Name}");
        }
    }

    internal static YamlMappingNode LoadMapping(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode mapping
            ? mapping
            : new YamlMappingNode();
    }

    /// <summary>提取所有供应商的 apiKeyEnv 环境变量名。</summary>
    public static IReadOnlySet<string> CollectApiKeyEnvs(YamlMappingNode settings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (settings.Children["llm-pi-ai"] is not YamlMappingNode llm
            || llm.Children["providers"] is not YamlMappingNode providers) return names;
        foreach (var provider in providers.Children.Values)
        {
            if (provider is YamlMappingNode mapping
                && mapping.Children[new YamlScalarNode("apiKeyEnv")] is YamlScalarNode env
                && env.Value is { } envName
                && envName.Length > 0)
            {
                names.Add(envName);
            }
        }

        return names;
    }

    private async Task PrepareRemoteDirectoryAsync(DshServerDefinition server, CancellationToken cancellationToken) =>
        await _runner.RunAsync(server, "mkdir -p ~/.dsh", cancellationToken).ConfigureAwait(false);

    private async Task BackupRemoteFileAsync(
        DshServerDefinition server,
        string remotePath,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            server,
            $"if [ -f {SshCommandRunner.QuoteRemotePath(remotePath)} ]; then " +
            $"cp {SshCommandRunner.QuoteRemotePath(remotePath)} {SshCommandRunner.QuoteRemotePath(remotePath)}.bak-$(date +%Y%m%d%H%M%S); fi",
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) return;
        logger.LogWarning("备份远端文件 {Path} 失败：{Error}", remotePath, result.Error);
        progress.Report($"警告：备份 {remotePath} 失败，继续同步。");
    }

    private static void CopyKey(YamlMappingNode source, YamlMappingNode target, string key)
    {
        var node = new YamlScalarNode(key);
        if (source.Children.TryGetValue(node, out var value))
        {
            target.Children[node] = value;
        }
    }
}
