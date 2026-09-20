namespace OhMyPc.Infrastructure.Dsh;

/// <summary>~/.ssh/config 中一条可导入的字面主机条目。</summary>
/// <param name="Alias">Host 键的别名，用作应用内显示名。</param>
/// <param name="HostName">真实地址；未写时即别名。</param>
public sealed record SshConfigEntry(string Alias, string HostName, string UserName, int Port, string? IdentityFile);

/// <summary>
/// 解析 ~/.ssh/config 中可导入的主机条目。只支持单层语义：字面条目取值，
/// `Host *` 块中的 User/Port/IdentityFile 作为缺省兜底（主机自带值优先）；
/// 其他通配符模式与 Match 块不展开，直接跳过。
/// </summary>
public static class SshConfigParser
{
    /// <summary>读取当前用户的 ~/.ssh/config；文件不存在返回空列表。</summary>
    public static IReadOnlyList<SshConfigEntry> LoadDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(path)) return [];
        using var reader = File.OpenText(path);
        return Parse(reader);
    }

    public static IReadOnlyList<SshConfigEntry> Parse(TextReader reader)
    {
        string? defaultUser = null;
        int? defaultPort = null;
        string? defaultIdentityFile = null;
        var entries = new List<PendingEntry>();
        PendingEntry? current = null;
        var inWildcardBlock = false;
        var inSkippedBlock = false;

        while (reader.ReadLine() is { } rawLine)
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator <= 0) continue;
            var key = line[..separator].TrimEnd('=').ToLowerInvariant();
            // 引号保留到各键自行处理：Host 的引号内可含空格，需先按引号取词
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0 && key != "host") continue;

            switch (key)
            {
                case "host":
                    (current, inWildcardBlock, inSkippedBlock) = BeginHostBlock(value, entries);
                    break;
                case "match":
                    current = null;
                    inWildcardBlock = false;
                    inSkippedBlock = true;
                    break;
                case "hostname" when current is not null:
                    current.HostName = Unquote(value);
                    break;
                case "user" when inWildcardBlock:
                    defaultUser ??= Unquote(value);
                    break;
                case "user" when current is not null:
                    current.UserName = Unquote(value);
                    break;
                case "port" when inWildcardBlock:
                    defaultPort ??= ParsePort(value);
                    break;
                case "port" when current is not null:
                    current.Port = ParsePort(value);
                    break;
                case "identityfile" when inWildcardBlock:
                    defaultIdentityFile ??= Unquote(value);
                    break;
                case "identityfile" when current is not null:
                    current.IdentityFile ??= Unquote(value);
                    break;
            }
        }

        return [.. entries.Select(entry => new SshConfigEntry(
            entry.Alias,
            entry.HostName,
            entry.UserName ?? defaultUser ?? "root",
            entry.Port ?? defaultPort ?? 22,
            entry.IdentityFile ?? defaultIdentityFile))];
    }

    private static (PendingEntry? Current, bool Wildcard, bool Skipped) BeginHostBlock(
        string value, List<PendingEntry> entries)
    {
        // Host 别名可带引号（"quoted alias"）；引号内不按空白拆分
        var patterns = new List<string>();
        var remaining = value.Trim();
        while (remaining.Length > 0)
        {
            if (remaining[0] == '"')
            {
                var closing = remaining.IndexOf('"', 1);
                if (closing < 0) break;
                patterns.Add(remaining[1..closing]);
                remaining = remaining[(closing + 1)..].Trim();
            }
            else
            {
                var space = remaining.IndexOfAny([' ', '\t']);
                if (space < 0)
                {
                    patterns.Add(remaining);
                    remaining = "";
                }
                else
                {
                    patterns.Add(remaining[..space]);
                    remaining = remaining[(space + 1)..].Trim();
                }
            }
        }

        if (patterns.Count == 1 && patterns[0] == "*")
        {
            return (null, Wildcard: true, Skipped: false);
        }

        if (patterns.Any(pattern => pattern.Contains('*') || pattern.Contains('?') || pattern.StartsWith('!')))
        {
            return (null, Wildcard: false, Skipped: true);
        }

        var entry = new PendingEntry(patterns[0]);
        entries.Add(entry);
        return (entry, Wildcard: false, Skipped: false);
    }

    /// <summary>去掉注释：# 需在行首或紧跟空白（否则可能是值的一部分）。</summary>
    private static string StripComment(string line)
    {
        var index = line.IndexOf('#');
        if (index < 0) return line;
        if (index == 0) return string.Empty;
        return char.IsWhiteSpace(line[index - 1]) ? line[..index] : line;
    }

    private static int? ParsePort(string value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65535 ? port : null;

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private sealed class PendingEntry(string alias)
    {
        public string Alias { get; } = alias;
        public string HostName { get; set; } = alias;
        public string? UserName { get; set; }
        public int? Port { get; set; }
        public string? IdentityFile { get; set; }
    }
}
