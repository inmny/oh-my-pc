using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.IntegrationTests;

public sealed class LocalDshProcessServiceTests
{
    [Fact]
    public void FindExecutable_ResolvesCmdShimOnWindows()
    {
        var root = NewTempDir();
        var nested = Path.Combine(root, "nodejs");
        Directory.CreateDirectory(nested);
        var script = Path.Combine(nested, "dsh.cmd");
        File.WriteAllText(script, "@echo off\r\n");

        var found = LocalDshProcessService.FindExecutable(
            "dsh", $"{root}{Path.PathSeparator}{nested}", ".COM;.EXE;.BAT;.CMD", windows: true);

        // Windows 文件系统不区分大小写，PATHEXT 命中的返回路径大小写可能来自扩展名展开
        Assert.True(string.Equals(script, found, StringComparison.OrdinalIgnoreCase), $"实际找到：{found}");
    }

    [Fact]
    public void FindExecutable_ReturnsNullWhenDirectoryMissing()
    {
        Assert.Null(LocalDshProcessService.FindExecutable(
            "dsh", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), ".COM;.EXE", windows: true));
    }

    [Fact]
    public void FindExecutable_OnUnixRequiresExactName()
    {
        var root = NewTempDir();
        var executable = Path.Combine(root, "dsh");
        File.WriteAllText(executable, "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, "dsh.cmd"), "#!/bin/sh\n");

        Assert.Equal(executable, LocalDshProcessService.FindExecutable("dsh", root, null, windows: false));
    }

    [Fact]
    public void QuoteRemotePath_ExpandsHomeInsideDoubleQuotes()
    {
        // 单引号内 ~ 不展开（MyServer 踩坑：写文件报 ~/.dsh/settings.yaml 不存在）
        var quoted = SshCommandRunner.QuoteRemotePath("~/.dsh/settings.yaml");
        Assert.Equal("\"$HOME/.dsh/settings.yaml\"", quoted);
        Assert.Equal("\"$HOME/.dsh/profiles/web/cordis.patch.yml\"",
            SshCommandRunner.QuoteRemotePath("~/.dsh/profiles/web/cordis.patch.yml"));
        Assert.Equal("\"plain.txt\"", SshCommandRunner.QuoteRemotePath("plain.txt"));
    }

    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"oh-my-pc-dsh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
