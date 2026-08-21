using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OhMyPc.App.Services;
using OhMyPc.Core.Domain;

namespace OhMyPc.App;

public partial class DanmakuOverlayWindow : Window
{
    private readonly AppSettings _settings;
    private readonly int _lane;

    public DanmakuOverlayWindow(NotificationRecord message, AppSettings settings, int lane)
    {
        _settings = settings;
        _lane = lane;
        InitializeComponent();
        TitleText.Text = message.Title;
        BodyText.Text = message.Body;
        SeverityBar.Background = new SolidColorBrush(message.Severity switch
        {
            NotificationSeverity.Critical => System.Windows.Media.Color.FromRgb(240, 106, 106),
            NotificationSeverity.Warning => System.Windows.Media.Color.FromRgb(240, 179, 91),
            _ => System.Windows.Media.Color.FromRgb(83, 200, 146)
        });
        Opacity = Math.Clamp(settings.DanmakuOpacity, 0.2, 1);
        TitleText.FontSize = Math.Clamp(settings.DanmakuFontSize, 12, 36);
        BodyText.FontSize = Math.Clamp(settings.DanmakuFontSize, 12, 36);
        SourceInitialized += (_, _) =>
        {
            NativeWindowPlacement.MakeClickThrough(this);
            NativeWindowPlacement.FillCursorMonitor(this);
        };
        Loaded += (_, _) => BeginMotion();
    }

    private void BeginMotion()
    {
        Canvas.SetTop(MessagePanel, 56 + _lane * 62);
        MessagePanel.Measure(new System.Windows.Size(720, double.PositiveInfinity));
        var distance = ActualWidth + MessagePanel.DesiredSize.Width;
        var speed = Math.Clamp(_settings.DanmakuSpeed, 60, 600);
        var maximumDuration = Math.Clamp(_settings.DanmakuDurationSeconds, 3, 30);
        var seconds = Math.Min(maximumDuration, distance / speed);
        var animation = new DoubleAnimation
        {
            From = ActualWidth,
            To = -MessagePanel.DesiredSize.Width,
            Duration = TimeSpan.FromSeconds(Math.Max(1, seconds)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) => Close();
        Motion.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
