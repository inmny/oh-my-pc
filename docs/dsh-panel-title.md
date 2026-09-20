# DSH 面板标签页标识方案（已按方案 A 实现）

## 问题

通过「打开」在浏览器里开多个远端 DSH 面板时，每个标签页标题都是 DSH 设置的"当前会话名"，无法分辨哪台是哪台。期望：标签页变成 `服务器名 · 会话名`（服务器名在最左）。

## 为什么不能"顺手"改

标签页标题 = 页面 `document.title`，由 DSH web 前端动态设置（会话切换时会变）。OhMyPc 与远端之间目前是**纯 TCP 隧道**（`ForwardedPortLocal`），流量逐字节透传，应用层上没有任何可插入标题的位置；远端 DSH 也不知道自己在 OhMyPc 里叫"MyServer"。所以必须在"浏览器看到的 HTML"层面动手，或者换一个我们能控制的渲染容器。

## 方案 A：本地反向代理 + HTML 注入（浏览器工作流不变）

### 原理

把"浏览器 → 隧道 → 远端"变成"浏览器 → 本地反代 → 隧道 → 远端"：

```
浏览器 ──HTTP/WS──> 本地代理(Kestrel, 绑 LocalPort) ──> 隧道(ForwardedPortLocal, 绑自动挑选的内部端口) ──> 远端 127.0.0.1:3080
```

代理对响应做一件事：`Content-Type: text/html` 的响应（就是面板外壳 HTML，体积小）先缓冲，在 `<title>` 前注入一段内联脚本，再改写 Content-Length 返回；其余请求（静态资源、API、下载）逐字节透传。注入的脚本用 MutationObserver 盯住 `<title>`，一旦变化就加回前缀：

```js
(function(){var p="MyServer · ";var f=()=>{if(document.title&&!document.title.startsWith(p))document.title=p+document.title};new MutationObserver(f).observe(document.head,{subtree:true,childList:true,characterData:true});f()})();
```

服务器名由代理按"哪个 LocalPort 对应哪台服务器"内联到脚本里，浏览器端零配置。

### 要点与对策

| 点 | 处理 |
|---|---|
| WebSocket（会话实时输出必需） | ASP.NET Core 检测 upgrade 请求 → `ClientWebSocket` 连上游 → 双向泵；Host/Origin 头保持 `127.0.0.1:LocalPort`（与现有 `--trusted-host` 信任域一致，fence 不破） |
| 压缩响应无法注入 | 转发时剥掉请求的 `Accept-Encoding`（回环链路无所谓带宽），上游一律返回未压缩内容 |
| Content-Length | 注入后重算 |
| 隧道端口 | `LocalPort` 让给代理；`ForwardedPortLocal` 改绑自动挑选的空闲内部端口（沿用现有"探测空闲端口"逻辑） |
| SSE/流式 | 非注入路径直接流式拷贝，不缓冲 |
| 注入兜底 | HTML 无 `<title>` 时在 `<head>` 开头插入自建 title；脚本带前缀幂等检查 |

### 已知限制（登记，不阻塞）

- 若 DSH web 将来引入 Service Worker 缓存外壳 HTML，SW 命中的缓存绕过代理注入 → 标题增强降级失效（页面功能不受影响）。当前版本未观察到 SW。
- 每台服务器多一个本地监听 + 一跳用户态转发，回环链路可忽略。
- Cookie 按 127.0.0.1 域共享（与现状一致，token 本就按端口 URL 携带，风险不变）。

### 工程量与风险

约 1~2 天。核心风险集中在 WebSocket 代理的真机验证（会话实时流、断线重连），需要对 TencentCloud/MyServer 各跑一轮完整会话。

## 方案 B：WebView2 内嵌面板窗口（实现最轻）

不再用系统浏览器，「打开」改为在 OhMyPc 内嵌的 WebView2 窗口/标签中打开面板。WebView2 提供 `AddScriptToExecuteOnDocumentCreatedAsync`——每次导航自动注入同款标题脚本，**不需要任何代理**，隧道保持现状。

- 额外收益：窗口标题同样自动带服务器名；可以做成一个"面板浏览器"多标签窗口（TabControl + 每服务器一个 WebView2），面板集中在应用内管理。
- 代价：打开方式从"系统浏览器"变为"应用内窗口"；新增 `Microsoft.Web.WebView2` 依赖（Evergreen 运行时，Win10/11 通常自带，缺失需引导安装）。
- 工程量约半天，无网络层风险。

## 方案对比

| | A 反代注入 | B WebView2 内嵌 |
|---|---|---|
| 标签页在哪 | 系统浏览器（工作流不变） | OhMyPc 内部窗口 |
| 标题增强 | 全浏览器标签生效 | 仅应用内窗口生效 |
| 工程量 | 1~2 天 | ~半天 |
| 主要风险 | WS 代理真机验证 | WebView2 运行时可用性 |
| 后续延展 | 离线占位页、多服务器聚合入口页、面板可达性监测 | 面板标签页管理、关闭自动清理隧道 |

## 建议

如果"在系统浏览器里开一堆标签"是你的主要使用方式 → 选 A（痛点解决得最彻底，代理层也是后续聚合能力的地基）。
如果可以接受面板收进 OhMyPc 里 → 选 B，半天落地且零风险；两者不互斥（可先 B 后 A）。

标题格式默认 `服务器名 · 会话名`（本机面板不处理）。
