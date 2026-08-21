using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Persistence;
using OhMyPc.Infrastructure.Vpn;

namespace OhMyPc.IntegrationTests;

public sealed class AppStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-{Guid.NewGuid():N}.db");
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
    public async Task Migration_SeedsSettingsAndDefaultRules()
    {
        var settings = await _store.GetSettingsAsync();
        var rules = await _store.ListRulesAsync();

        Assert.True(settings.NotificationsEnabled);
        Assert.Equal(NotificationRetentionPolicy.DefaultDays, settings.NotificationHistoryRetentionDays);
        Assert.Equal(8, rules.Count);
        Assert.Contains(rules, rule => rule.Id == "quota-critical");
        Assert.Contains(rules, rule => rule.Id == "vpn-quota-warning");
        Assert.Contains(rules, rule => rule.Id == "vpn-expiration-warning");
        var inputStatus = Assert.Single(rules, rule => rule.Id == "input-model-gpt-5.6-sol");
        Assert.True(inputStatus.Enabled);
        Assert.Equal(AutomationEventTypes.InputModelAvailabilityChanged, inputStatus.EventType);
        Assert.Equal("gpt-5.6-sol", Assert.Single(inputStatus.Conditions).Value!.GetValue<string>());
    }

    [Fact]
    public async Task CurrentQuotas_PreserveProgressLimit()
    {
        var source = new DataSourceDefinition
        {
            Id = "quota-source",
            Name = "Quota source",
            Kind = DataSourceKind.NewApi,
            BaseUrl = "https://example.test",
            ModelStatusUrl = "https://status.example.test/api/status"
        };
        await _store.SaveDataSourceAsync(source, apiKey: null);
        Assert.Equal(source.ModelStatusUrl, (await _store.GetDataSourceAsync(source.Id))?.ModelStatusUrl);
        await _store.ReplaceCurrentQuotasAsync(source.Id, [new QuotaSnapshot
        {
            SourceId = source.Id,
            WindowKey = "balance",
            Label = "Balance",
            Used = 800,
            Limit = 1000,
            ProgressLimit = 200,
            Remaining = 180,
            Unit = "USD"
        }]);

        var quota = Assert.Single(await _store.ListCurrentQuotasAsync());

        Assert.Equal(200, quota.ProgressLimit);
        Assert.Equal(90, quota.RemainingPercent);
    }

    [Fact]
    public async Task UsageUpsert_ReplacesDateSnapshotWhenProviderOrModelChanges()
    {
        var day = new DateOnly(2026, 8, 7);
        await _store.UpsertUsageAsync([
            Usage(day, "opencode", "old-model", input: 100, output: 50, cost: 1.00m, provider: "tken"),
            Usage(day, "opencode", "old-model", input: 15, output: 5, cost: 0.10m, provider: "ai_input,tken")
        ]);
        await _store.UpsertUsageAsync([Usage(day, "opencode", "new-model", input: 30, output: 30, cost: 0.30m, provider: "tken")]);
        await _store.UpsertUsageAsync([Usage(day.AddDays(1), "claude", "sonnet", input: 8, output: 2, cost: 0.05m)]);

        var rows = await _store.QueryUsageAsync(day, day.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(60, rows[0].TotalTokens);
        Assert.Equal(0.30m, rows[0].CostUsd);
        Assert.Equal(10, rows[1].TotalTokens);
    }

    [Fact]
    public async Task UsageReplace_ClearsEmptySnapshotOnlyForRequestedDeviceAndRange()
    {
        var day = new DateOnly(2026, 8, 8);
        await _store.UpsertUsageAsync([
            Usage(day, "dsh", "model", input: 100, output: 20, cost: 0.10m, deviceId: "local-device"),
            Usage(day, "remote", "model", input: 7, output: 3, cost: 0.01m, deviceId: "other-device")
        ]);

        await _store.ReplaceUsageAsync([], [new UsageObservationScope(day, "local-device")]);

        var row = Assert.Single(await _store.QueryUsageAsync(day, day));
        Assert.Equal(10, row.TotalTokens);
        Assert.Equal(0.01m, row.CostUsd);
    }

    [Fact]
    public async Task UsageBreakdown_GroupsByModelAndTool()
    {
        var day = new DateOnly(2026, 8, 9);
        await _store.UpsertUsageAsync([
            Usage(day, "codex", "shared-model", input: 100, output: 20, cost: 1.00m, cacheRead: 80, cacheWrite: 10),
            Usage(day, "opencode", "shared-model", input: 50, output: 10, cost: 0.50m, cacheRead: 40),
            Usage(day, "opencode", "other-model", input: 30, output: 5, cost: 0.20m, cacheWrite: 5)
        ]);

        var models = await _store.QueryUsageBreakdownAsync(day, day, UsageBreakdownGroup.Model);
        var tools = await _store.QueryUsageBreakdownAsync(day, day, UsageBreakdownGroup.Tool);

        Assert.Equal(["shared-model", "other-model"], models.Select(x => x.Name));
        Assert.Equal(150, models[0].InputTokens);
        Assert.Equal(30, models[0].OutputTokens);
        Assert.Equal(120, models[0].CacheReadTokens);
        Assert.Equal(10, models[0].CacheWriteTokens);
        Assert.Equal(310, models[0].TotalTokens);
        Assert.Equal(1.50m, models[0].CostUsd);
        Assert.Equal(["codex", "opencode"], tools.Select(x => x.Name));
        Assert.Equal(80, tools[1].InputTokens);
        Assert.Equal(15, tools[1].OutputTokens);
        Assert.Equal(40, tools[1].CacheReadTokens);
        Assert.Equal(5, tools[1].CacheWriteTokens);
        Assert.Equal(140, tools[1].TotalTokens);
    }

    [Fact]
    public async Task UsageUpsert_ReplacesOnlyTheMatchingDeviceSnapshot()
    {
        var day = new DateOnly(2026, 8, 10);
        await _store.UpsertUsageAsync([
            Usage(day, "opencode", "model", input: 100, output: 20, cost: 1.00m, deviceId: "device-a"),
            Usage(day, "opencode", "model", input: 200, output: 30, cost: 2.00m, deviceId: "device-b")
        ]);
        await _store.UpsertUsageAsync([
            Usage(day, "opencode", "model", input: 10, output: 5, cost: 0.10m, deviceId: "device-a")
        ]);

        var rows = await _store.QueryUsageAsync(day, day);

        var row = Assert.Single(rows);
        Assert.Equal(245, row.TotalTokens);
        Assert.Equal(2.10m, row.CostUsd);
    }

    [Fact]
    public async Task Credentials_AreEncryptedAndPreservedWhenEditOmitsKey()
    {
        var source = new DataSourceDefinition
        {
            Id = "primary",
            Name = "Primary",
            Kind = DataSourceKind.Sub2Api,
            BaseUrl = "https://example.test/v1"
        };
        await _store.SaveDataSourceAsync(source, "secret-token");
        source.Name = "Primary renamed";
        await _store.SaveDataSourceAsync(source, apiKey: null);

        var credential = await _store.GetCredentialAsync(source.Id);
        var loaded = await _store.GetDataSourceAsync(source.Id);

        Assert.Equal("secret-token", credential);
        Assert.Equal("Primary renamed", loaded?.Name);
    }

    [Fact]
    public async Task VpnAccount_EncryptsAuthDataAndPreservesItDuringRefresh()
    {
        var account = new VpnAccountDefinition
        {
            Email = "user@example.com",
            PlanName = "年度套餐",
            UploadedBytes = 100,
            DownloadedBytes = 200,
            TransferLimitBytes = 1000,
            Status = ProviderStatus.Healthy
        };
        await _store.SaveVpnAccountAsync(account, "raw auth token");
        account.DownloadedBytes = 300;
        await _store.SaveVpnAccountAsync(account);

        var loaded = await _store.GetVpnAccountAsync();
        var authData = await _store.GetVpnAuthDataAsync();
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EncryptedAuthData FROM VpnAccounts WHERE Id = 'passgo'";
        var encrypted = (byte[])(await command.ExecuteScalarAsync())!;

        Assert.Equal(300, loaded?.DownloadedBytes);
        Assert.Equal("raw auth token", authData);
        Assert.NotEqual("raw auth token", System.Text.Encoding.UTF8.GetString(encrypted));
    }

    [Fact]
    public async Task VpnDailyUsage_OverwritesSameDayAndQueriesInDateOrder()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        await _store.UpsertVpnDailyUsageAsync(new VpnDailyUsagePoint
        {
            Date = yesterday,
            UploadedBytes = 100,
            DownloadedBytes = 200,
            TransferLimitBytes = 1000,
            ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await _store.UpsertVpnDailyUsageAsync(new VpnDailyUsagePoint
        {
            Date = yesterday,
            UploadedBytes = 150,
            DownloadedBytes = 350,
            TransferLimitBytes = 1200,
            ObservedAt = DateTimeOffset.UtcNow
        });
        await _store.UpsertVpnDailyUsageAsync(new VpnDailyUsagePoint
        {
            Date = yesterday.AddDays(1),
            UploadedBytes = 200,
            DownloadedBytes = 400,
            TransferLimitBytes = 1200,
            ObservedAt = DateTimeOffset.UtcNow
        });

        var points = await _store.QueryVpnDailyUsageAsync(yesterday, yesterday.AddDays(1));

        Assert.Equal(2, points.Count);
        Assert.Equal(500, points[0].UsedBytes);
        Assert.Equal(600, points[1].UsedBytes);
    }

    [Fact]
    public async Task VpnRefresh_PreservesSnapshotWhenAuthenticationExpires()
    {
        await _store.SaveVpnAccountAsync(new VpnAccountDefinition
        {
            Email = "user@example.com",
            PlanName = "年度套餐",
            UploadedBytes = 100,
            DownloadedBytes = 200,
            TransferLimitBytes = 1000,
            Status = ProviderStatus.Healthy
        }, "expired-token");
        var service = new VpnQuotaRefreshService(
            _store,
            new ExpiredVpnClient(),
            new CapturePublisher(),
            new StubTextLocalizer(),
            NullLogger<VpnQuotaRefreshService>.Instance);

        await service.RefreshAsync();
        var loaded = await _store.GetVpnAccountAsync();

        Assert.Equal(ProviderStatus.AuthenticationFailed, loaded?.Status);
        Assert.Equal(300, loaded?.UsedBytes);
        Assert.Equal("登录状态已失效", loaded?.LastError);
        Assert.NotNull(loaded?.LastAttemptAt);
    }

    [Fact]
    public async Task VpnRefresh_SavesHistoryAndPublishesQuotaAndExpiration()
    {
        await _store.SaveVpnAccountAsync(new VpnAccountDefinition
        {
            Email = "user@example.com",
            Status = ProviderStatus.Healthy
        }, "active-token");
        var publisher = new CapturePublisher();
        var service = new VpnQuotaRefreshService(
            _store,
            new ActiveVpnClient(),
            publisher,
            new StubTextLocalizer(),
            NullLogger<VpnQuotaRefreshService>.Instance);

        await service.RefreshAsync();

        var history = await _store.QueryVpnDailyUsageAsync(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today));
        Assert.Single(history);
        Assert.Contains(publisher.Events, x => x.Type == AutomationEventTypes.VpnQuotaObserved);
        Assert.Contains(publisher.Events, x => x.Type == AutomationEventTypes.VpnExpirationObserved);
    }

    [Fact]
    public async Task DeleteDataSource_ClearsInputStatusAutomationState()
    {
        var source = new DataSourceDefinition
        {
            Id = "source-delete-status",
            Name = "Status source",
            Kind = DataSourceKind.NewApi,
            BaseUrl = "https://example.test",
            ModelStatusUrl = "https://status.example.test/api/status"
        };
        var subjectKey = $"input-status:source:{source.Id}:endpoint:HASH:model:model-a";
        var rule = (await _store.ListRulesAsync()).First();
        await _store.SaveDataSourceAsync(source, apiKey: null);
        await _store.SaveSourceStateAsync(new AutomationSourceState
        {
            Key = subjectKey,
            ValueJson = "true"
        });
        await _store.SaveRuleStateAsync(new AutomationRuleState
        {
            RuleId = rule.Id,
            SubjectKey = subjectKey,
            LastExecutedAt = DateTimeOffset.UtcNow
        });

        await _store.DeleteDataSourceAsync(source.Id);

        Assert.Null(await _store.GetSourceStateAsync(subjectKey));
        Assert.Null(await _store.GetRuleStateAsync(rule.Id, subjectKey));
    }

    [Fact]
    public async Task NotificationHistory_FiltersAndCapsStableDescendingResults()
    {
        var start = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 205; index++)
        {
            await _store.SaveNotificationAsync(Notification(
                $"n{index:000}",
                start.AddSeconds(index),
                index % 2 == 0 ? "source-a" : "source-b",
                index % 3 == 0 ? NotificationSeverity.Warning : NotificationSeverity.Info));
        }

        var latest = await _store.QueryNotificationsAsync(new NotificationHistoryQuery { Limit = 500 });
        var filtered = await _store.QueryNotificationsAsync(new NotificationHistoryQuery
        {
            Source = "source-a",
            Severity = NotificationSeverity.Warning,
            CreatedFrom = start.AddSeconds(100),
            CreatedBefore = start.AddSeconds(180)
        });
        var sources = await _store.ListNotificationSourcesAsync();

        Assert.Equal(NotificationHistoryQuery.MaximumLimit, latest.Count);
        Assert.Equal("n204", latest[0].Id);
        Assert.Equal("n005", latest[^1].Id);
        Assert.NotEmpty(filtered);
        Assert.All(filtered, record =>
        {
            Assert.Equal("source-a", record.Source);
            Assert.Equal(NotificationSeverity.Warning, record.Severity);
            Assert.InRange(record.CreatedAt, start.AddSeconds(100), start.AddSeconds(180).AddTicks(-1));
        });
        Assert.Equal(["source-a", "source-b"], sources);

        var sameTime = start.AddDays(1);
        await _store.SaveNotificationAsync(Notification("same-a", sameTime));
        await _store.SaveNotificationAsync(Notification("same-b", sameTime));
        var stable = await _store.QueryNotificationsAsync(new NotificationHistoryQuery
        {
            CreatedFrom = sameTime,
            Limit = 2
        });
        Assert.Equal(["same-b", "same-a"], stable.Select(record => record.Id));
    }

    [Fact]
    public async Task NotificationHistory_PrunesDeletesAndClearsThroughCutoff()
    {
        var cutoff = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        await _store.SaveNotificationAsync(Notification("old", cutoff.AddTicks(-1), "old-source"));
        await _store.SaveNotificationAsync(Notification("boundary", cutoff, "boundary-source"));
        await _store.SaveNotificationAsync(Notification("future", cutoff.AddTicks(1), "future-source"));

        Assert.Equal(1, await _store.PruneNotificationsAsync(cutoff));
        await _store.DeleteNotificationAsync("boundary");
        Assert.Equal(0, await _store.DeleteNotificationsThroughAsync(cutoff));
        Assert.Equal(1, await _store.DeleteNotificationsThroughAsync(cutoff.AddTicks(1)));

        Assert.Empty(await _store.QueryNotificationsAsync(new NotificationHistoryQuery()));
        Assert.Empty(await _store.ListNotificationSourcesAsync());
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_databasePath + suffix);
        return Task.CompletedTask;
    }

    private static NotificationRecord Notification(
        string id,
        DateTimeOffset createdAt,
        string source = "test-source",
        NotificationSeverity severity = NotificationSeverity.Info) => new()
    {
        Id = id,
        Origin = NotificationOrigin.Application,
        Source = source,
        Title = $"Title {id}",
        Body = $"Body {id}",
        Channels = NotificationChannels.Danmaku,
        Severity = severity,
        SubjectKey = id,
        CreatedAt = createdAt
    };

    private static UsageObservation Usage(
        DateOnly date,
        string client,
        string model,
        long input,
        long output,
        decimal cost,
        long cacheRead = 0,
        long cacheWrite = 0,
        string provider = "local",
        string deviceId = "test-device") => new()
    {
        Date = date,
        DeviceId = deviceId,
        Client = client,
        Provider = provider,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheWriteTokens = cacheWrite,
        MessageCount = 1,
        CostUsd = cost
    };

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ExpiredVpnClient : IVpnQuotaClient
    {
        public Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VpnSubscriptionSnapshot> GetSubscriptionAsync(string authData, CancellationToken cancellationToken = default) =>
            throw new PassGoApiException("登录状态已失效", true);
    }

    private sealed class ActiveVpnClient : IVpnQuotaClient
    {
        public Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VpnSubscriptionSnapshot> GetSubscriptionAsync(string authData, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VpnSubscriptionSnapshot
            {
                Email = "user@example.com",
                PlanName = "年度套餐",
                UploadedBytes = 100,
                DownloadedBytes = 200,
                TransferLimitBytes = 1000,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(5),
                ResetDay = 1
            });
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
}
