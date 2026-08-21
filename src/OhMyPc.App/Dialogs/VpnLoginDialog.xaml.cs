using System.Windows;
using OhMyPc.App.Services;

namespace OhMyPc.App.Dialogs;

public partial class VpnLoginDialog : Window
{
    private readonly LocalizationService _text;

    public VpnLoginDialog(LocalizationService text, string email = "")
    {
        _text = text;
        InitializeComponent();
        EmailBox.Text = email;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(EmailBox.Text)) EmailBox.Focus();
            else PasswordBox.Focus();
        };
    }

    public string Email => EmailBox.Text.Trim();
    public string Password => PasswordBox.Password;

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        {
            System.Windows.MessageBox.Show(
                _text["Message_VpnCredentialsRequired"],
                _text["Vpn_LoginTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
