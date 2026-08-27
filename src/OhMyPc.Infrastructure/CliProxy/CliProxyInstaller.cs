using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>从 GitHub Releases 下载 CLIProxyAPI 到 ~/utils/CPA，并可选地从旧 EasyCPA 迁移配置。</summary>
public sealed class CliProxyInstaller(
    IHttpClientFactory httpClientFactory,
    IProxyConfigStore store) : ICliProxyInstaller
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/router-for-me/CLIProxyAPI/releases/latest";

    public bool IsInstalled() => File.Exists(CliProxyPaths.ExecutablePath);

    public bool CanMigrateFromEasyCpa() => File.Exists(CliProxyPaths.EasyCpaConfigPath);

    public string? GetInstalledVersion() =>
        IsInstalled() ? FileVersionInfo.GetVersionInfo(CliProxyPaths.ExecutablePath).ProductVersion?.Trim() : null;

    public async Task<ProxyInstallResult> InstallAsync(ProxyInstallOptions options, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CliProxyPaths.InstallDirectory);
        await DownloadAndExtractAsync(cancellationToken);

        var migrated = false;
        if (options.MigrateFromEasyCpa && CanMigrateFromEasyCpa() && !File.Exists(CliProxyPaths.ConfigPath))
        {
            await MigrateEasyCpaAsync(cancellationToken);
            migrated = true;
        }
        await store.EnsureConfigAsync(cancellationToken);
        return new ProxyInstallResult { Migrated = migrated };
    }

    private async Task DownloadAndExtractAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("proxy-download");
        using var releaseResponse = await client.GetAsync(LatestReleaseUrl, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await releaseResponse.Content.ReadAsStringAsync(cancellationToken));
        var assetUrl = release?["assets"]?.AsArray()
            .FirstOrDefault(asset => IsWindowsX64Asset((string?)asset?["name"]))?["browser_download_url"]?.GetValue<string>()
            ?? throw new InvalidOperationException("发布列表中未找到 Windows x64 资产。");

        var tempZip = Path.Combine(Path.GetTempPath(), $"cliproxy-{Guid.NewGuid():N}.zip");
        try
        {
            using (var downloadResponse = await client.GetAsync(assetUrl, cancellationToken))
            {
                downloadResponse.EnsureSuccessStatusCode();
                await using var file = File.Create(tempZip);
                await downloadResponse.Content.CopyToAsync(file, cancellationToken);
            }
            ExtractIntoInstallDirectory(tempZip);
        }
        finally
        {
            try { File.Delete(tempZip); } catch (IOException) { }
        }
    }

    private static bool IsWindowsX64Asset(string? name) =>
        name is not null
        && name.Contains("windows", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("amd64", StringComparison.OrdinalIgnoreCase)
            || name.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
            || name.Contains("x64", StringComparison.OrdinalIgnoreCase));

    /// <summary>解压到暂存目录后，把包含 exe 的那一层目录内容复制进安装目录（兼容发布包带不带顶层文件夹）。</summary>
    private static void ExtractIntoInstallDirectory(string zipPath)
    {
        var staging = Path.Combine(CliProxyPaths.InstallDirectory, ".staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        try
        {
            var exePath = Directory.EnumerateFiles(staging, "cli-proxy-api.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("发布包中未找到 cli-proxy-api.exe。");
            var contentRoot = Path.GetDirectoryName(exePath)!;
            foreach (var source in Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(CliProxyPaths.InstallDirectory, Path.GetRelativePath(contentRoot, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>整份复制旧 EasyCPA 配置，仅重写 auth-dir 指向新 oauth 目录，并复制凭据目录。</summary>
    private static async Task MigrateEasyCpaAsync(CancellationToken cancellationToken)
    {
        var root = await YamlTree.ReadRootAsync(CliProxyPaths.EasyCpaConfigPath, cancellationToken);
        YamlTree.SetScalar(root, "auth-dir", "./oauth");
        ConfigFileSafety.WriteAllText(CliProxyPaths.ConfigPath, YamlTree.Save(root));
        if (Directory.Exists(CliProxyPaths.EasyCpaAuthDirectory))
        {
            ConfigFileSafety.CopyDirectory(CliProxyPaths.EasyCpaAuthDirectory, CliProxyPaths.AuthDirectory);
        }
    }
}
