using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using Renci.SshNet;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>
/// SSH 隧道 + 面板代理的组合：隧道把远端回环 web 转发到本机内部端口（自动挑选），
/// 面板代理占用服务器配置的 LocalPort 对外服务——浏览器访问代理，代理经隧道转发到远端，
/// 并在 HTML 中注入"服务器名 · "标题脚本。会话断开时两者一并失效（Active/IsOpen 均按实时状态返回）。
/// </summary>
public sealed class DshTunnelService(SshSessionPool pool, ILogger<DshTunnelService> logger) : IDshTunnelService, IDisposable
{
    private readonly Dictionary<string, TunnelEntry> _tunnels = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? StateChanged;

    public IReadOnlyList<DshTunnelHandle> Active =>
        [.. _tunnels.Values.Where(entry => entry.IsLive).Select(entry => entry.Handle)];

    public async Task<DshTunnelHandle> OpenAsync(DshServerDefinition server, CancellationToken cancellationToken = default)
    {
        // 先在锁外拿连接：首次连接可能触发指纹确认等 UI 交互
        var client = await pool.AcquireAsync(server, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tunnels.TryGetValue(server.Id, out var existing) && existing.IsLive)
            {
                return existing.Handle;
            }

            RemoveStale(server.Id);
            EnsureLocalPortFree(server.LocalPort);
            var upstreamPort = PickFreeTcpPort(server.LocalPort);

            var port = new ForwardedPortLocal("127.0.0.1", (uint)upstreamPort, "127.0.0.1", (uint)server.RemotePort);
            client.AddForwardedPort(port);
            port.Start();

            DshPanelProxy proxy;
            try
            {
                proxy = DshPanelProxy.Start(server.Name, server.LocalPort, upstreamPort, logger);
            }
            catch (Exception)
            {
                StopQuietly(new TunnelEntry(port, NonLiveHandle(server, upstreamPort), null));
                throw;
            }

            var handle = new DshTunnelHandle(server.Id, server.Name, server.LocalPort, server.RemotePort, upstreamPort);
            _tunnels[server.Id] = new TunnelEntry(port, handle, proxy);
            logger.LogInformation(
                "面板代理已就绪：{Server} 浏览器 127.0.0.1:{Local} → 代理 → 隧道 127.0.0.1:{Upstream} → 远端 127.0.0.1:{Remote}",
                server.Name, server.LocalPort, upstreamPort, server.RemotePort);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return handle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Close(string serverId)
    {
        _gate.Wait();
        try
        {
            RemoveStale(serverId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsOpen(string serverId)
    {
        lock (_tunnels)
        {
            return _tunnels.TryGetValue(serverId, out var entry) && entry.IsLive;
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            foreach (var entry in _tunnels.Values)
            {
                StopQuietly(entry);
            }

            _tunnels.Clear();
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }

    private void RemoveStale(string serverId)
    {
        if (!_tunnels.Remove(serverId, out var entry)) return;
        StopQuietly(entry);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopQuietly(TunnelEntry entry)
    {
        if (entry.Proxy is not null)
        {
            entry.Proxy.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        try
        {
            if (entry.Port.IsStarted) entry.Port.Stop();
            entry.Port.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "关闭隧道 {LocalPort} 失败", entry.Handle.LocalPort);
        }
    }

    private static void EnsureLocalPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException($"本地端口 {port} 已被占用，请在服务器设置中更换本地端口。", exception);
        }
    }

    /// <summary>挑一个系统空闲端口作为隧道内部绑定端口，排除面向浏览器的代理端口。</summary>
    private static int PickFreeTcpPort(int exclude)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != exclude) return port;
        }

        throw new InvalidOperationException("无法挑选可用的内部隧道端口。");
    }

    private static DshTunnelHandle NonLiveHandle(DshServerDefinition server, int upstreamPort) =>
        new(server.Id, server.Name, server.LocalPort, server.RemotePort, upstreamPort);

    private sealed record TunnelEntry(ForwardedPortLocal Port, DshTunnelHandle Handle, DshPanelProxy? Proxy)
    {
        public bool IsLive => Port.IsStarted && Proxy is not null;
    }
}
