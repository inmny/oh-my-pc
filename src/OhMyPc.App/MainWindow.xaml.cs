using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;

namespace OhMyPc.App;

public partial class MainWindow : Window
{
    private readonly WindowManager _windows;
    private readonly ThemeService _themes;

    public MainWindow(MainViewModel viewModel, WindowManager windows, ThemeService themes)
    {
        _windows = windows;
        _themes = themes;
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
        _themes.ThemeChanged += (_, _) => ApplyTitleBarTheme();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_windows.ExitRequested)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _windows.Exit();

    /// <summary>浅色主题下把系统标题栏切成浅色形态，避免浅色界面配深色标题栏。</summary>
    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = _themes.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
