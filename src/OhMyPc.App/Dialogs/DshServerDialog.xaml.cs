using System.Windows;
using System.Windows.Controls;
using OhMyPc.App.Services;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Dialogs;

public partial class DshServerDialog : Window
{
    private readonly LocalizationService _text;
    private readonly Func<DshServerDefinition, string?, Task<DshProbeResult>> _probe;
    private readonly string _id;
    private readonly string? _existingFingerprint;

    public DshServerDialog(
        LocalizationService text,
        Func<DshServerDefinition, string?, Task<DshProbeResult>> probe,
        DshServerDefinition? server = null)
    {
        _text = text;
        _probe = probe;
        InitializeComponent();
        server ??= new DshServerDefinition();
        _id = server.Id;
        _existingFingerprint = server.HostKeyFingerprint;
        Title = text[server.Host.Length == 0 ? "Dsh_ServerDialogNew" : "Dsh_ServerDialogTitle"];
        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        SshPortBox.Text = server.SshPort.ToString();
        UserBox.Text = server.UserName;
        AuthBox.SelectedIndex = server.AuthKind == DshAuthKind.Password ? 1 : 0;
        KeyPathBox.Text = server.KeyPath ?? "";
        RemotePortBox.Text = server.RemotePort.ToString();
        LocalPortBox.Text = server.LocalPort.ToString();
        NoteBox.Text = server.Note ?? "";
        SaveButton.Content = text["Common_Save"];
    }

    public DshServerDefinition Server { get; private set; } = null!;

    /// <summary>密码语义：密钥认证 → ""（清除已存密码）；密码认证留空 → null（保留已存）；填写 → 新密码。</summary>
    public string? Password { get; private set; }

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切换认证方式仅驱动 XAML 内的显隐触发器，无需额外处理
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildServer(out var server, out var password, out var error))
        {
            TestResultText.Text = error;
            return;
        }

        TestButton.IsEnabled = false;
        TestResultText.Text = _text["Dsh_TestRunning"];
        try
        {
            var result = await _probe(server, password);
            TestResultText.Text = !result.Reachable
                ? string.Format(_text["Dsh_TestUnreachable"], result.Error)
                : result.InstalledVersion is null
                    ? _text["Dsh_TestReachableNoDsh"]
                    : string.Format(_text["Dsh_TestReachable"], result.InstalledVersion);
        }
        catch (Exception exception)
        {
            TestResultText.Text = string.Format(_text["Dsh_TestUnreachable"], exception.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildServer(out var server, out var password, out var error))
        {
            System.Windows.MessageBox.Show(error, _text["Dsh_ServerDialogTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Server = server;
        Password = password;
        DialogResult = true;
    }

    private bool TryBuildServer(out DshServerDefinition server, out string? password, out string error)
    {
        server = null!;
        password = null;
        error = "";
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0 || user.Length == 0)
        {
            error = _text["Message_DshServerRequired"];
            return false;
        }

        if (!int.TryParse(SshPortBox.Text.Trim(), out var sshPort) || sshPort is not (>= 1 and <= 65535)
            || !int.TryParse(RemotePortBox.Text.Trim(), out var remotePort) || remotePort is not (>= 1 and <= 65535)
            || !int.TryParse(LocalPortBox.Text.Trim(), out var localPort) || localPort is not (>= 1 and <= 65535))
        {
            error = _text["Message_DshServerPort"];
            return false;
        }

        var authKind = AuthBox.SelectedIndex == 1 ? DshAuthKind.Password : DshAuthKind.Key;
        password = authKind == DshAuthKind.Password
            ? string.IsNullOrWhiteSpace(PasswordBox.Password) ? null : PasswordBox.Password.Trim()
            : "";

        server = new DshServerDefinition
        {
            Id = _id,
            Name = name.Length == 0 ? host : name,
            Host = host,
            SshPort = sshPort,
            UserName = user,
            AuthKind = authKind,
            KeyPath = string.IsNullOrWhiteSpace(KeyPathBox.Text) ? null : KeyPathBox.Text.Trim(),
            RemotePort = remotePort,
            LocalPort = localPort,
            HostKeyFingerprint = _existingFingerprint,
            Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim()
        };
        return true;
    }
}
