using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.IntegrationTests;

public sealed class SshConfigParserTests
{
    [Fact]
    public void Parse_ExtractsLiteralEntriesWithAliasAndPorts()
    {
        const string config = """
            # 注释行
            Host MyServer
              HostName 45.125.45.164
              User root
              Port 35091

            Host TencentCloud
              HostName 175.24.165.111
              User ubuntu
            """;

        var entries = SshConfigParser.Parse(new StringReader(config));

        Assert.Equal(2, entries.Count);
        Assert.Equal("MyServer", entries[0].Alias);
        Assert.Equal("45.125.45.164", entries[0].HostName);
        Assert.Equal("root", entries[0].UserName);
        Assert.Equal(35091, entries[0].Port);
        Assert.Equal("TencentCloud", entries[1].Alias);
        Assert.Equal(22, entries[1].Port);
        Assert.Null(entries[1].IdentityFile);
    }

    [Fact]
    public void Parse_AppliesWildcardDefaultsAndKeepsOwnValues()
    {
        const string config = """
            Host *
              User default-user
              Port 2222
              IdentityFile ~/.ssh/id_shared

            Host alpha
              HostName alpha.example.com

            Host beta
              HostName beta.example.com
              User beta-user
              IdentityFile ~/.ssh/id_beta
            """;

        var entries = SshConfigParser.Parse(new StringReader(config));

        Assert.Equal(2, entries.Count);
        Assert.Equal("default-user", entries[0].UserName);
        Assert.Equal(2222, entries[0].Port);
        Assert.Equal("~/.ssh/id_shared", entries[0].IdentityFile);
        Assert.Equal("beta-user", entries[1].UserName);
        Assert.Equal("~/.ssh/id_beta", entries[1].IdentityFile);
    }

    [Fact]
    public void Parse_SkipsPatternGroupsMatchBlocksAndCommentedLines()
    {
        const string config = """
            Host gpu-* !gateway
              User lab

            Host ok-host
              HostName 10.0.0.1 # 行尾注释
              Port 1022

            Match host secret
              User hidden

            Host "quoted alias"
              HostName quoted.example.com
            """;

        var entries = SshConfigParser.Parse(new StringReader(config));

        Assert.Equal(2, entries.Count);
        Assert.Equal("ok-host", entries[0].Alias);
        Assert.Equal(1022, entries[0].Port);
        Assert.Equal("quoted alias", entries[1].Alias);
        Assert.Equal("quoted.example.com", entries[1].HostName);
    }

    [Fact]
    public void Parse_AliasWithoutHostNameUsesAliasAsAddress()
    {
        const string config = "Host 58.221.7.174\n  User root";

        var entries = SshConfigParser.Parse(new StringReader(config));

        var entry = Assert.Single(entries);
        Assert.Equal("58.221.7.174", entry.HostName);
        Assert.Equal("root", entry.UserName);
        Assert.Equal(22, entry.Port);
    }

    [Fact]
    public void LoadDefault_ReadsCurrentUserConfigWhenPresent()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(path)) return; // CI 环境没有该文件则跳过

        var entries = SshConfigParser.LoadDefault();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("*", entry.Alias);
            Assert.DoesNotContain("?", entry.Alias);
            Assert.True(entry.Port is >= 1 and <= 65535);
        });
    }
}
