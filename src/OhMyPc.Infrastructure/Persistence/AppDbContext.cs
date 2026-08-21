using Microsoft.EntityFrameworkCore;

namespace OhMyPc.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DataSourceEntity> DataSources => Set<DataSourceEntity>();
    public DbSet<CredentialEntity> Credentials => Set<CredentialEntity>();
    public DbSet<DailyUsageEntity> DailyUsage => Set<DailyUsageEntity>();
    public DbSet<QuotaCurrentEntity> CurrentQuotas => Set<QuotaCurrentEntity>();
    public DbSet<AutomationRuleEntity> AutomationRules => Set<AutomationRuleEntity>();
    public DbSet<AutomationRuleStateEntity> AutomationRuleStates => Set<AutomationRuleStateEntity>();
    public DbSet<AutomationSourceStateEntity> AutomationSourceStates => Set<AutomationSourceStateEntity>();
    public DbSet<VpnAccountEntity> VpnAccounts => Set<VpnAccountEntity>();
    public DbSet<VpnDailyUsageEntity> VpnDailyUsage => Set<VpnDailyUsageEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DataSourceEntity>(entity =>
        {
            entity.ToTable("DataSources");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.BaseUrl).HasMaxLength(1024);
            entity.Property(x => x.ModelStatusUrl).HasMaxLength(1024);
            entity.HasOne(x => x.Credential)
                .WithOne(x => x.Source)
                .HasForeignKey<CredentialEntity>(x => x.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CredentialEntity>(entity =>
        {
            entity.ToTable("Credentials");
            entity.HasKey(x => x.SourceId);
        });

        modelBuilder.Entity<DailyUsageEntity>(entity =>
        {
            entity.ToTable("DailyUsage");
            entity.HasKey(x => new { x.Date, x.DeviceId, x.Client, x.Provider, x.Model });
            entity.HasIndex(x => new { x.Date, x.Client });
        });

        modelBuilder.Entity<QuotaCurrentEntity>(entity =>
        {
            entity.ToTable("CurrentQuotas");
            entity.HasKey(x => new { x.SourceId, x.WindowKey });
            entity.HasOne(x => x.Source)
                .WithMany(x => x.Quotas)
                .HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationRuleEntity>(entity =>
        {
            entity.ToTable("AutomationRules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Enabled, x.EventType });
        });

        modelBuilder.Entity<AutomationRuleStateEntity>(entity =>
        {
            entity.ToTable("AutomationRuleStates");
            entity.HasKey(x => new { x.RuleId, x.SubjectKey });
            entity.HasOne(x => x.Rule)
                .WithMany(x => x.States)
                .HasForeignKey(x => x.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationSourceStateEntity>(entity =>
        {
            entity.ToTable("AutomationSourceStates");
            entity.HasKey(x => x.Key);
        });

        modelBuilder.Entity<VpnAccountEntity>(entity =>
        {
            entity.ToTable("VpnAccounts");
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<VpnDailyUsageEntity>(entity =>
        {
            entity.ToTable("VpnDailyUsage");
            entity.HasKey(x => x.Date);
        });

        modelBuilder.Entity<NotificationEntity>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.Source).HasMaxLength(256);
            entity.Property(x => x.Title).HasMaxLength(120);
            entity.Property(x => x.Body).HasMaxLength(1000);
            entity.Property(x => x.SubjectKey).HasMaxLength(256);
            entity.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending();
            entity.HasIndex(x => new { x.Source, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            entity.HasIndex(x => new { x.Severity, x.CreatedAt, x.Id }).IsDescending(false, true, true);
        });

        modelBuilder.Entity<SettingEntity>(entity =>
        {
            entity.ToTable("Settings");
            entity.HasKey(x => x.Key);
        });
    }
}
