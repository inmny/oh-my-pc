using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;

namespace OhMyPc.App;

public partial class TrayPopupWindow : Window
{
    private readonly WindowManager _windows;
    private bool _isRepositioning;

    public TrayPopupWindow(MainViewModel viewModel, WindowManager windows)
    {
        _windows = windows;
        InitializeComponent();
        DataContext = viewModel;
        Deactivated += (_, _) => Hide();
        SizeChanged += TrayPopupWindow_SizeChanged;
    }

    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        Reposition();
        Activate();
    }

    private void TrayPopupWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible) Reposition();
    }

    private void Reposition()
    {
        if (_isRepositioning) return;
        _isRepositioning = true;
        try
        {
            NativeWindowPlacement.PlaceAtCursorWorkArea(this);
        }
        finally
        {
            _isRepositioning = false;
        }
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _windows.ShowMainWindow();
    }
}
