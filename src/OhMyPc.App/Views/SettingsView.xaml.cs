using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Infrastructure.LocalApi;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyPc.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private LocalizationService Text => ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();
    private ILogger<SettingsView> Logger => ((App)System.Windows.Application.Current).Services.GetRequiredService<ILogger<SettingsView>>();

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Validation.GetHasError(LocalApiPortBox))
        {
            System.Windows.MessageBox.Show(
                Text.Format(
                    "Message_InvalidLocalApiPort",
                    LocalNotificationApiService.MinimumPort,
                    LocalNotificationApiService.MaximumPort),
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string? error;
        try
        {
            error = await ViewModel.SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "设置操作失败");
            error = Text["Message_SettingsSaveFailed"];
        }

        if (error is not null)
        {
            System.Windows.MessageBox.Show(error, "Oh My PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.NotificationCenter.ShowTestNotificationAsync();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "测试通知发布失败");
            System.Windows.MessageBox.Show(
                Text["Message_NotificationPublishFailed"],
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
