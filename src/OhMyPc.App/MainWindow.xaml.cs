using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Dialogs;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.LocalApi;
using OhMyPc.Infrastructure.Vpn;

namespace OhMyPc.App;

public partial class MainWindow : Window
{
    private readonly WindowManager _windows;
    private readonly LocalizationService _text;
    private readonly IAutomationCatalog _automationCatalog;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        MainViewModel viewModel,
        WindowManager windows,
        LocalizationService text,
        IAutomationCatalog automationCatalog,
        ILogger<MainWindow> logger)
    {
        _windows = windows;
        _text = text;
        _automationCatalog = automationCatalog;
        _logger = logger;
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_windows.ExitRequested)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private async void ImportEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var count = await ViewModel.ImportEnvironmentAsync();
        System.Windows.MessageBox.Show(_text.Format("Message_ImportedSources", count), "Oh My PC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SourceDialog(_text) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.SaveSourceAsync(dialog.Source, dialog.ApiKey);
    }

    private async void EditQuotaSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuotaSourceCardViewModel card) return;
        var dialog = new SourceDialog(_text, card.Source) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.SaveSourceAsync(dialog.Source, dialog.ApiKey);
    }

    private async void RefreshModelStatus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuotaSourceCardViewModel card)
        {
            await ViewModel.RefreshModelStatusAsync(card.SourceId);
        }
    }

    private async void DeleteQuotaSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuotaSourceCardViewModel card) return;
        var answer = System.Windows.MessageBox.Show(
            _text.Format("Message_DeleteSource", card.SourceName),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            ViewModel.SelectedSource = card.Source;
            await ViewModel.DeleteSelectedSourceAsync();
        }
    }

    private async void ConnectVpn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VpnLoginDialog(_text, ViewModel.Vpn.Email) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await ViewModel.Vpn.ConnectAsync(dialog.Email, dialog.Password);
        }
        catch (Exception exception) when (exception is PassGoApiException or HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            System.Windows.MessageBox.Show(
                _text.Format("Message_VpnLoginFailed", exception.Message),
                _text["Vpn_LoginTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RemoveVpn_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            _text.Format("Message_RemoveVpn", ViewModel.Vpn.Email),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ViewModel.Vpn.RemoveAsync();
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleDialog(_text, _automationCatalog, null) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.NotificationCenter.SaveRuleAsync(dialog.Rule);
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NotificationCenter.SelectedRule is null) return;
        var dialog = new RuleDialog(_text, _automationCatalog, ViewModel.NotificationCenter.SelectedRule) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.NotificationCenter.SaveRuleAsync(dialog.Rule);
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NotificationCenter.SelectedRule is null) return;
        var answer = System.Windows.MessageBox.Show(
            _text.Format("Message_DeleteRule", _text.GetRuleName(ViewModel.NotificationCenter.SelectedRule)),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ViewModel.NotificationCenter.DeleteSelectedRuleAsync();
    }

    private async void NotificationFilter_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not System.Windows.Controls.ComboBox comboBox) return;
        comboBox.GetBindingExpression(System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateSource();
        await ViewModel.NotificationCenter.ReloadHistoryAsync();
    }

    private async void RefreshNotificationHistory_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.NotificationCenter.ReloadHistoryAsync();

    private void ReplayNotification_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NotificationCenter.ReplaySelectedNotification();

    private async void DeleteNotification_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.NotificationCenter.SelectedNotification;
        if (selected is null) return;
        var answer = System.Windows.MessageBox.Show(
            _text.Format("Message_DeleteNotification", selected.Title),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await ViewModel.NotificationCenter.DeleteSelectedNotificationAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "删除通知历史失败");
            System.Windows.MessageBox.Show(
                _text["Message_NotificationHistoryOperationFailed"],
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ClearNotificationHistory_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            _text["Message_ClearNotificationHistory"],
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await ViewModel.NotificationCenter.ClearHistoryAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "清空通知历史失败");
            System.Windows.MessageBox.Show(
                _text["Message_NotificationHistoryOperationFailed"],
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Controls.Validation.GetHasError(LocalApiPortBox))
        {
            System.Windows.MessageBox.Show(
                _text.Format(
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
            _logger.LogError(exception, "设置操作失败");
            error = _text["Message_SettingsSaveFailed"];
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
            _logger.LogError(exception, "测试通知发布失败");
            System.Windows.MessageBox.Show(
                _text["Message_NotificationPublishFailed"],
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
    private void DeleteProxyProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Proxy.SelectedProvider is null) return;
        var answer = System.Windows.MessageBox.Show(
            _text["Message_DeleteProxyProvider"],
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) ViewModel.Proxy.RemoveSelectedProvider();
    }

    private void AddProxyModel_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Proxy.SelectedProvider is null) return;
        var dialog = new ProxyModelDialog(_text, null) { Owner = this };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.AddModelToSelected(dialog.Model);
    }

    private void EditProxyModel_Click(object sender, RoutedEventArgs e)
    {
        var provider = ViewModel.Proxy.SelectedProvider;
        if (provider?.SelectedModel is null) return;
        var dialog = new ProxyModelDialog(_text, provider.SelectedModel.Source) { Owner = this };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.UpdateSelectedModel(dialog.Model);
    }

    private void DeleteProxyModel_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Proxy.RemoveSelectedModel();

    private async void FetchProxyModels_Click(object sender, RoutedEventArgs e)
    {
        var provider = ViewModel.Proxy.SelectedProvider;
        if (provider is null) return;
        IReadOnlyList<ViewModels.ProxyImportModelRow> rows;
        try
        {
            rows = await ViewModel.Proxy.PrepareImportRowsAsync(provider.ToConfig());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                _text.Format("Proxy_FetchModelsFailed", exception.Message),
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (rows.Count == 0)
        {
            System.Windows.MessageBox.Show(_text["Proxy_NoRemoteModels"], "Oh My PC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ProxyModelImportDialog(_text, provider.TitleText, rows) { Owner = this };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.ApplyImportedModels(rows);
    }

    private void EditUnifiedModel_Click(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.Proxy.SelectedUnifiedModel;
        if (row is null) return;
        var dialog = new ProxyUnifiedModelDialog(_text, row) { Owner = this };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.ApplyUnifiedModel(row, dialog.Result);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _windows.Exit();
}
