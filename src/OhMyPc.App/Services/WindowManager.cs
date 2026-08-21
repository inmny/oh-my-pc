using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace OhMyPc.App.Services;

public sealed class WindowManager(IServiceProvider services)
{
    public bool ExitRequested { get; private set; }

    public void ShowMainWindow()
    {
        var window = services.GetRequiredService<MainWindow>();
        System.Windows.Application.Current.MainWindow = window;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void HideMainWindow() => services.GetRequiredService<MainWindow>().Hide();

    public void Exit()
    {
        ExitRequested = true;
        System.Windows.Application.Current.Shutdown();
    }
}
