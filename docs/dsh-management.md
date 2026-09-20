# DSH 实例管理（本机 + SSH 远端）

把分散的 DSH（DeepSeek Harness）实例集中到 Oh My PC 管理：本机实例的启动/停止/重启，SSH 远端服务器上的安装/更新/拉起，以及把远端 web 面板经隧道转发到本地一键使用。本文为设计方案，已按三期完整实现（本机管理 / SSH 远端管理 / 隧道转发），实现细节以代码为准。

## 调研事实（2026-09-18 本机实测）

1. DSH 是 npm 包 `@deepseek-ai/dsh`，`dsh web` 启动浏览器 UI。相关启动参数：`--host`（默认回环地址）、`--port`（`0` 表示由系统选空闲端口）、`--no-open`、`--trusted-host`（/api 信任域白名单）。
2. 启动后在 stdout 打印唯一带凭证的入口：`dsh web: http://127.0.0.1:63560/?token=...`。不带 token 访问 `/` 返回 401（提示 reopen the URL printed by dsh web）。
3. 由此可得存活探测方式：能建立 HTTP 连接（无论 401 还是 200）即运行中；连接拒绝或超时即未运行。探测不需要 token。
4. 版本查询：`dsh --version`（如 `0.1.6-alpha.2`）；最新版可查 npm registry `GET https://registry.npmjs.org/@deepseek-ai/dsh/latest` 的 `version` 字段。
5. 多实例可并发：实测主实例（3080）运行中再起一个随机端口实例，无锁冲突。
6. 安装形态：本机为 npm 全局安装（Git Bash/PowerShell 下 `dsh` 实为 npm 的 shim，Windows 上是 `dsh.cmd` 批处理壳，真进程是 node 子进程）；远端 Linux 同为 `npm install -g @deepseek-ai/dsh`。
7. 远端要求：Linux 服务器 + 已安装 Node.js/npm（v1 不自动装 Node，缺失时报错提示）。
8. 本机 `~/.ssh`（2026-09-18 实测）：`config` 共 8 个条目，形态为 `Host 别名` + `HostName/User/Port`，全部未写 `IdentityFile`，密钥即 `~/.ssh` 默认键（本机为 `id_rsa`）；条目中混有 `github.com` 等非服务器目标；`known_hosts` 已含历史连接记录。

## 目标与非目标

目标（v1）：

- 本机：启动/停止/重启 `dsh web`，展示状态与版本，提取并打开带 token 的面板 URL。
- 远端：维护服务器列表；服务器可从 `~/.ssh/config` 一键导入（含别名、地址、端口、用户），也可手动添加；SSH 连接默认复用 `~/.ssh` 里的密钥，另支持密码登录；连接后安装/更新 DSH、拉起/停止 `dsh web`、探测状态与版本。
- 隧道：把远端 web 转发到本地回环端口，拼接 token 一键打开浏览器。

非目标（v1）：

- 不支持 Windows 远端服务器（远端命令按 bash 语法生成）。
- 不支持 ssh-agent、ProxyJump/ProxyCommand 多跳；`~/.ssh/config` 只解析字面条目与 `Host *` 公共缺省，通配符匹配组不展开。
- 不管理远端会话与 profile 定制，只管 `dsh web` 的启停。

补充（2026-09-19 实现时调整）：远端缺 Node.js 时自动安装——root 或免密 sudo 走系统源（NodeSource，Debian/RHEL/Arch），否则 nvm 装用户级 Node 并把可执行文件链接进 `~/.local/bin`（同步写入各 shell 配置的 PATH）；npm 全局 bin 不在 PATH 时把 `dsh` 也链接进 `~/.local/bin` 兜底。dsh 探测改为结构化输出，可区分"未安装"与"已安装但运行异常（如 Node 过旧）"。

## 总体设计

沿用现有三层结构与既成熟模式（对齐 CLIProxyAPI 管理的实现方式）：

- Core：域模型 + 契约接口，无 IO。
- Infrastructure：新增 `Dsh/` 模块，承载 SSH、远端编排、隧道、本机进程与版本检查。
- App：新增一级页面 `DshView`（本机卡片 + 服务器列表 + 操作日志）。

### 契约（增补到 Core/Contracts.cs）

```csharp
public interface ILocalDshManager
{
    DshInstanceSnapshot Snapshot { get; }          // 状态/版本/端口/面板URL/是否外部实例
    event EventHandler? StateChanged;
    Task StartAsync(int port, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);
}

public interface IRemoteDshService                 // 每台服务器一个实例，由工厂按定义创建
{
    Task<DshProbeResult> ProbeAsync(CancellationToken ct = default);   // 已安装版本+存活
    Task InstallAsync(IProgress<string> log, CancellationToken ct = default);
    Task UpdateAsync(IProgress<string> log, CancellationToken ct = default);
    Task StartAsync(DshLaunchOptions options, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public interface IDshTunnelService
{
    event EventHandler? StateChanged;
    Task OpenAsync(DshServerDefinition server, int localPort, CancellationToken ct = default);
    void Close(DshServerDefinition server);
    IReadOnlyList<DshTunnelHandle> Active { get; } // 含本地地址与连接状态
}
```

`DshLaunchOptions` 至少含：远端端口、`--trusted-host` 需要的本地端口（见下文 token 与信任域一节）。SSH 执行细节（连接、认证、命令）封装在 Infrastructure 内部（`SshSessionPool`），不进契约。

### Infrastructure/Dsh/ 模块

**`SshConfigParser`（`~/.ssh/config` 解析）**

- 提取字面条目：`Host 别名`、`HostName`、`User`、`Port`、`IdentityFile`；`Host *` 块中的 `User/Port/IdentityFile` 作为公共缺省合并进各条目；通配符匹配组（如 `Host *gpu`）不展开、不导入。
- 别名作为应用内服务器显示名；无 `Port` 默认 22，无 `HostName` 视别名即地址。
- 导入动作把条目拷贝为应用内 `DshServer` 记录（之后可自由编辑，不再跟随 config 变化）；重复导入按 `HostName+Port+UserName` 幂等合并，已编辑过的记录不覆盖。
- 导入对话框复选列出全部条目，`github.com` 这类非服务器目标由用户自行勾选排除（不做特判）。

**`SshSessionPool`（SSH.NET 封装）**

- 认证两种：密钥（`IdentityFile` 指定的文件 → 否则依次尝试 `~/.ssh` 默认键 `id_ed25519`/`id_ecdsa`/`id_rsa` 中存在者）、密码（入库前经 `CredentialProtector` DPAPI 加密）。密钥带口令时弹窗输入，仅驻内存不落库。
- 首次信任优先复用 `known_hosts`：目标主机在 `known_hosts` 中有匹配记录（含哈希格式 `|1|` 比对）即自动信任并入库指纹；无记录时弹指纹确认（SHA256），确认后入库；后续连接指纹不符即拒绝，防中间人。
- 每台服务器维持一条懒加载连接；命令执行走 `SshCommand`（stdout/stderr 异步流，安装/更新日志流式回传 UI）；断线后下次操作自动重连。

**`RemoteDshService`（远端编排，命令均以 bash 语法生成）**

- Probe：`command -v dsh >/dev/null && dsh --version` 拿版本；`curl -so /dev/null -w '%{http_code}' http://127.0.0.1:<port>/` 拿存活（`000`=未运行，`401/200`=运行中）。
- Install/Update：`npm install -g @deepseek-ai/dsh@latest`，输出流式回传；缺 node 时前置检查 `command -v npm` 失败并给出明确错误。
- Start：写 `~/.oh-my-pc/dsh-web.log` 与 pid 文件并后台拉起：
  ```bash
  nohup dsh web --host 127.0.0.1 --port <remotePort> --no-open --trusted-host 127.0.0.1:<localPort> \
    >> ~/.oh-my-pc/dsh-web.log 2>&1 & echo $! > ~/.oh-my-pc/dsh-web.pid
  ```
  启动后轮询 `tail` 日志尾部，提取 `dsh web: http://127.0.0.1:<port>/?token=...` 得到面板 URL 与 token。
- Stop：`kill $(cat ~/.oh-my-pc/dsh-web.pid) 2>/dev/null`；pid 文件缺失但端口有响应时，状态报"外部实例运行中"（用户手动/nohup 自启的），只读展示，不提供停止。
- 不采用 systemd user unit：兼容性坑多（lingering、无 systemd 环境）；`nohup`+pid 文件最简且够用，后续有需求再演进。

**`DshTunnelService`（转发）**

- SSH.NET `ForwardedPortLocal`：绑定 `127.0.0.1:<localPort>` → 远端 `127.0.0.1:<remotePort>`。远端 web 只绑回环，隧道正是安全通道，无需在服务器防火墙暴露 web 端口。
- 打开前检测本地端口占用（尝试绑定即知）；会话断开时隧道标记失效并回显状态；应用退出时全部关闭。
- 隧道句柄含本地地址，用于"打开面板"按钮与状态展示。

**`LocalDshProcessService`（本机，对齐 `CliProxyProcessService` 的形态）**

- 定位 dsh：沿 PATH 查找 `dsh.cmd`/`dsh`/`dsh.exe`（Windows 下用 `where dsh` 逻辑）；找不到报"未安装 DSH"。
- Start：隐藏窗口启动 `dsh web --port <port> --no-open`，订阅 stdout 用正则提取面板 URL（token 只驻内存，不落库）。
- 停止：Windows 上 `dsh.cmd` 是批处理壳，需按进程树终止（`taskkill /T` 或 Job Object），避免残留 node 子进程。
- 端口被占但不是本应用拉起的进程：报"外部实例运行中"，提供打开、不提供停止。
- 状态变化经 `StateChanged` 推给 ViewModel；额外用 HTTP 探测兜底（进程在但还在启动中时显示"启动中"）。

**`DshVersionClient`**

- 查 registry 最新版展示"当前 x.y → 最新 z.w"提示；直连失败（代理环境等）只降级为不显示，不阻塞任何操作。安装/更新始终由远端 npm 自行解析版本。

### token 与信任域（关键细节）

- 面板准入靠 URL 里的 token；打开地址固定拼 `http://127.0.0.1:<localPort>/?token=<token>`。
- 本地端口与远端端口不一致时，浏览器发出的 Host 头会变成 `127.0.0.1:<localPort>`，可能触发 `/api` 的信任域校验，因此远端 Start 时同步写入 `--trusted-host 127.0.0.1:<localPort>`。本地端口在服务器配置里固定，改动后需重启远端实例生效（实现时先实测仅凭 token 是否已足够，若够则 trusted-host 可省）。

### 数据模型（EF Core 迁移）

`DshServer` 实体：`Id`、`Name`（导入条目即 Host 别名）、`Host`、`SshPort`、`UserName`、`AuthKind`（Key/Password，默认 Key）、`KeyPath`（空 = `~/.ssh` 默认键）、`EncryptedPassword`、`RemotePort`（默认 3080）、`LocalPort`（默认 13080 起，配置时校验不冲突）、`HostKeyFingerprint`、`Note`、`CreatedAt`。密码经 `CredentialProtector` 加密；token 与运行态不入库。

### UI（新一级页面 "DSH"）

- 本机卡片：状态点（绿=运行中/黄=启动中/灰=已停止/蓝=外部实例）、版本、端口；按钮：启动/停止/重启/打开面板；面板 URL 可复制。
- 服务器列表：每行显示名称、host、状态（未连接/未安装/已停止/运行中/已转发）、远端版本与最新版提示；操作按钮：探测、安装、更新、启动、停止、转发并打开、关闭转发；列表上方提供"从 ~/.ssh 导入"入口。
- 操作日志区：安装/更新的流式输出。
- 添加/编辑服务器对话框：名称、host、SSH 端口、用户名、认证方式（密钥/密码；密钥可指定文件或留空用 `~/.ssh` 默认键）、远端端口、本地端口、"测试连接"（返回指纹确认与探测结果）。资源键进 `Strings.zh-CN.xaml`/`Strings.en-US.xaml`。

### 关键流程（转发并打开）

1. 连接会话（首次弹指纹确认）→ 2. Probe：未安装则引导安装，缺 node 则报错 → 3. 未运行则 Start（带 `--trusted-host`）并从日志提取 token → 4. 打开隧道 localPort→remotePort → 5. 打开 `http://127.0.0.1:<localPort>/?token=<token>`。

## 分期

1. 第 1 期（本机）：`LocalDshProcessService` + 页面本机卡片 + 面板 URL 打开。最小端到端，先验证 stdout 解析与进程树终止。
2. 第 2 期（远端）：服务器 CRUD + `~/.ssh/config` 导入 + SSH 连接（密钥/密码、known_hosts 复用与指纹确认）+ 安装/更新/启停/探测。
3. 第 3 期（隧道）：`DshTunnelService` + 转发并打开 + 状态整合。

每期独立可用，符合分层演进。

## 风险与开放问题

1. `--trusted-host` 的确切拦截范围需实测（伪造 Host 实测返回的也是 401，fence 可能只管 /api）；第 3 期验证，若仅 token 足够则简化。
2. 密钥直接读文件、不支持 ssh-agent 是 v1 明确取舍；覆盖 `~/.ssh` 默认键 + config `IdentityFile` 两种形态。若密钥带口令需每次会话输入一次。
3. 远端 npm 需能访问 registry（代理/镜像由服务器自身环境决定）；失败时展示原始输出，不做环境代劳。
4. 本机若用户自己在终端跑着 `dsh web`，只读识别为外部实例，不强抢管理权。
5. `~/.ssh/config` 只做单层解析（字面条目 + `Host *` 缺省），Match 块、通配符组、ProxyJump 均不展开；解析失败跳过该条目并记日志，不阻塞其余导入。
