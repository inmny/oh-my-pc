using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.LocalUsage;
using OhMyPc.Infrastructure.Persistence;

namespace OhMyPc.IntegrationTests;

public sealed class LocalUsageRefreshServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-refresh-{Guid.NewGuid():N}.db");
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
    public async Task Refresh_RunsCollectorOutsideCallingSynchronizationContext()
    {
        var collector = new MutableUsageCollector();
        var service = CreateService(collector, new RecordingPublisher());
        var originalContext = SynchronizationContext.Current;
        var callingContext = new SynchronizationContext();

        SynchronizationContext.SetSynchronizationContext(callingContext);
        var refresh = service.RefreshAsync(fullHistory: false);
        SynchronizationContext.SetSynchronizationContext(originalContext);
        await refresh;

        Assert.Null(collector.CapturedSynchronizationContext);
    }

    [Fact]
    public async Task Refresh_SkipsUnchangedPersistenceAndUiButKeepsAutomationEvaluation()
    {
        var collector = new MutableUsageCollector();
        var publisher = new RecordingPublisher();
        var service = CreateService(collector, publisher);
        var refreshedCount = 0;
        service.Refreshed += (_, _) => refreshedCount++;

        await service.RefreshAsync(fullHistory: false);
        await service.RefreshAsync(fullHistory: false);
        collector.InputTokens = 25;
        await service.RefreshAsync(fullHistory: false);
        collector.HasUsage = false;
        await service.RefreshAsync(fullHistory: false);
        await service.RefreshAsync(fullHistory: false);

        var today = await _store.GetTodayUsageAsync();
        Assert.Equal(5, collector.CollectionCount);
        Assert.Equal(5, publisher.PublishCount);
        Assert.Equal(3, refreshedCount);
        Assert.Equal(0, today.TotalTokens);
    }

    [Fact]
    public async Task Refresh_DoesNotDeleteUnobservedHistoryWithoutAPreviousSnapshot()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        await _store.UpsertUsageAsync([
            new UsageObservation
            {
                Date = yesterday,
                DeviceId = LocalUsageDevice.Id(),
                Client = "dsh",
                Provider = "test-provider",
                Model = "test-model",
                InputTokens = 42
            }
        ]);
        var collector = new MutableUsageCollector { HasUsage = false };
        var service = CreateService(collector, new RecordingPublisher());

        await service.RefreshAsync(fullHistory: true);

        var history = Assert.Single(await _store.QueryUsageAsync(yesterday, yesterday));
        Assert.Equal(42, history.TotalTokens);
    }

    [Fact]
    public async Task Refresh_PreservesFullHistoryActivityAcrossTodayRefreshes()
    {
        var collector = new MutableUsageCollector { ActivityTimeMsInFullHistory = 1_234 };
        var publisher = new RecordingPublisher();
        var service = CreateService(collector, publisher);
        var refreshedCount = 0;
        service.Refreshed += (_, _) => refreshedCount++;

        await service.RefreshAsync(fullHistory: true);
        await service.RefreshAsync(fullHistory: false);
        await service.RefreshAsync(fullHistory: true);

        var today = await _store.GetTodayUsageAsync();
        Assert.Equal(1_234, today.ActiveTimeMs);
        Assert.Equal(3, publisher.PublishCount);
        Assert.Equal(1, refreshedCount);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_databasePath + suffix);
        return Task.CompletedTask;
    }

    private LocalUsageRefreshService CreateService(
        ILocalUsageCollector collector,
        IAutomationEventPublisher publisher) => new(
        collector,
        _store,
        publisher,
        new StubTextLocalizer(),
        NullLogger<LocalUsageRefreshService>.Instance);

    private sealed class MutableUsageCollector : ILocalUsageCollector
    {
        public int CollectionCount { get; private set; }
        public long InputTokens { get; set; } = 10;
        public long ActivityTimeMsInFullHistory { get; set; }
        public bool HasUsage { get; set; } = true;
        public SynchronizationContext? CapturedSynchronizationContext { get; private set; }

        public Task<IReadOnlyList<UsageObservation>> CollectAsync(
            bool fullHistory,
            CancellationToken cancellationToken = default)
        {
            CollectionCount++;
            CapturedSynchronizationContext = SynchronizationContext.Current;
            if (!HasUsage) return Task.FromResult<IReadOnlyList<UsageObservation>>([]);

            var today = DateOnly.FromDateTime(DateTime.Now);
            var deviceId = LocalUsageDevice.Id();
            var observations = new List<UsageObservation>
            {
                new()
                {
                    Date = today,
                    DeviceId = deviceId,
                    Client = "test-client",
                    Provider = "test-provider",
                    Model = "test-model",
                    InputTokens = InputTokens,
                    ObservedAt = DateTimeOffset.UtcNow
                }
            };
            if (fullHistory && ActivityTimeMsInFullHistory > 0)
            {
                observations.Add(new UsageObservation
                {
                    Date = today,
                    DeviceId = deviceId,
                    Client = "_activity",
                    Provider = "tokscale",
                    Model = "active-time",
                    ActiveTimeMs = ActivityTimeMsInFullHistory,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
            return Task.FromResult<IReadOnlyList<UsageObservation>>(observations);
        }
    }

    private sealed class RecordingPublisher : IAutomationEventPublisher
    {
        public int PublishCount { get; private set; }

        public Task PublishAsync(AutomationEvent automationEvent, CancellationToken cancellationToken = default)
        {
            PublishCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubTextLocalizer : ITextLocalizer
    {
        public string this[string key] => key;
        public string Format(string key, params object?[] arguments) => key;
        public string GetEnum(Enum value) => value.ToString();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
