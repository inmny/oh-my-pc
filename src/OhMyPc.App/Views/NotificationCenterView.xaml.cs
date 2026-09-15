using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhMyPc.App.Dialogs;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Core;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;

namespace OhMyPc.App.Views;

public partial class NotificationCenterView : UserControl
{
    public NotificationCenterView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private LocalizationService Text => ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();
    private IAutomationCatalog AutomationCatalog => ((App)System.Windows.Application.Current).Services.GetRequiredService<IAutomationCatalog>();
    private ILogger<NotificationCenterView> Logger => ((App)System.Windows.Application.Current).Services.GetRequiredService<ILogger<NotificationCenterView>>();

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleDialog(Text, AutomationCatalog, null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await ViewModel.NotificationCenter.SaveRuleAsync(dialog.Rule);
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NotificationCenter.SelectedRule is null) return;
        var dialog = new RuleDialog(Text, AutomationCatalog, ViewModel.NotificationCenter.SelectedRule) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await ViewModel.NotificationCenter.SaveRuleAsync(dialog.Rule);
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NotificationCenter.SelectedRule is null) return;
        var answer = System.Windows.MessageBox.Show(
            Text.Format("Message_DeleteRule", Text.GetRuleName(ViewModel.NotificationCenter.SelectedRule)),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ViewModel.NotificationCenter.DeleteSelectedRuleAsync();
    }

    private async void NotificationFilter_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox comboBox) return;
        comboBox.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
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
            Text.Format("Message_DeleteNotification", selected.Title),
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
            Logger.LogError(exception, "删除通知历史失败");
            System.Windows.MessageBox.Show(
                Text["Message_NotificationHistoryOperationFailed"],
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ClearNotificationHistory_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            Text["Message_ClearNotificationHistory"],
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
            Logger.LogError(exception, "清空通知历史失败");
            System.Windows.MessageBox.Show(
                Text["Message_NotificationHistoryOperationFailed"],
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
