using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>
/// 每台服务器维持一条懒加载 SSH 连接。首次信任顺序：已存指纹 → known_hosts → 人工确认，
/// 均未命中则拒绝连接，防止中间人。
/// </summary>
public sealed class SshSessionPool : IDisposable
{
    private static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];
    private readonly IAppStore _store;
    private readonly DshInteractionPrompts _prompts;
    private readonly ILogger<SshSessionPool> _logger;
    private readonly Dictionary<string, PoolEntry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SshSessionPool(IAppStore store, DshInteractionPrompts prompts, ILogger<SshSessionPool> logger)
    {
        _store = store;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<SshClient> AcquireAsync(
        DshServerDefinition server,
        CancellationToken cancellationToken = default,
        DshConnectCredential? credential = null)
    {
        PoolEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(server.Id, out var existing))
            {
                entry = existing;
            }
            else
            {
                entry = new PoolEntry();
                _entries[server.Id] = entry;
            }
        }
        finally
        {
            _gate.Release();
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Client is { IsConnected: true } connected) return connected;
            entry.Client?.Dispose();
            entry.Client = await ConnectAsync(server, cancellationToken, credential).ConfigureAwait(false);
            return entry.Client;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>丢弃某台服务器的连接（命令执行失败后调用，下次操作自动重连）。</summary>
    public void Drop(string serverId)
    {
        lock (_entries)
        {
            if (_entries.Remove(serverId, out var entry))
            {
                entry.Client?.Dispose();
                entry.Client = null;
            }
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Client?.Dispose();
                entry.Client = null;
            }
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }

    private async Task<SshClient> ConnectAsync(
        DshServerDefinition server,
        CancellationToken cancellationToken,
        DshConnectCredential? credential = null)
    {
        var knownHostsContent = TryReadKnownHosts();
        DshHostKeyInfo? presented = null;

        for (var attempt = 0; ; attempt++)
        {
            var client = await CreateClientAsync(server, cancellationToken, credential).ConfigureAwait(false);
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = KnownHosts.Fingerprint(e.HostKey);
                presented = new DshHostKeyInfo(server.Host, server.SshPort, fingerprint, e.HostKeyName);
                var knownMatch = knownHostsContent is not null
                    && KnownHosts.GetKeyBlobs(knownHostsContent, server.Host, server.SshPort)
                        .Contains(Convert.ToBase64String(e.HostKey), StringComparer.Ordinal);
                e.CanTrust = string.Equals(server.HostKeyFingerprint, fingerprint, StringComparison.Ordinal)
                    || knownMatch;
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (presented is not null && server.HostKeyFingerprint is null)
                {
                    // known_hosts 命中的场合把指纹落库，之后不再依赖文件
                    await PersistFingerprintAsync(server, presented, cancellationToken).ConfigureAwait(false);
                }
                return client;
            }
            catch (Exception exception) when (attempt == 0 && presented is not null && exception is not OperationCanceledException)
            {
                // 首次尝试未受信（或 known_hosts 不匹配）：请人确认后重试一次
                client.Dispose();
                _logger.LogInformation(
                    "服务器 {Host}:{Port} 呈现未知主机密钥 {Fingerprint}，等待信任确认",
                    server.Host, server.SshPort, presented.Fingerprint);
                var info = presented;
                if (!await EnsureTrustedAsync(server, info, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"已拒绝 {server.Host}:{server.SshPort} 的主机密钥（{info.Fingerprint}），连接取消。");
                }
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private async Task<SshClient> CreateClientAsync(
        DshServerDefinition server,
        CancellationToken cancellationToken,
        DshConnectCredential? credential = null)
    {
        AuthenticationMethod authentication = server.AuthKind switch
        {
            DshAuthKind.Password => new PasswordAuthenticationMethod(
                server.UserName, await ResolvePasswordAsync(server, cancellationToken, credential).ConfigureAwait(false)),
            _ => new PrivateKeyAuthenticationMethod(
                server.UserName, await ResolveKeyFilesAsync(server, cancellationToken).ConfigureAwait(false))
        };
        return new SshClient(new ConnectionInfo(server.Host, server.SshPort, server.UserName, authentication))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };
    }

    private async Task<string> ResolvePasswordAsync(
        DshServerDefinition server,
        CancellationToken cancellationToken,
        DshConnectCredential? credential = null)
    {
        if (credential?.Password is { } overridePassword) return overridePassword;
        var password = await _store.GetDshPasswordAsync(server.Id, cancellationToken).ConfigureAwait(false);
        return password ?? throw new InvalidOperationException(
            $"服务器 {server.Name} 配置为密码登录，但尚未保存密码；请在服务器设置中填写。");
    }

    private async Task<PrivateKeyFile[]> ResolveKeyFilesAsync(DshServerDefinition server, CancellationToken cancellationToken)
    {
        List<string> paths = [];
        if (!string.IsNullOrWhiteSpace(server.KeyPath))
        {
            paths.Add(ExpandHome(server.KeyPath));
        }
        else
        {
            paths.AddRange(DefaultKeyNames
                .Select(name => Path.Combine(SshHome, name))
                .Where(File.Exists));
        }

        if (paths.Count == 0)
        {
            throw new InvalidOperationException(
                "未找到可用的 SSH 私钥：请在 ~/.ssh 放置默认密钥（id_ed25519/id_ecdsa/id_rsa）或在服务器设置中指定密钥文件。");
        }

        List<PrivateKeyFile> files = [];
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                files.Add(new PrivateKeyFile(path));
            }
            catch (Exception exception) when (
                exception is CryptographicException or SshException or FormatException or ArgumentException)
            {
                var passphrase = _prompts.RequestKeyPassphrase is null
                    ? null
                    : await _prompts.RequestKeyPassphrase(path).ConfigureAwait(false);
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new InvalidOperationException($"私钥 {path} 无法读取（可能已加密且未提供口令）。", exception);
                }

                files.Add(new PrivateKeyFile(path, passphrase));
            }
        }

        return [.. files];
    }

    private async Task<bool> EnsureTrustedAsync(DshServerDefinition server, DshHostKeyInfo info, CancellationToken cancellationToken)
    {
        if (_prompts.RequestHostKeyTrust is null)
        {
            throw new InvalidOperationException(
                $"服务器 {server.Host}:{server.SshPort} 的主机密钥尚未受信（{info.Fingerprint}），且没有可用的确认渠道。");
        }

        if (!await _prompts.RequestHostKeyTrust(info).ConfigureAwait(false)) return false;
        await PersistFingerprintAsync(server, info, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task PersistFingerprintAsync(DshServerDefinition server, DshHostKeyInfo info, CancellationToken cancellationToken)
    {
        server.HostKeyFingerprint = info.Fingerprint;
        try
        {
            await _store.SaveDshServerAsync(server, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "保存服务器 {Host} 的主机密钥指纹失败，下次连接可能需要重新确认", server.Host);
        }
    }

    private static string SshHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    private static string? TryReadKnownHosts()
    {
        try
        {
            var path = Path.Combine(SshHome, "known_hosts");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;

    private sealed class PoolEntry
    {
        public SshClient? Client { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
