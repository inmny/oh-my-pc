using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.Themes;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure;
using OhMyPc.Infrastructure.LocalApi;
using OhMyPc.Infrastructure.Logging;
using OhMyPc.Infrastructure.Persistence;
using Velopack;

namespace OhMyPc.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\OhMyPc.SingleInstance";
    private const string ShowEventName = @"Local\OhMyPc.ShowDashboard";
    private IHost? _host;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;
    private bool _ownsMutex;
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("应用服务尚未初始化。");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Velopack 更新钩子必须最先执行：处理更新后首次启动、安装/卸载等生命周期事件
        VelopackApp.Build().Run();
        DispatcherUnhandledException += (_, args) =>
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(args.Exception, "界面操作失败");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LiveCharts.Configure(config => config.AddDefaultTheme(requestedTheme: LvcThemeKind.Dark));

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _instanceMutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            _showWindowEvent.Set();
            Shutdown();
            return;
        }

        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _host?.Services.GetRequiredService<WindowManager>().ShowMainWindow()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Logging.AddProvider(new DailyFileLoggerProvider());
            builder.Services.AddSingleton<LocalizationService>();
            builder.Services.AddSingleton<ITextLocalizer>(services => services.GetRequiredService<LocalizationService>());
            builder.Services.AddOhMyPcInfrastructure();
            builder.Services.AddSingleton<StartupRegistrationService>();
            builder.Services.AddSingleton<DesktopNotificationSink>();
            builder.Services.AddSingleton<WindowManager>();
            builder.Services.AddSingleton<TrayPopupWindow>();
            builder.Services.AddSingleton<TrayService>();
            builder.Services.AddSingleton<DanmakuOverlayService>();
            builder.Services.AddSingleton<NotificationCenterViewModel>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<VpnQuotaViewModel>();
            builder.Services.AddSingleton<ProxyViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();
            await _host.Services.GetRequiredService<DatabaseBootstrapper>().InitializeAsync();
            var store = _host.Services.GetRequiredService<IAppStore>();
            var settings = await store.GetSettingsAsync();
            try
            {
                var retentionDays = NotificationRetentionPolicy.Normalize(settings.NotificationHistoryRetentionDays);
                await store.PruneNotificationsAsync(DateTimeOffset.UtcNow.AddDays(-retentionDays));
            }
            catch (Exception exception)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning(exception, "启动时清理通知历史失败");
            }

            var localization = _host.Services.GetRequiredService<LocalizationService>();
            Resources["Localization"] = localization;
            localization.Apply(settings.Language);
            _host.Services.GetRequiredService<DesktopNotificationSink>().Start();
            _host.Services.GetRequiredService<TrayService>().Start();
            _host.Services.GetRequiredService<DanmakuOverlayService>().Start();
            await _host.StartAsync();
            try
            {
                await _host.Services.GetRequiredService<LocalNotificationApiService>().ApplySettingsAsync(settings);
            }
            catch (Exception exception) when (exception is LocalNotificationApiException or ArgumentOutOfRangeException)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogError(exception, "本地弹幕 API 启动失败");
            }
            await _host.Services.GetRequiredService<MainViewModel>().LoadAsync();

            if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            {
                _host.Services.GetRequiredService<WindowManager>().ShowMainWindow();
            }
        }
        catch (Exception exception)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(exception, "应用启动失败");
            System.Windows.MessageBox.Show(
                string.Format(TryFindResource("Message_StartupError") as string ?? "Oh My PC 无法启动。\n\n{0}", exception.Message),
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowRegistration?.Unregister(null);
        _showWindowEvent?.Dispose();
        var host = _host;
        if (host is not null)
        {
            try
            {
                Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                host.Services.GetService<ILogger<App>>()?.LogError(exception, "应用服务停止失败");
            }
            finally
            {
                host.Dispose();
            }
        }

        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
