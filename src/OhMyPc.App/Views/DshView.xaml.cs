using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OhMyPc.App.Dialogs;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Dsh;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyPc.App.Views;

public partial class DshView : UserControl
{
    public DshView()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) =>
            LogList.ScrollIntoView(LogList.Items.Count > 0 ? LogList.Items[^1] : null);
    }

    private DshViewModel ViewModel => ((MainViewModel)DataContext).Dsh;
    private LocalizationService Text => ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DshServerDialog(Text, ViewModel.TestConnectionAsync) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        await ViewModel.SaveServerAsync(dialog.Server, dialog.Password);
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DshServerItemViewModel item) return;
        var dialog = new DshServerDialog(Text, ViewModel.TestConnectionAsync, item.Server) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        item.UpdateServer(dialog.Server);
        await ViewModel.SaveServerAsync(dialog.Server, dialog.Password);
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DshServerItemViewModel item) return;
        var answer = System.Windows.MessageBox.Show(
            string.Format(Text["Message_DeleteDshServer"], item.DisplayName),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ViewModel.DeleteServerAsync(item.Server.Id);
    }

    private async void SyncServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DshServerItemViewModel item) return;
        IReadOnlyList<DshConfigItem> items;
        try
        {
            items = DshConfigSyncService.DiscoverItems();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                string.Format(Text["Message_DshSyncFailed"], exception.Message),
                Text["Dsh_SyncTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show(
                Text["Message_DshSyncEmpty"],
                Text["Dsh_SyncTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var preselected = (item.Server.ConfigSyncSelection ?? "").Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dialog = new DshSyncDialog(Text, items, preselected) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Selected is null) return;
        await ViewModel.SyncConfigAsync(item, dialog.Selected);
    }

    private async void ImportServers_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<SshConfigEntry> entries;
        try
        {
            entries = SshConfigParser.LoadDefault();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                string.Format(Text["Message_DshImportFailed"], exception.Message),
                Text["Dsh_ImportTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (entries.Count == 0)
        {
            System.Windows.MessageBox.Show(
                Text["Message_DshImportEmpty"],
                Text["Dsh_ImportTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new DshImportDialog(Text, entries) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Selected is null) return;
        var (imported, skipped) = await ViewModel.ImportServersAsync(dialog.Selected);
        System.Windows.MessageBox.Show(
            string.Format(Text["Message_DshImportDone"], imported, skipped),
            Text["Dsh_ImportTitle"],
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
