using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>
/// 面板本地反向代理（原始 TCP 实现，无 HTTP 服务器依赖）：
/// - 浏览器 → 代理(LocalPort) → 隧道(UpstreamPort) → 远端回环 web；
/// - WebSocket 升级请求按字节原样中继（Host/Cookie 原样保留，DSH 的浏览器 Cookie 按 Host authority 绑定签名，
///   任何 Host 改写都会导致 401 无限重连），升级后按裸字节双向泵，帧保持不透明；
/// - 其余请求：请求头原样转发 + 按请求体长度补传；响应中 text/html 注入"服务器名 · "标题脚本并重写 Content-Length，
///   其余响应原样透传（含 chunked）。
/// </summary>
public sealed class DshPanelProxy : IAsyncDisposable
{
    /// <summary>HTML 注入缓冲上限；超过则放弃注入直接透传。</summary>
    private const int MaxHtmlBufferBytes = 20 * 1024 * 1024;
    private const int CopyBufferSize = 32 * 1024;

    private readonly TcpListener _listener;
    private readonly string _serverName;
    private readonly int _upstreamPort;
    private readonly int _proxyPort;
    private readonly ILogger _logger;

    private DshPanelProxy(TcpListener listener, string serverName, int upstreamPort, int proxyPort, ILogger logger)
    {
        _listener = listener;
        _serverName = serverName;
        _upstreamPort = upstreamPort;
        _proxyPort = proxyPort;
        _logger = logger;
    }

    public int Port => _proxyPort;

    public static DshPanelProxy Start(
        string serverName, int proxyPort, int upstreamPort, ILogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, proxyPort);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException($"无法在本机端口 {proxyPort} 启动面板代理：{exception.Message}", exception);
        }

        var proxy = new DshPanelProxy(listener, serverName, upstreamPort, proxyPort, logger);
        _ = proxy.AcceptLoopAsync();
        logger.LogInformation("面板代理已启动：127.0.0.1:{Port} → 127.0.0.1:{Upstream}", proxyPort, upstreamPort);
        return proxy;
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient browser;
            try
            {
                browser = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                _logger.LogWarning(exception, "面板代理接受连接失败");
                continue;
            }

            _ = HandleConnectionAsync(browser);
        }
    }

    private async Task HandleConnectionAsync(TcpClient browser)
    {
        try
        {
            using (browser)
            await using (var browserStream = browser.GetStream())
            {
                while (true)
                {
                    // 逐请求循环：同一连接上浏览器可能连续发起多个请求（keep-alive）
                    var head = await ReadHeadersAsync(browserStream).ConfigureAwait(false);
                    if (head.Length == 0) return; // 浏览器关闭连接

                    var headText = Encoding.ASCII.GetString(head);
                    var firstLine = headText.Split("\r\n")[0];
                    var isUpgrade = firstLine.Contains("websocket", StringComparison.OrdinalIgnoreCase)
                        && firstLine.Contains("upgrade", StringComparison.OrdinalIgnoreCase)
                        || headText.Contains("\r\nUpgrade: websocket", StringComparison.OrdinalIgnoreCase)
                        || headText.Contains("\r\nupgrade: websocket", StringComparison.OrdinalIgnoreCase);

                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, _upstreamPort).ConfigureAwait(false);
                    await using var upstreamStream = upstream.GetStream();

                    if (isUpgrade)
                    {
                        // WebSocket：请求头原样转发（Host/Cookie/凭证保持浏览器原值），此后裸字节双向泵。
                        // 升级连接的生命周期即连接生命周期，处理完本次即返回。
                    await upstreamStream.WriteAsync(head.AsMemory(0, head.Length), CancellationToken.None).ConfigureAwait(false);
                        var upstreamToBrowser = CopyAsync(upstreamStream, browserStream);
                        var browserToUpstream = CopyAsync(browserStream, upstreamStream);
                        await Task.WhenAny(upstreamToBrowser, browserToUpstream).ConfigureAwait(false);
                        return;
                    }

                    // 非 WS：转发请求头（剥离 Accept-Encoding，保证上游回明文可注入）+ 按请求体长度补传
                    var keptLines = headText.Split("\r\n")
                        .Where(line => !line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    while (keptLines.Count > 0 && keptLines[^1].Length == 0) keptLines.RemoveAt(keptLines.Count - 1);
                    var forwardedHead = string.Join("\r\n", keptLines) + "\r\n\r\n";
                    await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(forwardedHead)).ConfigureAwait(false);
                    var requestContentLength = ParseContentLength(headText);
                    if (requestContentLength > 0)
                    {
                        await CopyExactlyAsync(browserStream, upstreamStream, requestContentLength.Value).ConfigureAwait(false);
                    }

                    // 读上游响应头，判断注入与传输方式
                    var responseHead = await ReadHeadersAsync(upstreamStream).ConfigureAwait(false);
                    var upstreamFirstLine = Encoding.ASCII.GetString(responseHead).Split("\n")[0].Trim('\r');
                    if (responseHead.Length == 0) return;
                    var responseText = Encoding.ASCII.GetString(responseHead);
                    var (contentType, isChunked, bodyLength) = ParseResponseMeta(responseText);
                    var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
                    var statusCodeToken = responseText.Split("\r\n")[0].Split(' ')[1];
                    var isInjectable = statusCodeToken == "200";

                    // 完整读取响应体（chunked 解块 / 定长直读），再决定注入或透传。
                    // chunked 必须解块：若按裸流双泵，keep-alive 上游永不 EOF，连接会被占死导致页面空白。
                    byte[] body;
                    var canBuffer = (!isChunked && bodyLength is >= 0 && bodyLength <= MaxHtmlBufferBytes)
                        || (isChunked && (bodyLength is null || bodyLength <= MaxHtmlBufferBytes));
                    if (canBuffer)
                    {
                        body = isChunked
                            ? await ReadDeChunkedAsync(upstreamStream).ConfigureAwait(false)
                            : await ReadExactlyAsync(upstreamStream, bodyLength ?? 0).ConfigureAwait(false);
                    }
                    else
                    {
                        // 超大响应：放弃注入，按定长/裸流透传，并声明 Connection: close（透传无法界定结束）
                        var headWithClose = responseText.Contains("Connection:", StringComparison.OrdinalIgnoreCase)
                            ? responseText
                            : responseText[..^2] + "Connection: close\r\n\r\n";
                        await browserStream.WriteAsync(Encoding.ASCII.GetBytes(headWithClose)).ConfigureAwait(false);
                        if (!isChunked && bodyLength is > 0)
                        {
                            await CopyExactlyAsync(upstreamStream, browserStream, bodyLength.Value).ConfigureAwait(false);
                        }
                        else
                        {
                            await CopyAsync(upstreamStream, browserStream).ConfigureAwait(false);
                        }
                        return;
                    }

                    if (isHtml && isInjectable)
                    {
                        var encoding = GetEncoding(ParseCharset(responseText));
                        var injected = InjectTitleScript(encoding.GetString(body), _serverName);
                        body = encoding.GetBytes(injected);
                    }

                    // 解块后以 Content-Length 形式回给浏览器（keep-alive 下也能准确定位响应结束）
                    var finalizedHead = FinalizeResponseHead(responseText, body.Length);
                    var headBytes = Encoding.ASCII.GetBytes(finalizedHead);
                    await browserStream.WriteAsync(headBytes.AsMemory(0, headBytes.Length)).ConfigureAwait(false);
                    if (body.Length > 0)
                    {
                        await browserStream.WriteAsync(body.AsMemory(0, body.Length)).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "面板代理连接结束（浏览器或上游断开）");
        }
    }

    /// <summary>在 HTML 的 head 开标签后注入标题脚本（最先执行，先于应用的标题设置）；
    /// 无 head 时依次尝试 body/文档头。注意不能插到 &lt;/title&gt; 之前——那会落入 title 的 RCDATA 不执行。</summary>
    internal static string InjectTitleScript(string html, string serverName)
    {
        var script = BuildTitleScript(serverName);
        var index = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var close = html.IndexOf('>', index);
            if (close >= 0) return html.Insert(close + 1, script);
        }

        index = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var close = html.IndexOf('>', index);
            if (close >= 0) return html.Insert(close + 1, script);
        }

        return script + html;
    }

    internal static string BuildTitleScript(string serverName)
    {
        // JSON 字符串字面量即安全的 JS 字符串（服务器名可能含引号等字符）
        var name = System.Text.Json.JsonSerializer.Serialize(serverName);
        return "<script>window.__ompcTitleFix=1;(function(){var p=" + name + "+\"\\u00B7 \";" +
               "function f(){var t=document.title||\"\";if(t.indexOf(p)===0)return;document.title=p+t}" +
               "document.addEventListener(\"DOMContentLoaded\",function(){f();new MutationObserver(f).observe(document.head,{subtree:true,childList:true,characterData:true})})})();</script>";
    }

    internal static int? ParseContentLength(string headText)
    {
        foreach (var line in headText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    internal static (string ContentType, bool IsChunked, int? BodyLength) ParseResponseMeta(string responseHead)
    {
        var lines = responseHead.Split("\r\n");
        string contentType = "";
        var isChunked = false;
        int? bodyLength = null;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && value.Contains("chunked", StringComparison.OrdinalIgnoreCase)) isChunked = true;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var length)) bodyLength = length;
        }

        return (contentType, isChunked, bodyLength);
    }

    internal static string ParseCharset(string responseHead)
    {
        var index = responseHead.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "utf-8";
        var value = responseHead[(index + "charset=".Length)..].Split("\r\n")[0].Trim().Trim('"', ';', ' ');
        var end = value.IndexOfAny([';', ' ']);
        return end > 0 ? value[..end] : value;
    }

    /// <summary>把响应头中的 Content-Length 改写为注入后的长度。</summary>
    internal static string RewriteContentLength(string responseHead, int newLength)
    {
        var lines = responseHead.Split("\r\n");
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"Content-Length: {newLength}";
                break;
            }
        }

        return string.Join("\r\n", lines);
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[CopyBufferSize];
        while (true)
        {
            var read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            if (read == 0) return;
            await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private static async Task CopyExactlyAsync(NetworkStream from, NetworkStream to, long count)
    {
        var remaining = count;
        var buffer = new byte[CopyBufferSize];
        while (remaining > 0)
        {
            var read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read == 0) return;
            await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }

        return result;
    }

    /// <summary>
    /// 逐字节读取到 CRLFCRLF（头边界）。必须逐字节：块读会把 chunked body 的前缀一起带回来，
    /// 导致解块器从流中间开始解析。头只有几百字节，无性能影响。
    /// </summary>
    private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1];
        while (memory.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1)).ConfigureAwait(false);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            var bytes = memory.ToArray();
            if (EndsWithDoubleCrlf(bytes)) return bytes;
        }

        return memory.ToArray();
    }

    /// <summary>读取 chunked 响应体并解块为原始字节（忽略 trailer）。</summary>
    private static async Task<byte[]> ReadDeChunkedAsync(NetworkStream stream)
    {
        using var memory = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream).ConfigureAwait(false);
            var sizeText = sizeLine.Split(';')[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size)) break;
            if (size == 0)
            {
                await ReadLineAsync(stream).ConfigureAwait(false);
                break;
            }

            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(offset, size - offset)).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }

            memory.Write(chunk, 0, offset);
            await ReadLineAsync(stream).ConfigureAwait(false);
        }

        return memory.ToArray();
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer.AsMemory(0, 1)).ConfigureAwait(false) == 1)
        {
            if (buffer[0] == 10) break; // LF
            if (buffer[0] != 13) line.Append((char)buffer[0]); // skip CR
        }

        return line.ToString();
    }

    /// <summary>响应头最终化：移除 Transfer-Encoding，设置/替换 Content-Length，保留其余头。</summary>
    internal static string FinalizeResponseHead(string responseHead, int bodyLength)
    {
        var lines = responseHead.Split("\r\n");
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(line);
        }

        var insertAt = kept.Count >= 1 ? 1 : 0; // 状态行之后
        kept.Insert(Math.Min(insertAt, kept.Count), $"Content-Length: {bodyLength}");
        return string.Join("\r\n", kept) + "\r\n\r\n"; // 头结束需空行，body 才能被浏览器正确分帧
    }

    private static bool EndsWithDoubleCrlf(byte[] bytes) =>
        bytes.Length >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10;

    private static Encoding GetEncoding(string charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding(charset.Trim('"', ' ', ';'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
    }
}
