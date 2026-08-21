using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Automation;
using OhMyPc.Infrastructure.Persistence;

namespace OhMyPc.IntegrationTests;

public sealed class AutomationTests
{
    [Fact]
    public async Task Migration_ConvertsLegacyRuleAndState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260808132725_InitialCreate");
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO NotificationRules
                    (Id, Name, Enabled, TriggerKind, Operator, SourceId, WindowKey, Threshold, MatchText, Channels, Severity, CooldownMinutes, RespectQuietHours)
                VALUES
                    ('custom-quota', 'Custom quota', 1, 0, 0, 'source-1', 'daily', 15, NULL, 5, 1, 30, 1);
                INSERT INTO NotificationStates
                    (RuleId, SubjectKey, LastMatched, LastNumericValue, LastTextValue, LastNotifiedAt)
                VALUES
                    ('custom-quota', 'source-1:quota:daily:QuotaRemainingPercent', 1, 10, NULL, '2026-08-10T10:00:00.0000000+00:00');
                """);
        }

        await new DatabaseBootstrapper(factory).InitializeAsync();
        var store = new AppStore(factory, new CredentialProtector());
        var rule = Assert.Single(await store.ListRulesAsync(), item => item.Id == "custom-quota");

        Assert.Equal(AutomationEventTypes.QuotaObserved, rule.EventType);
        Assert.Equal(3, rule.Conditions.Count);
        Assert.Equal("source-1", rule.Conditions.Single(condition => condition.Field == "sourceId").Value!.GetValue<string>());
        Assert.Equal(15, rule.Conditions.Single(condition => condition.Field == "remainingPercent").Value!.GetValue<double>());
        var action = LocalNotificationActionOptions.FromDefinition(Assert.Single(rule.Actions));
        Assert.Equal(NotificationChannels.Danmaku | NotificationChannels.Tray, action.Channels);
        Assert.NotNull(await store.GetRuleStateAsync("custom-quota", "source-1:quota:daily"));
        Assert.Contains(await store.ListRulesAsync(), item => item.Id == "input-model-gpt-5.6-sol");

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
    }

    [Fact]
    public async Task Engine_MatchesEventAndHonorsCooldown()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"oh-my-pc-engine-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await new DatabaseBootstrapper(factory).InitializeAsync();
        var store = new AppStore(factory, new CredentialProtector());
        await store.SaveRuleAsync(new AutomationRuleDefinition
        {
            Id = "engine-test",
            Name = "Engine test",
            EventType = "test.event",
            Conditions =
            [
                new AutomationConditionDefinition
                {
                    Field = "title",
                    Operator = AutomationConditionOperator.Contains,
                    ValueKind = AutomationValueKind.Text,
                    Value = JsonValue.Create("important")
                }
            ],
            Actions =
            [
                new LocalNotificationActionOptions
                {
                    Channels = NotificationChannels.Tray,
                    Severity = NotificationSeverity.Info
                }.ToDefinition()
            ],
            CooldownMinutes = 60,
            RespectQuietHours = false
        });
        var sink = new CaptureNotificationSink();
        var engine = new AutomationEngine(
            store,
            new AutomationRuleMatcher(),
            [new LocalNotificationActionHandler(sink)]);
        var automationEvent = new AutomationEvent
        {
            Type = "test.event",
            SourceId = "source-1",
            SubjectKey = "test:subject",
            Title = "Title",
            Body = "Body",
            Fields = new JsonObject { ["title"] = "Important update" }
        };

        await engine.PublishAsync(automationEvent);
        await engine.PublishAsync(automationEvent);

        var message = Assert.Single(sink.Messages);
        Assert.Equal(NotificationOrigin.Automation, message.Origin);
        Assert.Equal("source-1", message.Source);

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
    }

    private sealed class CaptureNotificationSink : INotificationSink
    {
        public List<NotificationMessage> Messages { get; } = [];

        public Task<NotificationRecord> PublishAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(ToRecord(message));
        }

        private static NotificationRecord ToRecord(NotificationMessage message) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Origin = message.Origin,
            Source = message.Source,
            Title = message.Title,
            Body = message.Body,
            Channels = message.Channels,
            Severity = message.Severity,
            SubjectKey = message.SubjectKey,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
