using System.Security.Cryptography;
using System.Text;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>
/// known_hosts 文件匹配：给定 host:port 找出已记录的服务器公钥（wire 格式 base64 blob），
/// 用于首次 SSH 连接的静默信任。支持明文与 |1| 盐哈希两种条目；明文模式只做精确匹配。
/// </summary>
public static class KnownHosts
{
    public static IReadOnlyList<string> GetKeyBlobs(string fileContent, string host, int port)
    {
        var candidate = NormalizeHost(host, port);
        var blobs = new List<string>();
        foreach (var rawLine in fileContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) continue;

            var matched = tokens[0].StartsWith('|')
                ? MatchesHashedPattern(tokens[0], candidate)
                : tokens[0].Split(',').Contains(candidate);
            if (matched) blobs.Add(tokens[^1]);
        }

        return blobs;
    }

    /// <summary>OpenSSH 风格指纹：SHA256:BASE64（标准字母表、去填充）。</summary>
    public static string Fingerprint(byte[] hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    internal static string NormalizeHost(string host, int port) =>
        port == 22 ? host : $"[{host}]:{port}";

    private static bool MatchesHashedPattern(string patternField, string candidate)
    {
        // |1|base64(salt)|base64(HMAC-SHA1(salt, host))|
        var parts = patternField.Split('|');
        if (parts.Length != 5 || parts[1] != "1") return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            using var hmac = new HMACSHA1(salt);
            var actual = hmac.ComputeHash(Encoding.ASCII.GetBytes(candidate));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
