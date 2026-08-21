using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Persistence;

public sealed class AppStore(
    IDbContextFactory<AppDbContext> contextFactory,
    CredentialProtector credentialProtector) : IAppStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DataSourceDefinition>> ListDataSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DataSources.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => ToDefinition(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<DataSourceDefinition?> GetDataSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DataSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToDefinition(entity);
    }

    public async Task SaveDataSourceAsync(DataSourceDefinition source, string? apiKey, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DataSources.SingleOrDefaultAsync(x => x.Id == source.Id, cancellationToken);
        if (entity is null)
        {
            entity = new DataSourceEntity { Id = source.Id };
            db.DataSources.Add(entity);
        }

        Apply(source, entity);
        if (apiKey is not null)
        {
            var credential = await db.Credentials.SingleOrDefaultAsync(x => x.SourceId == source.Id, cancellationToken);
            if (credential is null)
            {
                credential = new CredentialEntity { SourceId = source.Id };
                db.Credentials.Add(credential);
            }

            credential.EncryptedValue = credentialProtector.Protect(apiKey);
            credential.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDataSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.DataSources.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null) return;
        var statusKeyPrefix = $"input-status:source:{id}:";
        db.AutomationSourceStates.RemoveRange(await db.AutomationSourceStates
            .Where(state => state.Key.StartsWith(statusKeyPrefix))
            .ToListAsync(cancellationToken));
        db.AutomationRuleStates.RemoveRange(await db.AutomationRuleStates
            .Where(state => state.SubjectKey.StartsWith(statusKeyPrefix))
            .ToListAsync(cancellationToken));
        db.DataSources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDataSourceHealthAsync(DataSourceDefinition source, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DataSources.SingleAsync(x => x.Id == source.Id, cancellationToken);
        entity.Status = (int)source.Status;
        entity.LastAttemptAt = Format(source.LastAttemptAt);
        entity.LastSuccessAt = Format(source.LastSuccessAt);
        entity.LastError = source.LastError;
        entity.ConsecutiveFailures = source.ConsecutiveFailures;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetCredentialAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.Credentials.AsNoTracking()
            .Where(x => x.SourceId == sourceId)
            .Select(x => x.EncryptedValue)
            .SingleOrDefaultAsync(cancellationToken);
        return value is null ? null : credentialProtector.Unprotect(value);
    }

    public async Task UpsertUsageAsync(IReadOnlyCollection<UsageObservation> observations, CancellationToken cancellationToken = default)
    {
        if (observations.Count == 0) return;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var scope in observations
            .Select(item => (Date: item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.DeviceId))
            .Distinct())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "DailyUsage"
                WHERE "Date" = {scope.Date} AND "DeviceId" = {scope.DeviceId};
                """, cancellationToken);
        }

        await UpsertUsageRowsAsync(db, observations, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceUsageAsync(
        IReadOnlyCollection<UsageObservation> observations,
        IReadOnlyCollection<UsageObservationScope> scopes,
        CancellationToken cancellationToken = default)
    {
        var distinctScopes = scopes.Distinct().ToArray();
        if (distinctScopes.Any(scope => string.IsNullOrWhiteSpace(scope.DeviceId)))
        {
            throw new ArgumentException("设备 ID 不能为空。", nameof(scopes));
        }
        var scopeSet = distinctScopes.ToHashSet();
        if (observations.Any(item => !scopeSet.Contains(new UsageObservationScope(item.Date, item.DeviceId))))
        {
            throw new ArgumentException("用量观测必须属于指定的替换范围。", nameof(observations));
        }
        if (distinctScopes.Length == 0) return;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var scope in distinctScopes)
        {
            var date = scope.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "DailyUsage"
                WHERE "Date" = {date} AND "DeviceId" = {scope.DeviceId};
                """, cancellationToken);
        }
        await UpsertUsageRowsAsync(db, observations, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertUsageRowsAsync(
        AppDbContext db,
        IEnumerable<UsageObservation> observations,
        CancellationToken cancellationToken)
    {
        foreach (var item in observations)
        {
            var date = item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var costMicroUsd = (long)decimal.Round(item.CostUsd * 1_000_000m, 0, MidpointRounding.AwayFromZero);
            var observedAt = item.ObservedAt.ToUniversalTime().ToString("O");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DailyUsage"
                    ("Date", "DeviceId", "Client", "Provider", "Model", "InputTokens", "OutputTokens",
                     "CacheReadTokens", "CacheWriteTokens", "ReasoningTokens", "TotalTokens", "MessageCount",
                     "ActiveTimeMs", "CostMicroUsd", "ObservedAt")
                VALUES
                    ({date}, {item.DeviceId}, {item.Client}, {item.Provider}, {item.Model}, {item.InputTokens}, {item.OutputTokens},
                     {item.CacheReadTokens}, {item.CacheWriteTokens}, {item.ReasoningTokens}, {item.TotalTokens}, {item.MessageCount},
                     {item.ActiveTimeMs}, {costMicroUsd}, {observedAt})
                ON CONFLICT("Date", "DeviceId", "Client", "Provider", "Model") DO UPDATE SET
                    "InputTokens" = excluded."InputTokens",
                    "OutputTokens" = excluded."OutputTokens",
                    "CacheReadTokens" = excluded."CacheReadTokens",
                    "CacheWriteTokens" = excluded."CacheWriteTokens",
                    "ReasoningTokens" = excluded."ReasoningTokens",
                    "TotalTokens" = excluded."TotalTokens",
                    "MessageCount" = excluded."MessageCount",
                    "ActiveTimeMs" = excluded."ActiveTimeMs",
                    "CostMicroUsd" = excluded."CostMicroUsd",
                    "ObservedAt" = excluded."ObservedAt";
                """, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<UsageTrendPoint>> QueryUsageAsync(
        DateOnly from,
        DateOnly to,
        string? client = null,
        CancellationToken cancellationToken = default)
    {
        var start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DailyUsage.AsNoTracking().Where(x => string.Compare(x.Date, start) >= 0 && string.Compare(x.Date, end) <= 0);
        if (!string.IsNullOrWhiteSpace(client)) query = query.Where(x => x.Client == client);

        var rows = await query.GroupBy(x => x.Date)
            .Select(group => new
            {
                Date = group.Key,
                Input = group.Sum(x => x.InputTokens),
                Output = group.Sum(x => x.OutputTokens),
                CacheRead = group.Sum(x => x.CacheReadTokens),
                CacheWrite = group.Sum(x => x.CacheWriteTokens),
                Messages = group.Sum(x => x.MessageCount),
                Active = group.Sum(x => x.ActiveTimeMs),
                Cost = group.Sum(x => x.CostMicroUsd)
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new UsageTrendPoint
        {
            Date = DateOnly.ParseExact(x.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            InputTokens = x.Input,
            OutputTokens = x.Output,
            CacheReadTokens = x.CacheRead,
            CacheWriteTokens = x.CacheWrite,
            MessageCount = x.Messages,
            ActiveTimeMs = x.Active,
            CostUsd = x.Cost / 1_000_000m
        }).ToList();
    }

    public async Task<IReadOnlyList<UsageBreakdownPoint>> QueryUsageBreakdownAsync(
        DateOnly from,
        DateOnly to,
        UsageBreakdownGroup group,
        CancellationToken cancellationToken = default)
    {
        var start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DailyUsage.AsNoTracking()
            .Where(x => string.Compare(x.Date, start) >= 0
                && string.Compare(x.Date, end) <= 0
                && x.Client != "_activity");
        var grouped = group == UsageBreakdownGroup.Model
            ? query.GroupBy(x => x.Model)
            : query.GroupBy(x => x.Client);
        var rows = await grouped
            .Select(items => new
            {
                Name = items.Key,
                Input = items.Sum(x => x.InputTokens),
                Output = items.Sum(x => x.OutputTokens),
                CacheRead = items.Sum(x => x.CacheReadTokens),
                CacheWrite = items.Sum(x => x.CacheWriteTokens),
                Cost = items.Sum(x => x.CostMicroUsd),
                Total = items.Sum(x => x.TotalTokens)
            })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new UsageBreakdownPoint
        {
            Name = x.Name,
            TotalTokens = x.Total,
            InputTokens = x.Input,
            OutputTokens = x.Output,
            CacheReadTokens = x.CacheRead,
            CacheWriteTokens = x.CacheWrite,
            CostUsd = x.Cost / 1_000_000m
        }).ToList();
    }

    public async Task<UsageTrendPoint> GetTodayUsageAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var rows = await QueryUsageAsync(today, today, cancellationToken: cancellationToken);
        return rows.SingleOrDefault() ?? new UsageTrendPoint { Date = today };
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> ListCurrentQuotasAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CurrentQuotas.AsNoTracking()
            .Include(x => x.Source)
            .OrderBy(x => x.Source.Name)
            .ThenBy(x => x.WindowKey)
            .ToListAsync(cancellationToken);
        return rows.Select(ToSnapshot).ToList();
    }

    public async Task ReplaceCurrentQuotasAsync(string sourceId, IReadOnlyCollection<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.CurrentQuotas.Where(x => x.SourceId == sourceId).ToListAsync(cancellationToken);
        db.CurrentQuotas.RemoveRange(existing);
        db.CurrentQuotas.AddRange(snapshots.Select(x => new QuotaCurrentEntity
        {
            SourceId = sourceId,
            WindowKey = x.WindowKey,
            Label = x.Label,
            Used = x.Used,
            Limit = x.Limit,
            ProgressLimit = x.ProgressLimit,
            Remaining = x.Remaining,
            Unit = x.Unit,
            ResetAt = Format(x.ResetAt),
            ObservedAt = x.ObservedAt.ToUniversalTime().ToString("O"),
            Status = (int)x.Status,
            Detail = x.Detail
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var json = await db.Settings.AsNoTracking().Where(x => x.Key == "app").Select(x => x.JsonValue).SingleOrDefaultAsync(cancellationToken);
        return json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Settings.SingleOrDefaultAsync(x => x.Key == "app", cancellationToken);
        if (entity is null)
        {
            entity = new SettingEntity { Key = "app" };
            db.Settings.Add(entity);
        }
        entity.JsonValue = JsonSerializer.Serialize(settings, JsonOptions);
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveNotificationAsync(
        NotificationRecord notification,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Notifications.Add(new NotificationEntity
        {
            Id = notification.Id,
            Origin = (int)notification.Origin,
            Source = notification.Source,
            Title = notification.Title,
            Body = notification.Body,
            Channels = (int)notification.Channels,
            Severity = (int)notification.Severity,
            SubjectKey = notification.SubjectKey,
            CreatedAt = Format(notification.CreatedAt)!
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationRecord>> QueryNotificationsAsync(
        NotificationHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = db.Notifications.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            var source = query.Source.Trim();
            rows = rows.Where(x => x.Source == source);
        }
        if (query.Severity is not null)
        {
            var severity = (int)query.Severity.Value;
            rows = rows.Where(x => x.Severity == severity);
        }
        if (query.CreatedFrom is not null)
        {
            var createdFrom = Format(query.CreatedFrom)!;
            rows = rows.Where(x => string.Compare(x.CreatedAt, createdFrom) >= 0);
        }
        if (query.CreatedBefore is not null)
        {
            var createdBefore = Format(query.CreatedBefore)!;
            rows = rows.Where(x => string.Compare(x.CreatedAt, createdBefore) < 0);
        }

        var limit = Math.Clamp(query.Limit, 1, NotificationHistoryQuery.MaximumLimit);
        var entities = await rows
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.Select(ToNotification).ToList();
    }

    public async Task<IReadOnlyList<string>> ListNotificationSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Notifications.AsNoTracking()
            .Where(x => x.Source != "")
            .Select(x => x.Source)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteNotificationAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Notifications.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> DeleteNotificationsThroughAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        var value = Format(cutoff)!;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Notifications
            .Where(x => string.Compare(x.CreatedAt, value) <= 0)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> PruneNotificationsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        var value = Format(cutoff)!;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Notifications
            .Where(x => string.Compare(x.CreatedAt, value) < 0)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<VpnAccountDefinition?> GetVpnAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.VpnAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "passgo", cancellationToken);
        return entity is null ? null : ToVpnAccount(entity);
    }

    public async Task<string?> GetVpnAuthDataAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.VpnAccounts.AsNoTracking()
            .Where(x => x.Id == "passgo")
            .Select(x => x.EncryptedAuthData)
            .SingleOrDefaultAsync(cancellationToken);
        return value is null ? null : credentialProtector.Unprotect(value);
    }

    public async Task SaveVpnAccountAsync(
        VpnAccountDefinition account,
        string? authData = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.VpnAccounts.SingleOrDefaultAsync(x => x.Id == "passgo", cancellationToken);
        if (entity is null)
        {
            entity = new VpnAccountEntity { Id = "passgo" };
            db.VpnAccounts.Add(entity);
        }

        Apply(account, entity);
        if (authData is not null) entity.EncryptedAuthData = credentialProtector.Protect(authData);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertVpnDailyUsageAsync(VpnDailyUsagePoint point, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var date = point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var entity = await db.VpnDailyUsage.SingleOrDefaultAsync(x => x.Date == date, cancellationToken);
        if (entity is null)
        {
            entity = new VpnDailyUsageEntity { Date = date };
            db.VpnDailyUsage.Add(entity);
        }

        entity.UploadedBytes = point.UploadedBytes;
        entity.DownloadedBytes = point.DownloadedBytes;
        entity.TransferLimitBytes = point.TransferLimitBytes;
        entity.ObservedAt = Format(point.ObservedAt)!;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VpnDailyUsagePoint>> QueryVpnDailyUsageAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.VpnDailyUsage.AsNoTracking()
            .Where(x => string.Compare(x.Date, start) >= 0 && string.Compare(x.Date, end) <= 0)
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);
        return rows.Select(ToVpnDailyUsage).ToList();
    }

    public async Task DeleteVpnAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.VpnAccounts.SingleOrDefaultAsync(x => x.Id == "passgo", cancellationToken);
        if (entity is not null) db.VpnAccounts.Remove(entity);
        db.VpnDailyUsage.RemoveRange(await db.VpnDailyUsage.ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationRuleDefinition>> ListRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AutomationRules.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return rows.Select(ToRule).ToList();
    }

    public async Task<IReadOnlyList<AutomationRuleDefinition>> ListRulesForEventAsync(
        string eventType,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AutomationRules.AsNoTracking()
            .Where(x => x.Enabled && x.EventType == eventType)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return rows.Select(ToRule).ToList();
    }

    public async Task SaveRuleAsync(AutomationRuleDefinition rule, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == rule.Id, cancellationToken);
        if (entity is null)
        {
            entity = new AutomationRuleEntity { Id = rule.Id };
            db.AutomationRules.Add(entity);
        }
        Apply(rule, entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return;
        db.AutomationRules.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationRuleState?> GetRuleStateAsync(string ruleId, string subjectKey, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AutomationRuleStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RuleId == ruleId && x.SubjectKey == subjectKey, cancellationToken);
        return entity is null ? null : new AutomationRuleState
        {
            RuleId = entity.RuleId,
            SubjectKey = entity.SubjectKey,
            LastExecutedAt = Parse(entity.LastExecutedAt)
        };
    }

    public async Task SaveRuleStateAsync(AutomationRuleState state, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AutomationRuleStates.SingleOrDefaultAsync(
            x => x.RuleId == state.RuleId && x.SubjectKey == state.SubjectKey,
            cancellationToken);
        if (entity is null)
        {
            entity = new AutomationRuleStateEntity { RuleId = state.RuleId, SubjectKey = state.SubjectKey };
            db.AutomationRuleStates.Add(entity);
        }
        entity.LastExecutedAt = Format(state.LastExecutedAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationSourceState?> GetSourceStateAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AutomationSourceStates.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        return entity is null ? null : new AutomationSourceState
        {
            Key = entity.Key,
            ValueJson = entity.ValueJson,
            UpdatedAt = Parse(entity.UpdatedAt)!.Value
        };
    }

    public async Task SaveSourceStateAsync(AutomationSourceState state, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AutomationSourceStates.SingleOrDefaultAsync(x => x.Key == state.Key, cancellationToken);
        if (entity is null)
        {
            entity = new AutomationSourceStateEntity { Key = state.Key };
            db.AutomationSourceStates.Add(entity);
        }
        entity.ValueJson = state.ValueJson;
        entity.UpdatedAt = Format(state.UpdatedAt)!;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static NotificationRecord ToNotification(NotificationEntity entity) => new()
    {
        Id = entity.Id,
        Origin = (NotificationOrigin)entity.Origin,
        Source = entity.Source,
        Title = entity.Title,
        Body = entity.Body,
        Channels = (NotificationChannels)entity.Channels,
        Severity = (NotificationSeverity)entity.Severity,
        SubjectKey = entity.SubjectKey,
        CreatedAt = Parse(entity.CreatedAt) ?? DateTimeOffset.MinValue
    };

    private static DataSourceDefinition ToDefinition(DataSourceEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Kind = (DataSourceKind)entity.Kind,
        BaseUrl = entity.BaseUrl,
        ModelStatusUrl = entity.ModelStatusUrl,
        Enabled = entity.Enabled,
        PollIntervalSeconds = entity.PollIntervalSeconds,
        Status = (ProviderStatus)entity.Status,
        LastAttemptAt = Parse(entity.LastAttemptAt),
        LastSuccessAt = Parse(entity.LastSuccessAt),
        LastError = entity.LastError,
        ConsecutiveFailures = entity.ConsecutiveFailures
    };

    private static VpnAccountDefinition ToVpnAccount(VpnAccountEntity entity) => new()
    {
        Email = entity.Email,
        PlanName = entity.PlanName,
        UploadedBytes = entity.UploadedBytes,
        DownloadedBytes = entity.DownloadedBytes,
        TransferLimitBytes = entity.TransferLimitBytes,
        ExpiresAt = Parse(entity.ExpiresAt),
        ResetDay = entity.ResetDay,
        Status = (ProviderStatus)entity.Status,
        LastAttemptAt = Parse(entity.LastAttemptAt),
        LastSuccessAt = Parse(entity.LastSuccessAt),
        LastError = entity.LastError
    };

    private static VpnDailyUsagePoint ToVpnDailyUsage(VpnDailyUsageEntity entity) => new()
    {
        Date = DateOnly.ParseExact(entity.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        UploadedBytes = entity.UploadedBytes,
        DownloadedBytes = entity.DownloadedBytes,
        TransferLimitBytes = entity.TransferLimitBytes,
        ObservedAt = Parse(entity.ObservedAt)!.Value
    };

    private static void Apply(VpnAccountDefinition account, VpnAccountEntity entity)
    {
        entity.Email = account.Email;
        entity.PlanName = account.PlanName;
        entity.UploadedBytes = account.UploadedBytes;
        entity.DownloadedBytes = account.DownloadedBytes;
        entity.TransferLimitBytes = account.TransferLimitBytes;
        entity.ExpiresAt = Format(account.ExpiresAt);
        entity.ResetDay = account.ResetDay;
        entity.Status = (int)account.Status;
        entity.LastAttemptAt = Format(account.LastAttemptAt);
        entity.LastSuccessAt = Format(account.LastSuccessAt);
        entity.LastError = account.LastError;
    }

    private static void Apply(DataSourceDefinition source, DataSourceEntity entity)
    {
        entity.Name = source.Name;
        entity.Kind = (int)source.Kind;
        entity.BaseUrl = source.BaseUrl.TrimEnd('/');
        entity.ModelStatusUrl = source.ModelStatusUrl.Trim();
        entity.Enabled = source.Enabled;
        entity.PollIntervalSeconds = Math.Max(60, source.PollIntervalSeconds);
        entity.Status = (int)source.Status;
        entity.LastAttemptAt = Format(source.LastAttemptAt);
        entity.LastSuccessAt = Format(source.LastSuccessAt);
        entity.LastError = source.LastError;
        entity.ConsecutiveFailures = source.ConsecutiveFailures;
    }

    private static QuotaSnapshot ToSnapshot(QuotaCurrentEntity entity) => new()
    {
        SourceId = entity.SourceId,
        SourceName = entity.Source.Name,
        WindowKey = entity.WindowKey,
        Label = entity.Label,
        Used = entity.Used,
        Limit = entity.Limit,
        ProgressLimit = entity.ProgressLimit,
        Remaining = entity.Remaining,
        Unit = entity.Unit,
        ResetAt = Parse(entity.ResetAt),
        ObservedAt = Parse(entity.ObservedAt) ?? DateTimeOffset.UtcNow,
        Status = (ProviderStatus)entity.Status,
        Detail = entity.Detail
    };

    private static AutomationRuleDefinition ToRule(AutomationRuleEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Enabled = entity.Enabled,
        EventType = entity.EventType,
        MatchMode = (AutomationMatchMode)entity.MatchMode,
        Conditions = JsonSerializer.Deserialize<List<AutomationConditionDefinition>>(entity.ConditionsJson, JsonOptions) ?? [],
        Actions = JsonSerializer.Deserialize<List<AutomationActionDefinition>>(entity.ActionsJson, JsonOptions) ?? [],
        CooldownMinutes = entity.CooldownMinutes,
        RespectQuietHours = entity.RespectQuietHours
    };

    private static void Apply(AutomationRuleDefinition rule, AutomationRuleEntity entity)
    {
        entity.Name = rule.Name;
        entity.Enabled = rule.Enabled;
        entity.EventType = rule.EventType;
        entity.MatchMode = (int)rule.MatchMode;
        entity.ConditionsJson = JsonSerializer.Serialize(rule.Conditions, JsonOptions);
        entity.ActionsJson = JsonSerializer.Serialize(rule.Actions, JsonOptions);
        entity.CooldownMinutes = Math.Max(0, rule.CooldownMinutes);
        entity.RespectQuietHours = rule.RespectQuietHours;
    }

    private static DateTimeOffset? Parse(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    private static string? Format(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
}
