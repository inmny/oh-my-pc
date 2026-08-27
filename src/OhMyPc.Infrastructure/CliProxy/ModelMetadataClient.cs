using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>models.dev 元数据客户端：一次拉取后整个进程生命周期内缓存。</summary>
public sealed class ModelMetadataClient(IHttpClientFactory httpClientFactory) : IModelMetadataProvider
{
    private const string ApiUrl = "https://models.dev/api.json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, ModelMetadata>? _cache;

    public async Task<IReadOnlyDictionary<string, ModelMetadata>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null) return _cache;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null) return _cache;
            var client = httpClientFactory.CreateClient("models-dev");
            var json = await client.GetStringAsync(ApiUrl, cancellationToken);
            _cache = ModelMetadataParser.Parse(json);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }
}
