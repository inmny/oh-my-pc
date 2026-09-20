using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.IntegrationTests;

/// <summary>DshPanelProxy 的端到端验证：本地桩上游 + 断言注入/透传/WS。</summary>
public sealed class DshPanelProxyTests : IAsyncLifetime
{
    private WebApplication _upstream = null!;
    private int _upstreamPort;
    private DshPanelProxy? _proxy;
    private int _proxyPort;

    private const string ShellHtml = """
        <!doctype html>
        <html>
        <head><title>会话甲</title></head>
        <body>panel-body</body>
        </html>
        """;

    public async Task InitializeAsync()
    {
        _upstream = BuildUpstream();
        await _upstream.StartAsync();
        _upstreamPort = FirstPort(_upstream);
        _proxyPort = PickFreePort();
    }

    public async Task DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
        await _upstream.StopAsync();
        _upstream.DisposeAsync();
    }

    [Fact]
    public async Task HtmlResponse_GetsServerNamePrefixInjectedIntoTitle()
    {
        _proxy = StartProxy();

        using var client = new System.Net.Http.HttpClient();
        var html = await client.GetStringAsync($"http://127.0.0.1:{_proxyPort}/");

        // 注入只添加脚本（前缀由脚本在运行时拼接），脚本位于 head 开标签后、原 title 内容保持不变
        Assert.Contains("<head><script>", html);
        Assert.Contains("<title>会话甲</title>", html);
        Assert.Contains("\"MyServer\"", html);
        Assert.Contains("MutationObserver", html);
        Assert.Contains("__ompcTitleFix", html);
        Assert.Contains("panel-body", html);
    }

    [Fact]
    public async Task NonHtmlResponse_PassesThroughUnmodified()
    {
        _proxy = StartProxy();

        using var client = new System.Net.Http.HttpClient();
        var js = await client.GetStringAsync($"http://127.0.0.1:{_proxyPort}/static/app.js");

        Assert.Equal("console.log('no-inject');", js.Trim());
    }

    [Fact]
    public async Task HostHeader_PreservedAsProxyAuthority()
    {
        _proxy = StartProxy();

        using var client = new System.Net.Http.HttpClient();
        var host = await client.GetStringAsync($"http://127.0.0.1:{_proxyPort}/api/host");

        // Host 头必须保持 LocalPort 权威，与远端 --trusted-host 信任域一致
        Assert.Equal($"127.0.0.1:{_proxyPort}", host.Trim());
    }

    [Fact]
    public async Task WebSocket_IsProxiedBothWays()
    {
        _proxy = StartProxy();

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", "dsh-auth=regression");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_proxyPort}/ws"), CancellationToken.None);

        var payload = Encoding.UTF8.GetBytes("ping-through-proxy");
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
        var received = new byte[64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(received), CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.Equal("ping-through-proxy", Encoding.UTF8.GetString(received, 0, result.Count));
    }

    [Fact]
    public void InjectTitleScript_InsertsAfterHeadOpening()
    {
        var html = DshPanelProxy.InjectTitleScript(
            "<html><head><meta charset=\"utf-8\"><title>s</title></head><body></body></html>", "S1");

        // 脚本必须紧跟 head 开标签（先于应用的标题 JS 执行），且不得落入 title 的 RCDATA
        Assert.StartsWith("<html><head><script>", html);
        Assert.True(html.IndexOf("<script>", StringComparison.Ordinal) < html.IndexOf("<title>", StringComparison.Ordinal));
        Assert.Contains("MutationObserver", html);
        Assert.Contains("__ompcTitleFix", html);
    }

    [Fact]
    public void InjectTitleScript_FallsBackToBodyThenDocument()
    {
        var body = DshPanelProxy.InjectTitleScript("<html><body>hi</body></html>", "S1");
        Assert.StartsWith("<html><body><script>", body);

        var bare = DshPanelProxy.InjectTitleScript("plain", "S1");
        Assert.StartsWith("<script>", bare);
    }

    [Fact]
    public void BuildTitleScript_JsonEscapesServerName()
    {
        // System.Text.Json 默认转义把双引号写成字面反斜杠 + u0022，在 JS 字符串里安全
        var script = DshPanelProxy.BuildTitleScript("quote\"name");
        Assert.Contains("quote\\u0022name", script);
    }

    /// <summary>手动验证挂载点：设置 OMC_PROXY_HARNESS=1 时，把 127.0.0.1:13080 的代理指向
    /// OMC_PROXY_UPSTREAM（默认 3188）并保持 90 秒，供 curl / 浏览器实测。</summary>
    [Fact]
    public async Task KeepAliveHarness_ForManualChecks()
    {
        if (Environment.GetEnvironmentVariable("OMPC_PROXY_HARNESS") != "1") return;
        var upstreamPort = int.Parse(Environment.GetEnvironmentVariable("OMPC_PROXY_UPSTREAM") ?? "3188");
        await using var proxy = DshPanelProxy.Start("MyServer", 13080, upstreamPort, new ConsoleLogger());
        await Task.Delay(TimeSpan.FromSeconds(90));
    }

    private static WebApplication BuildUpstream()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            switch (context.Request.Path)
            {
                case "/":
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(ShellHtml);
                    break;
                case "/static/app.js":
                    context.Response.ContentType = "text/javascript";
                    await context.Response.WriteAsync("console.log('no-inject');");
                    break;
                case "/api/host":
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync(context.Request.Host.Value ?? "");
                    break;
                case "/api/data":
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("""{"ok":true}""");
                    break;
                case "/ws" when context.WebSockets.IsWebSocketRequest:
                {
                    // 模拟远端 dsh 的浏览器鉴权：无 Cookie 的握手必须被拒（回归守卫：代理需转发 Cookie）
                    if (!context.Request.Headers.ContainsKey("Cookie"))
                    {
                        context.Response.StatusCode = 401;
                        break;
                    }

                    try
                    {
                        var socket = await context.WebSockets.AcceptWebSocketAsync();
                        Console.WriteLine("[stub] ws accepted");
                        var buffer = new byte[64 * 1024];
                        while (socket.State == WebSocketState.Open)
                        {
                            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            Console.WriteLine($"[stub] echo {result.Count} bytes");
                            await socket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count),
                                result.MessageType, result.EndOfMessage, context.RequestAborted);
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine("[stub] ws error: " + exception);
                        throw;
                    }

                    break;
                }
                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        });
        return app;
    }

    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception is not null) Console.WriteLine(exception);
        }
    }

    private DshPanelProxy StartProxy()
    {
        var proxy = DshPanelProxy.Start(
            "MyServer", _proxyPort, _upstreamPort,
            new ConsoleLogger());
        _proxy = proxy;
        return proxy;
    }

    private static int FirstPort(WebApplication application) =>
        application.Urls.Select(url => new Uri(url)).First().Port;

    private static int PickFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
