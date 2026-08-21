using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.InputStatus;
using OhMyPc.Infrastructure.Persistence;

namespace OhMyPc.IntegrationTests;

public sealed class InputStatusTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-status-{Guid.NewGuid():N}.db");
    private AppStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await new DatabaseBootstrapper(factory).InitializeAsync();
        _store = new AppStore(factory, new CredentialProtector());
    }

    [Fact]
    public async Task Client_UsesConfiguredEndpointAndParsesModelAvailability()
    {
        const string json = """
            {
              "all_ok": false,
              "services": [
                {
                  "model": "gpt-5.6-sol",
                  "uptime_pct": 100.0,
                  "last": { "ok": false, "latency_ms": 5615, "error": "HTTP 503" },
                  "history": [
                    { "ts": 1786715510, "ok": true, "latency_ms": 120, "error": null }
                  ]
                }
              ]
            }
            """;
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var client = new InputStatusClient(new StubHttpClientFactory(handler));
        var endpoint = new Uri("https://status.example.test/custom/status");

        var model = Assert.Single(await client.GetModelsAsync(endpoint));

        Assert.Equal(endpoint.ToString(), handler.Url);
        Assert.Equal("gpt-5.6-sol", model.Model);
        Assert.False(model.Available);
        Assert.Equal(5615, model.LatencyMilliseconds);
        Assert.Equal("HTTP 503", model.Error);
    }

    [Fact]
    public async Task RefreshAll_IsolatesSourcesAndPublishesRealSourceId()
    {
        var sourceA = Source("source-a", "https://a.example.test/status");
        var sourceB = Source("source-b", "https://b.example.test/status");
        await _store.SaveDataSourceAsync(sourceA, apiKey: null);
        await _store.SaveDataSourceAsync(sourceB, apiKey: null);
        var client = new SequenceInputStatusClient();
        client.Enqueue(sourceA.ModelStatusUrl, [Status(available: true)]);
        client.Enqueue(sourceB.ModelStatusUrl, [Status(available: false)]);
        client.Enqueue(sourceA.ModelStatusUrl, [Status(available: false)]);
        client.Enqueue(sourceB.ModelStatusUrl, [Status(available: false)]);
        var publisher = new CapturePublisher();
        var service = CreateService(client, publisher);

        await service.RefreshAllAsync();
        await service.RefreshAllAsync();

        var automationEvent = Assert.Single(publisher.Events);
        Assert.Equal(sourceA.Id, automationEvent.SourceId);
        Assert.Equal(sourceA.Id, automationEvent.Fields["sourceId"]!.GetValue<string>());
        Assert.StartsWith($"input-status:source:{sourceA.Id}:endpoint:", automationEvent.SubjectKey);
        Assert.EndsWith(":model:gpt-5.6-sol", automationEvent.SubjectKey);
        Assert.Equal(2, service.GetSnapshot(sourceA.Id)!.Models.Single().Samples.Count);
        Assert.Equal(2, service.GetSnapshot(sourceB.Id)!.Models.Single().Samples.Count);
        Assert.Equal(4, client.Endpoints.Count);
    }

    [Fact]
    public async Task Refresh_FailureKeepsLastSnapshotAndUrlChangeClearsSamples()
    {
        var source = Source("source-a", "https://a.example.test");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new SequenceInputStatusClient();
        client.Enqueue(source.ModelStatusUrl, [Status(available: true)]);
        client.EnqueueFailure(source.ModelStatusUrl, new HttpRequestException("temporary failure"));
        client.EnqueueFailure(source.ModelStatusUrl, new HttpRequestException("still failing"));
        var publisher = new CapturePublisher();
        var service = CreateService(client, publisher);

        await service.RefreshAsync(source.Id);
        await service.RefreshAsync(source.Id);
        await service.RefreshAsync(source.Id);

        var failed = service.GetSnapshot(source.Id)!;
        Assert.Single(failed.Models);
        Assert.Single(failed.Models[0].Samples);
        Assert.Equal("still failing", failed.Error);
        Assert.NotNull(failed.LastSuccessAt);

        source.ModelStatusUrl = "https://b.example.test/status";
        await _store.SaveDataSourceAsync(source, apiKey: null);
        client.Enqueue(source.ModelStatusUrl, [Status(available: false)]);
        await service.RefreshAsync(source.Id);

        var changed = service.GetSnapshot(source.Id)!;
        Assert.Equal(new Uri(source.ModelStatusUrl).AbsoluteUri, changed.StatusUrl);
        Assert.Single(changed.Models[0].Samples);
        Assert.False(changed.Models[0].Samples[0]);
        Assert.Empty(publisher.Events);

        source.ModelStatusUrl = "";
        await _store.SaveDataSourceAsync(source, apiKey: null);
        await service.RefreshAsync(source.Id);
        Assert.Null(service.GetSnapshot(source.Id));
    }

    [Fact]
    public async Task UrlChange_ClearsSnapshotBeforeRequestingNewEndpoint()
    {
        var source = Source("source-a", "https://a.example.test/status");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new SwitchingEndpointInputStatusClient();
        var service = CreateService(client, new CapturePublisher());
        await service.RefreshAsync(source.Id);
        Assert.NotNull(service.GetSnapshot(source.Id));

        source.ModelStatusUrl = "https://b.example.test/status";
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var refresh = service.RefreshAsync(source.Id);
        await client.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(service.GetSnapshot(source.Id));
        client.CompleteSecondRequest([Status(available: false)]);
        await refresh;

        var changed = service.GetSnapshot(source.Id)!;
        Assert.Equal(new Uri(source.ModelStatusUrl).AbsoluteUri, changed.StatusUrl);
        Assert.Single(changed.Models.Single().Samples);
    }

    [Fact]
    public async Task RefreshAll_DisabledSourceClearsSnapshot()
    {
        var source = Source("source-a", "https://a.example.test/status");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new SequenceInputStatusClient();
        client.Enqueue(source.ModelStatusUrl, [Status(available: true)]);
        var service = CreateService(client, new CapturePublisher());
        await service.RefreshAllAsync();

        source.Enabled = false;
        await _store.SaveDataSourceAsync(source, apiKey: null);
        await service.RefreshAllAsync();

        Assert.Null(service.GetSnapshot(source.Id));
        Assert.Single(client.Endpoints);
    }

    [Fact]
    public async Task RefreshAsync_EmptyStatusUrlClearsSnapshot()
    {
        var source = Source("source-a", "https://a.example.test/status");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new SequenceInputStatusClient();
        client.Enqueue(source.ModelStatusUrl, [Status(available: true)]);
        var service = CreateService(client, new CapturePublisher());
        await service.RefreshAsync(source.Id);

        source.ModelStatusUrl = "";
        await _store.SaveDataSourceAsync(source, apiKey: null);
        await service.RefreshAsync(source.Id);

        Assert.Null(service.GetSnapshot(source.Id));
        Assert.Single(client.Endpoints);
    }

    [Fact]
    public async Task RefreshAll_DeletedSourceClearsSnapshot()
    {
        var source = Source("source-a", "https://a.example.test/status");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new SequenceInputStatusClient();
        client.Enqueue(source.ModelStatusUrl, [Status(available: true)]);
        var service = CreateService(client, new CapturePublisher());
        await service.RefreshAllAsync();

        await _store.DeleteDataSourceAsync(source.Id);
        await service.RefreshAllAsync();

        Assert.Null(service.GetSnapshot(source.Id));
        Assert.Single(client.Endpoints);
    }

    [Fact]
    public async Task Refresh_KeepsOnlyLatestTwentyRealSamples()
    {
        var source = Source("source-a", "https://a.example.test/status");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new SequenceInputStatusClient();
        for (var index = 0; index < 25; index++)
        {
            client.Enqueue(source.ModelStatusUrl, [Status(available: index % 2 == 0)]);
        }
        var service = CreateService(client, new CapturePublisher());

        for (var index = 0; index < 25; index++) await service.RefreshAsync(source.Id);

        var samples = service.GetSnapshot(source.Id)!.Models.Single().Samples;
        Assert.Equal(20, samples.Count);
        Assert.False(samples[0]);
        Assert.True(samples[^1]);
        Assert.Equal(25, client.Endpoints.Count);
    }

    [Fact]
    public async Task InvalidateSource_DiscardsInFlightResult()
    {
        var source = Source("source-a", "https://a.example.test/status");
        await _store.SaveDataSourceAsync(source, apiKey: null);
        var client = new BlockingInputStatusClient();
        var publisher = new CapturePublisher();
        var service = CreateService(client, publisher);

        var refresh = service.RefreshAsync(source.Id);
        await client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.InvalidateSource(source.Id);
        client.Complete([Status(available: true)]);
        await refresh;

        Assert.Null(service.GetSnapshot(source.Id));
        Assert.Empty(publisher.Events);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_databasePath + suffix);
        return Task.CompletedTask;
    }

    private InputStatusRefreshService CreateService(
        IInputStatusClient client,
        IAutomationEventPublisher publisher) =>
        new(client, _store, publisher, new StubTextLocalizer(), NullLogger<InputStatusRefreshService>.Instance);

    private static DataSourceDefinition Source(string id, string modelStatusUrl) => new()
    {
        Id = id,
        Name = id,
        Kind = DataSourceKind.NewApi,
        BaseUrl = $"https://{id}.example.test",
        ModelStatusUrl = modelStatusUrl
    };

    private static InputModelStatus Status(bool available) => new()
    {
        Model = "gpt-5.6-sol",
        Available = available,
        LatencyMilliseconds = 120,
        Error = available ? null : "HTTP 503"
    };

    private sealed class SwitchingEndpointInputStatusClient : IInputStatusClient
    {
        private readonly TaskCompletionSource<IReadOnlyList<InputModelStatus>> _secondResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        public TaskCompletionSource<bool> SecondRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<InputModelStatus>> GetModelsAsync(
            Uri statusEndpoint,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requestCount) == 1) return [Status(available: true)];
            SecondRequestStarted.TrySetResult(true);
            return await _secondResponse.Task.WaitAsync(cancellationToken);
        }

        public void CompleteSecondRequest(IReadOnlyList<InputModelStatus> models) =>
            _secondResponse.TrySetResult(models);
    }

    private sealed class BlockingInputStatusClient : IInputStatusClient
    {
        private readonly TaskCompletionSource<IReadOnlyList<InputModelStatus>> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<InputModelStatus>> GetModelsAsync(
            Uri statusEndpoint,
            CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult(true);
            return await _response.Task.WaitAsync(cancellationToken);
        }

        public void Complete(IReadOnlyList<InputModelStatus> models) => _response.TrySetResult(models);
    }

    private sealed class SequenceInputStatusClient : IInputStatusClient
    {
        private readonly Dictionary<string, Queue<object>> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Endpoints { get; } = [];

        public void Enqueue(string endpoint, IReadOnlyList<InputModelStatus> models) => Queue(endpoint).Enqueue(models);
        public void EnqueueFailure(string endpoint, Exception exception) => Queue(endpoint).Enqueue(exception);

        public Task<IReadOnlyList<InputModelStatus>> GetModelsAsync(Uri statusEndpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(statusEndpoint.AbsoluteUri);
            var response = Queue(statusEndpoint.AbsoluteUri).Dequeue();
            if (response is Exception exception) return Task.FromException<IReadOnlyList<InputModelStatus>>(exception);
            return Task.FromResult((IReadOnlyList<InputModelStatus>)response);
        }

        private Queue<object> Queue(string endpoint)
        {
            var key = new Uri(endpoint).AbsoluteUri;
            if (!_responses.TryGetValue(key, out var queue))
            {
                queue = new Queue<object>();
                _responses[key] = queue;
            }
            return queue;
        }
    }

    private sealed class CapturePublisher : IAutomationEventPublisher
    {
        public List<AutomationEvent> Events { get; } = [];

        public Task PublishAsync(AutomationEvent automationEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(automationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class StubTextLocalizer : ITextLocalizer
    {
        public string this[string key] => key;
        public string Format(string key, params object?[] arguments) => $"{key}:{string.Join(',', arguments)}";
        public string GetEnum(Enum value) => value.ToString();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Url { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            return Task.FromResult(response);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
