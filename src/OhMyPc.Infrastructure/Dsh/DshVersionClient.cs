using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.Dsh;

/// <summary>查询 npm registry 上 @deepseek-ai/dsh 的最新版本，用于界面提示“有新版本”；查询失败只降级不阻塞。</summary>
public sealed class DshVersionClient(IHttpClientFactory httpClientFactory, ILogger<DshVersionClient> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string? Version, DateTimeOffset FetchedAt) _cache;

    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _cache.FetchedAt < CacheLifetime) return _cache.Version;
            var client = httpClientFactory.CreateClient("npm-registry");
            await using var stream = await client.GetStreamAsync(
                "https://registry.npmjs.org/@deepseek-ai/dsh/latest", cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var version = document.RootElement.GetProperty("version").GetString();
            _cache = (version, DateTimeOffset.UtcNow);
            return version;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(exception, "查询 dsh 最新版本失败，本次展示不含最新版提示");
            _cache = (null, DateTimeOffset.UtcNow);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
