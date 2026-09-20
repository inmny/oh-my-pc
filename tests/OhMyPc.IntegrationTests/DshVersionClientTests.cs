using System.Net;
using System.Text.Json;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.IntegrationTests;

public sealed class RemoteDshCommandsTests
{
    [Fact]
    public void Start_ContainsPortsTrustedHostAndPidRedirect()
    {
        var command = RemoteDshCommands.Start(3080, 13080);

        Assert.Contains("mkdir -p ~/.oh-my-pc\n", command);
        Assert.Contains("nohup dsh web --host 127.0.0.1 --port 3080 --no-open", command);
        Assert.Contains("--trusted-host 127.0.0.1:13080", command);
        Assert.Contains(">> ~/.oh-my-pc/dsh-web.log 2>&1 < /dev/null &", command);
        Assert.Contains("echo $! > ~/.oh-my-pc/dsh-web.pid", command);
        // 后台启动必须与 pid 写入分开（& 优先级坑），且 stdin 脱离通道让命令立即返回
        Assert.Contains("&\necho $! > ~/.oh-my-pc/dsh-web.pid\n", command);
    }

    [Fact]
    public void Start_OmitsTrustedHostWhenLocalPortUnknown()
    {
        Assert.DoesNotContain("trusted-host", RemoteDshCommands.Start(4000, null));
    }

    [Fact]
    public void Stop_KillsPidFileThenPortListenerForExternalInstances()
    {
        var command = RemoteDshCommands.Stop(3080);

        Assert.Contains("kill \"$(cat ~/.oh-my-pc/dsh-web.pid)\"", command);
        Assert.Contains("rm -f ~/.oh-my-pc/dsh-web.pid", command);
        // 外部启动的实例没有 pid 文件：按端口杀监听进程兜底
        Assert.Contains("fuser -k 3080/tcp", command);
        Assert.Contains("ss -tlnp", command);
        Assert.EndsWith("; true", command.TrimEnd());
    }

    [Fact]
    public void ProbeHttp_AlwaysEmitsSingleStatusCodeLine()
    {
        var command = RemoteDshCommands.ProbeHttp(8317);

        // curl 失败时 -w 仍会输出，必须经变量归一成确定的一行，否则多行 "000\n000" 会被误判为存活
        Assert.Contains("code=$(curl -s -o /dev/null -m 2 -w '%{http_code}' http://127.0.0.1:8317/", command);
        Assert.EndsWith("echo \"${code:-000}\"", command.TrimEnd());
    }

    [Fact]
    public void HttpAliveParse_IgnoresDuplicatedFailureLines()
    {
        // MyServer 实测踩坑：curl 失败输出两行 000，Trim 相等比较会误判为存活
        Assert.True(IsAlive("401"));
        Assert.True(IsAlive("200\n200"));
        Assert.False(IsAlive("000"));
        Assert.False(IsAlive("000\n000"));
    }

    private static bool IsAlive(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.Length == 3 && line.All(char.IsAsciiDigit) && line != "000");

    [Fact]
    public void ReadPanelUrl_GrepsTokenForRequestedPort()
    {
        var command = RemoteDshCommands.ReadPanelUrl(4001);

        Assert.Contains("http://127.0.0.1:4001/", command);
        Assert.Contains("tail -n 1", command);
    }

    [Fact]
    public void WrapLoginShell_EscapesSingleQuotes()
    {
        var wrapped = SshCommandRunner.WrapLoginShell("echo 'a b'");

        Assert.StartsWith("bash -lc '", wrapped);
        Assert.EndsWith("'", wrapped);
        Assert.Contains("'\\''", wrapped);
    }

    [Fact]
    public void DshLaunchOptions_CarriesPorts()
    {
        var options = new DshLaunchOptions(3080, 13080);

        Assert.Equal(3080, options.RemotePort);
        Assert.Equal(13080, options.TrustedLocalPort);
    }

    [Fact]
    public void ParseDistro_MapsFamiliesAndLeavesUnknownUnsupported()
    {
        Assert.Equal(DistroKind.Debian, RemoteDshCommands.ParseDistro("ubuntu"));
        Assert.Equal(DistroKind.Debian, RemoteDshCommands.ParseDistro("Debian"));
        Assert.Equal(DistroKind.Rhel, RemoteDshCommands.ParseDistro("rocky"));
        Assert.Equal(DistroKind.Arch, RemoteDshCommands.ParseDistro("manjaro"));
        Assert.Equal(DistroKind.Unsupported, RemoteDshCommands.ParseDistro("alpine"));
    }

    [Fact]
    public void InstallNode_UsesSudoPrefixForNonRoot()
    {
        var debianWithSudo = RemoteDshCommands.InstallNode(DistroKind.Debian, "sudo -E ");
        Assert.Contains("deb.nodesource.com/setup_22.x | sudo -E bash -", debianWithSudo);
        Assert.Contains("sudo -E DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs", debianWithSudo);

        var rhelAsRoot = RemoteDshCommands.InstallNode(DistroKind.Rhel, "");
        Assert.Contains("rpm.nodesource.com/setup_22.x | bash -", rhelAsRoot);
        Assert.Contains("|| yum install -y nodejs", rhelAsRoot);

        Assert.Contains("pacman -Sy --noconfirm nodejs npm", RemoteDshCommands.InstallNode(DistroKind.Arch, "sudo -E "));
    }

    [Fact]
    public void EnsurePathPersistence_AppendsLocalBinToShellProfiles()
    {
        var command = RemoteDshCommands.EnsurePathPersistence();

        Assert.Contains("mkdir -p ~/.local/bin", command);
        Assert.Contains("~/.bash_profile ~/.bash_login ~/.profile ~/.bashrc", command);
        Assert.Contains("grep -qs \"local/bin\" \"$f\"", command);
        Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"", command);
        Assert.EndsWith("true", command.TrimEnd());
    }

    [Fact]
    public void EnsureDshOnPath_LinksNpmGlobalBinWhenMissing()
    {
        var command = RemoteDshCommands.EnsureDshOnPath();

        Assert.Contains("if ! command -v dsh >/dev/null 2>&1; then", command);
        Assert.Contains("npm prefix -g", command);
        Assert.Contains("ln -sf \"$p/bin/dsh\" ~/.local/bin/dsh", command);
    }

    [Fact]
    public void ProbeDsh_EmitsStructuredVersionAndPath()
    {
        var command = RemoteDshCommands.ProbeDsh();

        Assert.Contains("dsh-version=%s", command);
        Assert.Contains("dsh --version 2>&1", command);
        Assert.Contains("dsh-path=%s", command);
        Assert.Contains("dsh-version=missing", command);
    }

    [Fact]
    public void ProbeNode_ReportsVersionsRootSudoAndDistro()
    {
        var command = RemoteDshCommands.ProbeNode();

        Assert.Contains("node=", command);
        Assert.Contains("npm=", command);
        Assert.Contains("root=", command);
        Assert.Contains("sudo -n true", command);
        Assert.Contains("/etc/os-release", command);
    }

    [Fact]
    public void InstallNodeViaNvm_FallsBackToUserLevelWithoutRoot()
    {
        Assert.Contains("nvm-sh/nvm/", RemoteDshCommands.InstallNvm());
        Assert.Contains("nvm install --lts", RemoteDshCommands.InstallNodeViaNvm());
        var link = RemoteDshCommands.LinkNvmToPath();
        Assert.Contains("nvm which default", link);
        Assert.Contains("ln -sf", link);
        Assert.Contains("~/.local/bin", link);
    }
}

public sealed class DshVersionClientTests
{
    [Fact]
    public async Task GetLatestVersionAsync_ParsesRegistryVersion()
    {
        var handler = new StubHandler(
            """{"name":"@deepseek-ai/dsh","version":"0.1.6-alpha.2"}""",
            "https://registry.npmjs.org/@deepseek-ai/dsh/latest");
        var client = new DshVersionClient(new StubClientFactory(handler), NullLoggerShared);

        Assert.Equal("0.1.6-alpha.2", await client.GetLatestVersionAsync());
    }

    [Fact]
    public async Task GetLatestVersionAsync_ReturnsNullOnHttpFailure()
    {
        var handler = new StubHandler("{}", "https://registry.npmjs.org/@deepseek-ai/dsh/latest") { StatusCode = HttpStatusCode.InternalServerError };
        var client = new DshVersionClient(new StubClientFactory(handler), NullLoggerShared);

        Assert.Null(await client.GetLatestVersionAsync());
    }

    [Fact]
    public async Task GetLatestVersionAsync_CachesResultWithinLifetime()
    {
        var handler = new StubHandler(
            """{"version":"0.2.0"}""",
            "https://registry.npmjs.org/@deepseek-ai/dsh/latest");
        var client = new DshVersionClient(new StubClientFactory(handler), NullLoggerShared);

        Assert.Equal("0.2.0", await client.GetLatestVersionAsync());
        handler.Dispose();
        Assert.Equal("0.2.0", await client.GetLatestVersionAsync());
    }

    private static Microsoft.Extensions.Logging.Abstractions.NullLogger<DshVersionClient> NullLoggerShared { get; } =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DshVersionClient>.Instance;

    private sealed class StubClientFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private readonly string _expectedUrl;

        public StubHandler(string payload, string expectedUrl)
        {
            _payload = payload;
            _expectedUrl = expectedUrl;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedUrl, request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(_payload, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
