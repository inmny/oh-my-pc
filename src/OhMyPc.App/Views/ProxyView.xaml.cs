using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OhMyPc.App.Dialogs;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyPc.App.Views;

public partial class ProxyView : UserControl
{
    public ProxyView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private LocalizationService Text => ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();

    private void DeleteProxyProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Proxy.SelectedProvider is null) return;
        var answer = System.Windows.MessageBox.Show(
            Text["Message_DeleteProxyProvider"],
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) ViewModel.Proxy.RemoveSelectedProvider();
    }

    private void AddProviderKind_Click(object sender, RoutedEventArgs e)
    {
        // 添加动作由按钮的 AddProviderCommand 完成，这里只负责收起下拉菜单
        AddProviderToggle.IsChecked = false;
    }

    private void AddProxyModel_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Proxy.SelectedProvider is null) return;
        var dialog = new ProxyModelDialog(Text, null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.AddModelToSelected(dialog.Model);
    }

    private void EditProxyModel_Click(object sender, RoutedEventArgs e)
    {
        var provider = ViewModel.Proxy.SelectedProvider;
        if (provider?.SelectedModel is null) return;
        var dialog = new ProxyModelDialog(Text, provider.SelectedModel.Source) { Owner = Window.GetWindow(this) };
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
                Text.Format("Proxy_FetchModelsFailed", exception.Message),
                "Oh My PC",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (rows.Count == 0)
        {
            System.Windows.MessageBox.Show(Text["Proxy_NoRemoteModels"], "Oh My PC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ProxyModelImportDialog(Text, provider.TitleText, rows) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.ApplyImportedModels(rows);
    }

    private void EditUnifiedModel_Click(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.Proxy.SelectedUnifiedModel;
        if (row is null) return;
        var dialog = new ProxyUnifiedModelDialog(Text, row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) ViewModel.Proxy.ApplyUnifiedModel(row, dialog.Result);
    }
}
