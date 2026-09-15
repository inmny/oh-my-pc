using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OhMyPc.App.Dialogs;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Infrastructure.Vpn;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyPc.App.Views;

public partial class VpnView : UserControl
{
    public VpnView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private LocalizationService Text => ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();

    private async void ConnectVpn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VpnLoginDialog(Text, ViewModel.Vpn.Email) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await ViewModel.Vpn.ConnectAsync(dialog.Email, dialog.Password);
        }
        catch (Exception exception) when (exception is PassGoApiException or HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            System.Windows.MessageBox.Show(
                Text.Format("Message_VpnLoginFailed", exception.Message),
                Text["Vpn_LoginTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RemoveVpn_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            Text.Format("Message_RemoveVpn", ViewModel.Vpn.Email),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ViewModel.Vpn.RemoveAsync();
    }
}
