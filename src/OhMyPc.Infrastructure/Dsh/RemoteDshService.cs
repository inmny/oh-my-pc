using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>单台远端服务器的 DSH 编排；实例按服务器定义创建，不跨调用保存状态。</summary>
public sealed class RemoteDshService(
    DshServerDefinition server,
    SshSessionPool pool,
    ILogger<RemoteDshService> logger,
    DshConnectCredential? credential = null) : IRemoteDshService
{
    private static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(40);
    private const int MinimumNodeMajor = 20;
    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+", RegexOptions.Compiled);

    private readonly SshCommandRunner _runner = new(pool, credential);

    public async Task<DshProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dsh = await _runner.RunAsync(server, RemoteDshCommands.ProbeDsh(), cancellationToken).ConfigureAwait(false);
            var node = ParseKeyValueLines((await _runner.RunAsync(server, RemoteDshCommands.ProbeNode(), cancellationToken)
                .ConfigureAwait(false)).Output);
            var http = await _runner.RunAsync(server, RemoteDshCommands.ProbeHttp(server.RemotePort), cancellationToken).ConfigureAwait(false);
            var (version, detail) = ParseDshProbe(dsh.Output, node.GetValueOrDefault("node", "missing"));
            return new DshProbeResult
            {
                Reachable = true,
                InstalledVersion = version,
                WebRunning = IsHttpAlive(http.Output),
                Error = detail
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "探测远端 DSH 失败：{Host}:{Port}", server.Host, server.SshPort);
            return new DshProbeResult { Reachable = false, Error = exception.Message };
        }
    }

    public Task InstallAsync(IProgress<string> progress, CancellationToken cancellationToken = default) =>
        InstallOrUpdateAsync(progress, cancellationToken);

    public Task UpdateAsync(IProgress<string> progress, CancellationToken cancellationToken = default) =>
        InstallOrUpdateAsync(progress, cancellationToken);

    public async Task<string?> StartAsync(DshLaunchOptions options, CancellationToken cancellationToken = default)
    {
        await _runner.RunAsync(server, RemoteDshCommands.Start(options.RemotePort, options.TrustedLocalPort), cancellationToken)
            .ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + StartDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = await ReadPanelUrlAsync(options.RemotePort, cancellationToken).ConfigureAwait(false);
            if (url is not null)
            {
                // URL 打印后端口可能仍在启动，等 HTTP 可访问再返回，避免浏览器拿到拒绝连接
                var http = await _runner.RunAsync(server, RemoteDshCommands.ProbeHttp(options.RemotePort), cancellationToken)
                    .ConfigureAwait(false);
                if (IsHttpAlive(http.Output)) return url;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default) =>
        await _runner.RunAsync(server, RemoteDshCommands.Stop(server.RemotePort), cancellationToken).ConfigureAwait(false);

    public Task<string?> GetPanelUrlAsync(CancellationToken cancellationToken = default) =>
        ReadPanelUrlAsync(server.RemotePort, cancellationToken);

    /// <summary>
    /// 安装/更新 DSH：npm 缺失或 Node 低于 20 时先自动安装/升级 Node
    /// （root 或免密 sudo 走系统源，否则 nvm 用户级），然后把 DSH 装到用户级前缀
    /// ~/.local（无 EACCES）并验证可运行。
    /// </summary>
    private async Task InstallOrUpdateAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var environment = ParseKeyValueLines((await _runner.RunAsync(server, RemoteDshCommands.ProbeNode(), cancellationToken)
            .ConfigureAwait(false)).Output);
        var nodeVersion = environment.GetValueOrDefault("node", "missing");
        var npmMissing = environment.GetValueOrDefault("npm", "missing") == "missing";
        var nodeTooOld = !npmMissing && TryGetNodeMajor(nodeVersion) is { } major && major < MinimumNodeMajor;

        if (npmMissing || nodeTooOld)
        {
            await InstallNodeAsync(
                environment,
                nodeTooOld ? $"Node {nodeVersion} 过旧（需要 {MinimumNodeMajor}+），正在升级…" : "未检测到 Node.js，正在安装…",
                progress,
                cancellationToken).ConfigureAwait(false);
            environment = ParseKeyValueLines((await _runner.RunAsync(server, RemoteDshCommands.ProbeNode(), cancellationToken)
                .ConfigureAwait(false)).Output);
            nodeVersion = environment.GetValueOrDefault("node", "missing");
            if (environment.GetValueOrDefault("npm", "missing") == "missing")
            {
                throw new InvalidOperationException("Node.js 自动安装后 npm 仍不可用，请查看输出日志定位系统源问题。");
            }

            if (TryGetNodeMajor(nodeVersion) is not { } upgradedMajor || upgradedMajor < MinimumNodeMajor)
            {
                throw new InvalidOperationException($"Node.js 升级后版本仍不满足要求（当前 {nodeVersion}，需要 {MinimumNodeMajor}+）。");
            }
        }

        await _runner.RunAsync(server, RemoteDshCommands.EnsurePathPersistence(), cancellationToken).ConfigureAwait(false);

        progress.Report("$ npm install -g --prefix ~/.local @deepseek-ai/dsh@latest");
        var exit = await _runner.StreamAsync(server, RemoteDshCommands.InstallOrUpdate(), progress, cancellationToken)
            .ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"远端 npm 安装失败（退出码 {exit}），请查看输出日志。");
        }

        await _runner.RunAsync(server, RemoteDshCommands.EnsureDshOnPath(), cancellationToken).ConfigureAwait(false);

        // 装完立即验证： PATH 链接不生效或 Node 过旧导致跑不起来时，当场给出原因而不是留给探测
        var verify = ParseKeyValueLines((await _runner.RunAsync(server, RemoteDshCommands.ProbeDsh(), cancellationToken)
            .ConfigureAwait(false)).Output);
        var versionText = verify.GetValueOrDefault("dsh-version", "missing");
        if (versionText == "missing")
        {
            throw new InvalidOperationException("安装命令已执行，但远端仍找不到 dsh 命令；请把上方输出日志发给我们排查。");
        }

        if (!VersionPattern.IsMatch(versionText))
        {
            throw new InvalidOperationException($"dsh 已安装但无法运行（Node {nodeVersion}）：{versionText}");
        }

        progress.Report($"安装完成：dsh {versionText}");
    }

    private async Task InstallNodeAsync(
        IReadOnlyDictionary<string, string> environment,
        string reason,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var isRoot = environment.GetValueOrDefault("root") == "yes";
        var passwordlessSudo = environment.GetValueOrDefault("sudo") == "yes";
        var distro = RemoteDshCommands.ParseDistro(environment.GetValueOrDefault("distro", "unknown"));

        if (isRoot || passwordlessSudo)
        {
            if (distro == DistroKind.Unsupported)
            {
                throw new InvalidOperationException(
                    $"暂不支持自动安装的系统发行版（{environment.GetValueOrDefault("distro", "unknown")}），请手动安装 Node.js {MinimumNodeMajor}+。");
            }

            var sudoPrefix = isRoot ? "" : "sudo -E ";
            progress.Report($"{reason}（{distro} 系统源，{(isRoot ? "root" : "sudo")}）");
            var exit = await _runner.StreamAsync(
                server, RemoteDshCommands.InstallNode(distro, sudoPrefix), progress, cancellationToken).ConfigureAwait(false);
            if (exit != 0)
            {
                throw new InvalidOperationException($"Node.js 系统源安装失败（退出码 {exit}），请查看输出日志。");
            }
        }
        else
        {
            progress.Report($"{reason}（无 sudo 权限，改用 nvm 用户级安装）");
            var installExit = await _runner.StreamAsync(server, RemoteDshCommands.InstallNvm(), progress, cancellationToken)
                .ConfigureAwait(false);
            if (installExit != 0)
            {
                throw new InvalidOperationException($"nvm 安装失败（退出码 {installExit}），请查看输出日志。");
            }

            var nodeExit = await _runner.StreamAsync(server, RemoteDshCommands.InstallNodeViaNvm(), progress, cancellationToken)
                .ConfigureAwait(false);
            if (nodeExit != 0)
            {
                throw new InvalidOperationException($"nvm 安装 Node.js 失败（退出码 {nodeExit}），请查看输出日志。");
            }

            await _runner.RunAsync(server, RemoteDshCommands.LinkNvmToPath(), cancellationToken).ConfigureAwait(false);
        }

        await _runner.RunAsync(server, RemoteDshCommands.EnsurePathPersistence(), cancellationToken).ConfigureAwait(false);
        progress.Report("Node.js 就绪。");
    }

    private async Task<string?> ReadPanelUrlAsync(int remotePort, CancellationToken cancellationToken)
    {
        var output = (await _runner.RunAsync(server, RemoteDshCommands.ReadPanelUrl(remotePort), cancellationToken)
            .ConfigureAwait(false)).Output.Trim();
        return output.Length == 0 ? null : UrlPattern.Match(output) is { Success: true } match ? match.Value : null;
    }

    /// <summary>解析 ProbeDsh 的结构化输出：未安装返回 null；找到但跑不起来时把原因放进 detail。</summary>
    private static (string? InstalledVersion, string? Detail) ParseDshProbe(string output, string nodeVersion)
    {
        var lines = ParseKeyValueLines(output);
        var versionText = lines.GetValueOrDefault("dsh-version", "missing");
        return versionText switch
        {
            "missing" or "" => (null, null),
            _ when VersionPattern.IsMatch(versionText) => (versionText, null),
            _ => (null, TryGetNodeMajor(nodeVersion) is { } major && major < MinimumNodeMajor
                ? $"dsh 无法运行：Node {nodeVersion} 过旧（需要 {MinimumNodeMajor}+），点「安装」自动升级"
                : $"dsh 无法运行：{versionText}")
        };
    }

    /// <summary>node 版本形如 v22.11.0；解析失败返回 null。</summary>
    internal static int? TryGetNodeMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "missing") return null;
        var text = version.TrimStart('v', 'V');
        var major = text.Split('.')[0];
        return int.TryParse(major, out var value) && value > 0 ? value : null;
    }

    private static Dictionary<string, string> ParseKeyValueLines(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator]] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    /// <summary>存活 = 输出里存在 1xx-5xx 的真实状态码；"000"（连接失败）不算。容许多行冗余输出。</summary>
    private static bool IsHttpAlive(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.Length == 3 && line.All(char.IsAsciiDigit) && line != "000");
}

/// <summary>按服务器定义创建 IRemoteDshService 实例。</summary>
public sealed class DshRemoteServiceFactory(SshSessionPool pool, ILogger<RemoteDshService> logger)
{
    public IRemoteDshService Create(DshServerDefinition server, DshConnectCredential? credential = null) =>
        new RemoteDshService(server, pool, logger, credential);

    /// <summary>服务器配置变更或删除后丢弃旧连接，下次操作按新配置重连。</summary>
    public void DropConnection(string serverId) => pool.Drop(serverId);
}
