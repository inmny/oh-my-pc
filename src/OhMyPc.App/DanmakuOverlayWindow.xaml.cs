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
    private readonly double _targetOpacity;
    private readonly System.Windows.Media.Color _severityColor;
    private TimeSpan _slideSeconds = TimeSpan.FromSeconds(8);

    public DanmakuOverlayWindow(NotificationRecord message, AppSettings settings, int lane)
    {
        _settings = settings;
        _lane = lane;
        _targetOpacity = Math.Clamp(settings.DanmakuOpacity, 0.2, 1);
        InitializeComponent();
        Opacity = 0; // 淡入动画的起点

        var bodyFontSize = Math.Clamp(settings.DanmakuFontSize, 12, 36);
        var titleFontSize = Math.Max(12, bodyFontSize * 0.72);
        _severityColor = message.Severity switch
        {
            NotificationSeverity.Critical => System.Windows.Media.Color.FromRgb(240, 106, 106),
            NotificationSeverity.Warning => System.Windows.Media.Color.FromRgb(240, 179, 91),
            _ => System.Windows.Media.Color.FromRgb(83, 200, 146)
        };

        TitleText.Text = message.Title;
        BodyText.Text = message.Body;
        TitleText.FontSize = titleFontSize;
        BodyText.FontSize = bodyFontSize;
        IconText.FontSize = bodyFontSize * 0.9;
        // 严重/警告标题着色；信息标题用主题正文色
        TitleText.Foreground = message.Severity == NotificationSeverity.Info
            ? (System.Windows.Media.Brush)TryFindResource("TextBrush")!
            : new SolidColorBrush(_severityColor);
        IconText.Foreground = new SolidColorBrush(_severityColor);
        IconText.Text = message.Severity switch
        {
            NotificationSeverity.Critical => "\uE783", // 错误
            NotificationSeverity.Warning => "\uE7BA", // 警告
            _ => "\uE946" // 信息
        };
        SeverityGradientTop.Color = _severityColor;
        SeverityGradientBottom.Color = System.Windows.Media.Color.FromArgb(0, _severityColor.R, _severityColor.G, _severityColor.B);

        // 来源徽章：应用自身来源不展示，避免噪音
        if (string.IsNullOrWhiteSpace(message.Source) || message.Source == "oh-my-pc")
        {
            SourceBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            SourceText.Text = message.Source;
        }

        SourceInitialized += (_, _) =>
        {
            NativeWindowPlacement.MakeClickThrough(this);
            NativeWindowPlacement.FillCursorMonitor(this);
        };
        Loaded += (_, _) => BeginMotion();
    }

    private void BeginMotion()
    {
        var laneHeight = Math.Max(62, BodyText.FontSize * 2.6);
        Canvas.SetTop(MessagePanel, 56 + _lane * laneHeight);
        MessagePanel.Measure(new System.Windows.Size(720, double.PositiveInfinity));
        var distance = ActualWidth + MessagePanel.DesiredSize.Width;
        var speed = Math.Clamp(_settings.DanmakuSpeed, 60, 600);
        var maximumDuration = Math.Clamp(_settings.DanmakuDurationSeconds, 3, 30);
        _slideSeconds = TimeSpan.FromSeconds(Math.Min(maximumDuration, distance / speed));

        // 恒速滑行（弹幕特性，无缓动）
        var slide = new DoubleAnimation
        {
            From = ActualWidth,
            To = -MessagePanel.DesiredSize.Width,
            Duration = _slideSeconds
        };
        slide.Completed += (_, _) => Close();
        Motion.BeginAnimation(TranslateTransform.XProperty, slide);

        // 透明度时间线：200ms 淡入 → 保持 → 离场前 300ms 淡出
        var holdUntil = TimeSpan.FromSeconds(Math.Max(0, _slideSeconds.TotalSeconds - 0.3));
        var fade = new DoubleAnimationUsingKeyFrames { Duration = _slideSeconds };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(_targetOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(_targetOpacity, KeyTime.FromTimeSpan(holdUntil)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(_slideSeconds)));
        BeginAnimation(OpacityProperty, fade);
    }

}
