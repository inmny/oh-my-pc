using System.ComponentModel;
using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;

namespace OhMyPc.App;

public partial class MainWindow : Window
{
    private readonly WindowManager _windows;

    public MainWindow(MainViewModel viewModel, WindowManager windows)
    {
        _windows = windows;
        InitializeComponent();
        DataContext = viewModel;
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
}
