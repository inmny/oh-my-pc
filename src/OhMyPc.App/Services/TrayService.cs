using OhMyPc.App.ViewModels;
using Microsoft.Extensions.Logging;
using OhMyPc.Core.Domain;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OhMyPc.App.Services;

public sealed class TrayService(
    DesktopNotificationSink notifications,
    TrayPopupWindow popup,
    WindowManager windows,
    MainViewModel viewModel,
    LocalizationService text,
    ILogger<TrayService> logger) : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private Forms.ContextMenuStrip? _menu;
    private Forms.ToolStripMenuItem? _openItem;
    private Forms.ToolStripMenuItem? _refreshItem;
    private Forms.ToolStripMenuItem? _testItem;
    private Forms.ToolStripMenuItem? _exitItem;

    public void Start()
    {
        _openItem = new Forms.ToolStripMenuItem();
        _openItem.Click += (_, _) => windows.ShowMainWindow();
        _refreshItem = new Forms.ToolStripMenuItem();
        _refreshItem.Click += async (_, _) => await viewModel.RefreshAllAsync();
        _testItem = new Forms.ToolStripMenuItem();
        _testItem.Click += async (_, _) => await ShowTestNotificationAsync();
        _exitItem = new Forms.ToolStripMenuItem();
        _exitItem.Click += (_, _) => windows.Exit();

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.AddRange([_openItem, _refreshItem, _testItem, new Forms.ToolStripSeparator(), _exitItem]);
        _icon = new Forms.NotifyIcon
        {
            Icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true
        };
        ApplyLanguage();
        _icon.MouseUp += Icon_MouseUp;
        _icon.DoubleClick += (_, _) => windows.ShowMainWindow();
        notifications.Published += Notifications_Published;
        text.LanguageChanged += Text_LanguageChanged;
    }

    private void Icon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) popup.ToggleNearTray();
    }

    private void Notifications_Published(object? sender, NotificationRecord message)
    {
        if (_icon is null || (message.Channels & (NotificationChannels.Tray | NotificationChannels.System)) == 0) return;
        var icon = message.Severity switch
        {
            NotificationSeverity.Critical => Forms.ToolTipIcon.Error,
            NotificationSeverity.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info
        };
        _icon.ShowBalloonTip(5000, message.Title, message.Body, icon);
    }

    private async Task ShowTestNotificationAsync()
    {
        try
        {
            await viewModel.NotificationCenter.ShowTestNotificationAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "托盘测试通知发布失败");
        }
    }

    private void Text_LanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    private void ApplyLanguage()
    {
        _openItem!.Text = text["Tray_OpenDashboard"];
        _refreshItem!.Text = text["Tray_RefreshNow"];
        _testItem!.Text = text["Tray_TestNotification"];
        _exitItem!.Text = text["Common_Exit"];
        _icon!.Text = text["App_TrayText"];
    }

    public void Dispose()
    {
        notifications.Published -= Notifications_Published;
        text.LanguageChanged -= Text_LanguageChanged;
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        _menu?.Dispose();
    }
}
