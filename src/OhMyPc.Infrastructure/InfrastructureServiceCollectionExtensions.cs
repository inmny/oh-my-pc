using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhMyPc.Core;
using OhMyPc.Infrastructure.Automation;
using OhMyPc.Infrastructure.InputStatus;
using OhMyPc.Infrastructure.LocalApi;
using OhMyPc.Infrastructure.LocalUsage;
using OhMyPc.Infrastructure.Notifications;
using OhMyPc.Infrastructure.Persistence;
using OhMyPc.Infrastructure.Providers;
using OhMyPc.Infrastructure.Vpn;

namespace OhMyPc.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddOhMyPcInfrastructure(this IServiceCollection services)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={AppPaths.DatabasePath};Default Timeout=5"));
        services.AddSingleton<CredentialProtector>();
        services.AddSingleton<DatabaseBootstrapper>();
        services.AddSingleton<IAppStore, AppStore>();
        services.AddSingleton<NotificationCenterService>();
        services.AddSingleton<INotificationSink>(provider => provider.GetRequiredService<NotificationCenterService>());
        services.AddSingleton<INotificationFeed>(provider => provider.GetRequiredService<NotificationCenterService>());
        services.AddSingleton<LocalNotificationApiService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LocalNotificationApiService>());

        services.AddSingleton<AutomationRuleMatcher>();
        services.AddSingleton<IAutomationActionHandler, LocalNotificationActionHandler>();
        services.AddSingleton<AutomationEngine>();
        services.AddSingleton<IAutomationEventPublisher>(provider => provider.GetRequiredService<AutomationEngine>());
        services.AddSingleton<IAutomationEventDescriptorProvider, UsageAutomationDescriptorProvider>();
        services.AddSingleton<IAutomationEventDescriptorProvider, InputStatusAutomationDescriptorProvider>();
        services.AddSingleton<IAutomationValueOptionsProvider, DataSourceAutomationOptionsProvider>();
        services.AddSingleton<IAutomationValueOptionsProvider, QuotaWindowAutomationOptionsProvider>();
        services.AddSingleton<IAutomationValueOptionsProvider, InputModelAutomationOptionsProvider>();
        services.AddSingleton<AutomationCatalog>();
        services.AddSingleton<IAutomationCatalog>(provider => provider.GetRequiredService<AutomationCatalog>());

        services.AddSingleton<LocalToolDetector>();
        services.AddSingleton<TokscaleClient>();
        services.AddSingleton<DshUsageCollector>();
        services.AddSingleton<ILocalUsageCollector, CompositeLocalUsageCollector>();
        services.AddSingleton<LocalUsageRefreshService>();
        services.AddHostedService<LocalUsageWorker>();

        services.AddHttpClient("providers", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OhMyPc/1.0");
        });
        services.AddHttpClient("input-status", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OhMyPc/1.0");
        });
        services.AddHttpClient("passgo", client =>
        {
            client.BaseAddress = new Uri("https://tot.3616666.xyz/api/v1/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OhMyPc/1.0");
        });
        services.AddSingleton<IVpnQuotaClient, PassGoClient>();
        services.AddSingleton<VpnQuotaRefreshService>();
        services.AddHostedService<VpnQuotaPollingWorker>();
        services.AddSingleton<IInputStatusClient, InputStatusClient>();
        services.AddSingleton<InputStatusRefreshService>();
        services.AddHostedService<InputStatusPollingWorker>();
        services.AddSingleton<IQuotaProvider, Sub2ApiProvider>();
        services.AddSingleton<IQuotaProvider, NewApiProvider>();
        services.AddSingleton<IQuotaProvider, ZhipuCodingPlanProvider>();
        services.AddSingleton<EnvironmentSourceImporter>();
        services.AddSingleton<QuotaRefreshService>();
        services.AddHostedService<QuotaPollingWorker>();
        return services;
    }
}
