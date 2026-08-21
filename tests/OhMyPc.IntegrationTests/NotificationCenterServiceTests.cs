using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Notifications;
using OhMyPc.Infrastructure.Persistence;

namespace OhMyPc.IntegrationTests;

public sealed class NotificationCenterServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-notifications-{Guid.NewGuid():N}.db");
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
    public async Task Publish_PersistsBeforeIsolatedRealtimeEvent()
    {
        using var service = new NotificationCenterService(
            _store,
            NullLogger<NotificationCenterService>.Instance);
        var persistedBeforeEvent = false;
        NotificationRecord? observed = null;
        service.Published += (_, _) => throw new InvalidOperationException("订阅者失败");
        service.Published += (_, record) =>
        {
            persistedBeforeEvent = _store.QueryNotificationsAsync(new NotificationHistoryQuery())
                .GetAwaiter()
                .GetResult()
                .Any(item => item.Id == record.Id);
            observed = record;
        };

        var published = await service.PublishAsync(new NotificationMessage
        {
            Origin = NotificationOrigin.Automation,
            Source = "  source-a  ",
            Title = "Title",
            Body = "Body",
            Channels = NotificationChannels.Danmaku | NotificationChannels.Tray,
            Severity = NotificationSeverity.Warning,
            SubjectKey = "subject-a"
        });

        Assert.True(persistedBeforeEvent);
        Assert.Same(published, observed);
        Assert.Equal(32, published.Id.Length);
        Assert.Equal("source-a", published.Source);
        Assert.Equal(TimeSpan.Zero, published.CreatedAt.Offset);
        var stored = Assert.Single(await _store.QueryNotificationsAsync(new NotificationHistoryQuery()));
        Assert.Equal(published.Id, stored.Id);
        Assert.Equal(NotificationOrigin.Automation, stored.Origin);
        Assert.Equal(NotificationChannels.Danmaku | NotificationChannels.Tray, stored.Channels);
    }

    [Fact]
    public async Task Publish_SerializesConcurrentWritesAndHonorsPreCommitCancellation()
    {
        using var service = new NotificationCenterService(
            _store,
            NullLogger<NotificationCenterService>.Instance);
        var events = 0;
        service.Published += (_, _) => Interlocked.Increment(ref events);

        var records = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            service.PublishAsync(new NotificationMessage
            {
                Source = "concurrent",
                Title = $"Title {index}",
                Body = "Body",
                Channels = NotificationChannels.Danmaku
            })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PublishAsync(new NotificationMessage { Body = "cancelled" }, cancellation.Token));

        Assert.Equal(20, records.Select(record => record.Id).Distinct().Count());
        Assert.Equal(20, events);
        Assert.Equal(20, (await _store.QueryNotificationsAsync(new NotificationHistoryQuery())).Count);
    }

    [Fact]
    public async Task ClearThrough_SerializesWithConcurrentPublishing()
    {
        using var service = new NotificationCenterService(
            _store,
            NullLogger<NotificationCenterService>.Instance);
        var publishing = Enumerable.Range(0, 40)
            .Select(index => service.PublishAsync(new NotificationMessage
            {
                Source = "race",
                Title = $"Title {index}",
                Body = "Body",
                Channels = NotificationChannels.Danmaku
            }))
            .ToArray();
        var cutoff = DateTimeOffset.UtcNow;

        var clearing = service.ClearThroughAsync(cutoff);
        await Task.WhenAll(publishing);
        await clearing;

        var remaining = await _store.QueryNotificationsAsync(new NotificationHistoryQuery());
        Assert.All(remaining, record => Assert.True(record.CreatedAt > cutoff));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_databasePath + suffix);
        return Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
