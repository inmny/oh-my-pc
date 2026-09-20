using System.Windows;
using OhMyPc.App.Services;

namespace OhMyPc.App.Dialogs;

public partial class DshPassphraseDialog : Window
{
    private readonly LocalizationService _text;

    public DshPassphraseDialog(LocalizationService text, string keyPath)
    {
        _text = text;
        InitializeComponent();
        HintText.Text = string.Format(text["Dsh_PassphraseHint"], keyPath);
        OkButton.Content = text["Common_Confirm"];
        Loaded += (_, _) => PassphraseBox.Focus();
    }

    public string? Passphrase { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PassphraseBox.Password))
        {
            System.Windows.MessageBox.Show(
                _text["Dsh_PassphraseRequired"],
                _text["Dsh_PassphraseTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Passphrase = PassphraseBox.Password;
        DialogResult = true;
    }
}
