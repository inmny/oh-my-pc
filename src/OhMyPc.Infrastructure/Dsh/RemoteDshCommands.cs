namespace OhMyPc.Infrastructure.Dsh;

/// <summary>远端系统的包管理器类别，决定 Node.js 的自动安装方式。</summary>
public enum DistroKind
{
    Debian,
    Rhel,
    Arch,
    Unsupported
}

/// <summary>远端 DSH 管理用到的 bash 命令构造；只生成命令文本，执行由 SshCommandRunner 负责。</summary>
public static class RemoteDshCommands
{
    public const string LogFile = "~/.oh-my-pc/dsh-web.log";
    public const string PidFile = "~/.oh-my-pc/dsh-web.pid";

    /// <summary>结构化探测 dsh：dsh-version=missing 表示未安装；有值但不是版本号说明装了但跑不起来（如 Node 过旧）。</summary>
    public static string ProbeDsh() =>
        "if command -v dsh >/dev/null 2>&1; then " +
        "printf \"dsh-version=%s\\n\" \"$(dsh --version 2>&1 | head -n 1)\"; " +
        "printf \"dsh-path=%s\\n\" \"$(command -v dsh)\"; " +
        "else echo \"dsh-version=missing\"; fi";

    /// <summary>结构化探测 Node 环境：node/npm 版本、是否 root、免密 sudo、发行版。</summary>
    public static string ProbeNode() =>
        "printf \"node=%s\\n\" \"$(command -v node >/dev/null 2>&1 && node --version 2>/dev/null || echo missing)\"; " +
        "printf \"npm=%s\\n\" \"$(command -v npm >/dev/null 2>&1 && npm --version 2>/dev/null || echo missing)\"; " +
        "printf \"root=%s\\n\" \"$( [ \"$(id -u)\" = 0 ] && echo yes || echo no )\"; " +
        "printf \"sudo=%s\\n\" \"$( sudo -n true 2>/dev/null && echo yes || echo no )\"; " +
        "printf \"distro=%s\\n\" \"$( . /etc/os-release 2>/dev/null && printf \"%s\" \"${ID:-unknown}\" || echo unknown )\"";

    /// <summary>把发行版 id 归到安装策略。</summary>
    public static DistroKind ParseDistro(string id) => id.Trim().ToLowerInvariant() switch
    {
        "ubuntu" or "debian" or "linuxmint" or "deepin" or "uos" or "kali" => DistroKind.Debian,
        "centos" or "rhel" or "rocky" or "almalinux" or "fedora" or "ol" or "amzn" or "openeuler" or "anolis" => DistroKind.Rhel,
        "arch" or "manjaro" or "endeavouros" => DistroKind.Arch,
        _ => DistroKind.Unsupported
    };

    /// <summary>系统源安装 Node.js；sudoPrefix 为空表示已在 root。</summary>
    public static string InstallNode(DistroKind distro, string sudoPrefix) => distro switch
    {
        DistroKind.Debian =>
            $"curl -fsSL https://deb.nodesource.com/setup_22.x | {sudoPrefix}bash - && " +
            $"{sudoPrefix}DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs",
        DistroKind.Rhel =>
            $"curl -fsSL https://rpm.nodesource.com/setup_22.x | {sudoPrefix}bash - && " +
            $"({sudoPrefix}dnf install -y nodejs || {sudoPrefix}yum install -y nodejs)",
        DistroKind.Arch =>
            $"{sudoPrefix}pacman -Sy --noconfirm nodejs npm",
        _ => throw new ArgumentOutOfRangeException(nameof(distro), distro, null)
    };

    /// <summary>无 root 权限时用 nvm 装用户级 Node（不动系统）。</summary>
    public static string InstallNvm() =>
        "curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash";

    public static string InstallNodeViaNvm() =>
        "export NVM_DIR=\"$HOME/.nvm\"; . \"$NVM_DIR/nvm.sh\" && nvm install --lts && nvm alias default \"lts/*\"";

    /// <summary>把 nvm 默认版本的 node/npm 等可执行文件链接进 ~/.local/bin。</summary>
    public static string LinkNvmToPath() =>
        "export NVM_DIR=\"$HOME/.nvm\"; . \"$NVM_DIR/nvm.sh\" && " +
        "mkdir -p ~/.local/bin && ln -sf \"$(dirname \"$(nvm which default)\")\"/* ~/.local/bin/";

    /// <summary>确保 ~/.local/bin 进入各 shell 配置的 PATH（登录与非交互 shell 均可解析 dsh）。</summary>
    public static string EnsurePathPersistence()
    {
        const string exportLine = "export PATH=\"$HOME/.local/bin:$PATH\"";
        return
            "mkdir -p ~/.local/bin; " +
            "for f in ~/.bash_profile ~/.bash_login ~/.profile ~/.bashrc; do " +
            "if [ -f \"$f\" ] && ! grep -qs \"local/bin\" \"$f\"; then printf \"\\n" + exportLine + "\\n\" >> \"$f\"; fi; done; " +
            "if [ ! -f ~/.bash_profile ] && [ ! -f ~/.bash_login ] && ! grep -qs \"local/bin\" ~/.profile 2>/dev/null; then " +
            "printf \"\\n" + exportLine + "\\n\" >> ~/.profile; fi; true";
    }

    /// <summary>dsh 装完但不在 PATH 时，把 npm 全局 bin 里的 dsh 链接进 ~/.local/bin。</summary>
    public static string EnsureDshOnPath() =>
        "if ! command -v dsh >/dev/null 2>&1; then " +
        "p=$(npm prefix -g 2>/dev/null); " +
        "if [ -x \"$p/bin/dsh\" ]; then mkdir -p ~/.local/bin && ln -sf \"$p/bin/dsh\" ~/.local/bin/dsh; fi; fi; true";

    /// <summary>探测回环 web：始终输出单行 HTTP 状态码（curl 失败时 -w 仍会打印，经变量归一为确定的一行 000）。</summary>
    public static string ProbeHttp(int port) =>
        $"code=$(curl -s -o /dev/null -m 2 -w '%{{http_code}}' http://127.0.0.1:{port}/ 2>/dev/null); echo \"${{code:-000}}\"";

    /// <summary>装到用户级前缀（~/.local）：无 sudo 也不会 EACCES，且天然落在 PATH 兜底目录里。</summary>
    public static string InstallOrUpdate() =>
        "npm install -g --prefix \"$HOME/.local\" @deepseek-ai/dsh@latest";

    /// <summary>
    /// 多行脚本：顺序执行避免 "A &amp;&amp; B &amp; C" 的优先级坑（pid 会抢在 mkdir 前写）；
    /// stdin 重定向到 /dev/null，后台进程不占用 SSH 通道，命令立即返回。
    /// </summary>
    public static string Start(int remotePort, int? trustedLocalPort)
    {
        var trusted = trustedLocalPort is { } local ? $" --trusted-host 127.0.0.1:{local}" : "";
        return
            "mkdir -p ~/.oh-my-pc\n" +
            $"nohup dsh web --host 127.0.0.1 --port {remotePort} --no-open{trusted} >> {LogFile} 2>&1 < /dev/null &\n" +
            $"echo $! > {PidFile}\n" +
            "echo launched";
    }

    /// <summary>停止 dsh web：优先 pid 文件；外部启动的实例（无 pid）按端口杀监听进程。</summary>
    public static string Stop(int port) =>
        $"test -f {PidFile} && kill \"$(cat {PidFile})\" 2>/dev/null; rm -f {PidFile}; " +
        $"fuser -k {port}/tcp 2>/dev/null; " +
        $"p=$(ss -tlnp 2>/dev/null | grep \":{port} \" | grep -oP \"pid=\\K[0-9]+\" | head -n 1); " +
        "[ -n \"$p\" ] && kill \"$p\" 2>/dev/null; true";

    /// <summary>取日志里该端口最近一次打印的面板地址（含 token），无则输出空。</summary>
    public static string ReadPanelUrl(int remotePort) =>
        $"grep -h -o \"dsh web: http://127.0.0.1:{remotePort}/[^[:space:]]*\" {LogFile} 2>/dev/null | tail -n 1";
}
