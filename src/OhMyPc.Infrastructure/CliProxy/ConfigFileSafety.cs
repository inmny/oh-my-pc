namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>用户配置文件写入的共用安全措施：先复制 .bak 备份，再就地写入。</summary>
/// <remarks>必须就地写入而非"临时文件+替换"：CLIProxyAPI 依靠写入事件触发热重载，替换式写法会让其文件监视失效。</remarks>
internal static class ConfigFileSafety
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException($"路径缺少目录部分：{path}", nameof(path));
        Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, content);
    }

    public static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }
}
