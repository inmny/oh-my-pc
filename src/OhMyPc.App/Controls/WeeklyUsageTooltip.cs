using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using OhMyPc.Core;
using OhMyPc.App.Services;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OhMyPc.App.Controls;

/// <summary>
/// 每周用量图表的自定义悬浮框：以「周范围 / 总量与环比 / 费用 / 消息 / 峰值 / 最低 / 日均」两列网格呈现。
/// 不依赖 LiveCharts 的 tooltip 管线：监听图表 MouseMove，按鼠标 X 位置换算周索引查 WeeklyUsageSummary，
/// 经 WPF Adorner 显示在图表顶缘下方居右。
/// </summary>
public sealed class WeeklyUsageTooltip
{
    /// <summary>与 WeeklyChartDrawMargin(82, Auto, 28, Auto) 对应的绘图区水平边距。</summary>
    private const double LeftMargin = 82;
    private const double RightMargin = 28;
    private const int SlotCount = 53;

    private readonly ITextLocalizer _text;
    private readonly bool _compact;
    private readonly Grid _grid = new();
    private readonly Border _border;
    private ToolTipAdorner? _adorner;
    private FrameworkElement? _chart;
    private int _activeIndex = -1;

    public WeeklyUsageTooltip(ITextLocalizer text, bool compact)
    {
        _text = text;
        _compact = compact;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x2C, 0x27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Child = _grid,
            IsHitTestVisible = false
        };
    }

    /// <summary>索引 → 摘要，与图表 X 轴索引一致，由 ViewModel 注入。</summary>
    public IReadOnlyDictionary<int, WeeklyUsageSummary> Summaries { get; set; } =
        new Dictionary<int, WeeklyUsageSummary>();

    /// <summary>绑定到图表：订阅 MouseMove，鼠标离开时隐藏。</summary>
    public void Attach(FrameworkElement chart)
    {
        _chart = chart;
        chart.MouseMove += OnChartMouseMove;
        chart.MouseLeave += (_, _) => Hide();
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_chart is null || Summaries.Count == 0)
        {
            Hide();
            return;
        }
        var position = e.GetPosition(_chart);
        var plotWidth = _chart.ActualWidth - LeftMargin - RightMargin;
        if (plotWidth <= 0 || position.X < LeftMargin || position.X > _chart.ActualWidth - RightMargin)
        {
            Hide();
            return;
        }

        // X 轴 53 个周槽位均分绘图区宽度
        var index = Math.Clamp((int)((position.X - LeftMargin) / plotWidth * SlotCount), 0, SlotCount - 1);
        if (!Summaries.TryGetValue(index, out var summary) || summary.TotalTokens <= 0)
        {
            Hide();
            return;
        }
        if (index == _activeIndex && _adorner is { Visibility: Visibility.Visible }) return;
        _activeIndex = index;

        BuildContent(summary);
        if (_adorner is null)
        {
            _adorner = new ToolTipAdorner(_chart, _border);
            _adorner.Attach();
        }
        // 顶部中心对齐选中周槽位中心，并限制在绘图区左右边界内
        var slotCenter = LeftMargin + (index + 0.5) * plotWidth / SlotCount;
        _adorner.UpdateAnchor(slotCenter, LeftMargin, _chart.ActualWidth - RightMargin);
        _adorner.Invalidate();
        _adorner.Visibility = Visibility.Visible;
    }

    private void Hide()
    {
        _activeIndex = -1;
        _adorner?.Visibility = Visibility.Collapsed;
    }

    private void BuildContent(WeeklyUsageSummary summary)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        var row = 0;
        AddTitle(row++, summary);
        AddRow(row++, "Overview_TooltipTotal", Tokens(summary.TotalTokens), TrendText(summary));
        AddRow(row++, "Overview_TooltipCost", summary.CostUsd.ToString("$#,##0.00"));
        AddRow(row++, "Overview_TooltipMessages", summary.MessageCount.ToString("N0"));
        if (_compact)
        {
            if (summary.ActiveDays > 0)
            {
                AddRow(row, "Overview_TooltipDailyAverage", Tokens(summary.DailyAverage));
            }
            return;
        }
        AddRow(row++, "Overview_TooltipPeak", PeakText(summary));
        AddRow(row++, "Overview_TooltipTrough", TroughText(summary));
        AddRow(row, "Overview_TooltipDailyAverage", DailyAverageText(summary));
    }

    private void AddTitle(int row, WeeklyUsageSummary summary)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = _text.Format("Overview_TooltipWeekRange", summary.WeekRangeText),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xE9)),
            Margin = new Thickness(0, 0, 0, 5)
        };
        Grid.SetColumnSpan(title, 2);
        Grid.SetRow(title, row);
        _grid.Children.Add(title);
    }

    private void AddRow(int row, string labelKey, string value, string? trend = null)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Text = _text[labelKey],
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xA1)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 16, 3)
        };
        Grid.SetRow(label, row);
        _grid.Children.Add(label);

        var valuePanel = new StackPanel { Orientation = Orientation.Horizontal };
        valuePanel.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xE9)),
            Margin = new Thickness(0, 0, 6, 3)
        });
        if (trend is { Length: > 0 })
        {
            valuePanel.Children.Add(new TextBlock
            {
                Text = trend,
                Foreground = TrendBrush(trend),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 1, 0, 3)
            });
        }
        Grid.SetRow(valuePanel, row);
        Grid.SetColumn(valuePanel, 1);
        _grid.Children.Add(valuePanel);
    }

    private string? TrendText(WeeklyUsageSummary summary) => summary.TrendPercent switch
    {
        null => null,
        var trend when trend > 0 => TrendUpText(trend.Value),
        var trend when trend < 0 => TrendDownText(Math.Abs(trend.Value)),
        _ => "±0%"
    };

    public static string TrendUpText(double percent) => $"↑ {percent:0}%";

    public static string TrendDownText(double percent) => $"↓ {percent:0}%";

    // 环比配色与 K 线一致：涨红跌绿
    private static Brush TrendBrush(string trend) => new SolidColorBrush(
        trend.StartsWith('↑') ? Color.FromRgb(0xF0, 0x6A, 0x6A) :
        trend.StartsWith('↓') ? Color.FromRgb(0x53, 0xC8, 0x92) :
        Color.FromRgb(0x9A, 0xA8, 0xA1));

    private static string PeakText(WeeklyUsageSummary summary) => summary.PeakDate is null
        ? "-"
        : $"{summary.PeakDate:MM-dd} · {Tokens(summary.PeakTokens)}";

    private static string TroughText(WeeklyUsageSummary summary) => summary.TroughDate is null
        ? "-"
        : $"{summary.TroughDate:MM-dd} · {Tokens(summary.TroughTokens)}";

    private string DailyAverageText(WeeklyUsageSummary summary) =>
        _text.Format("Overview_TooltipActiveDays", Tokens(summary.DailyAverage), summary.ActiveDays);

    /// <summary>令牌数的展示缩写（万/亿），热力图悬浮框共用。</summary>
    internal static string Tokens(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000.0:0.##} 亿",
        >= 10_000 => $"{value / 10_000.0:0.##} 万",
        _ => value.ToString("N0")
    };

    /// <summary>把 tooltip Border 放进图表宿主的装饰层：顶部中心锚定选中周槽位中心，左右限制在绘图区内。</summary>
    private sealed class ToolTipAdorner : Adorner
    {
        private readonly Border _content;
        private readonly VisualCollection _visuals;
        private double _anchorX;
        private double _minX;
        private double _maxX;

        public ToolTipAdorner(FrameworkElement adornedChart, Border content) : base(adornedChart)
        {
            _content = content;
            _visuals = new VisualCollection(this) { content };
            Visibility = Visibility.Collapsed;
        }

        public void UpdateAnchor(double slotCenterX, double plotLeft, double plotRight)
        {
            _anchorX = slotCenterX;
            _minX = plotLeft;
            _maxX = plotRight;
        }

        public void Attach()
        {
            var layer = AdornerLayer.GetAdornerLayer(AdornedElement)
                ?? throw new InvalidOperationException("图表宿主没有可用的装饰层。");
            if (layer.GetAdorners(this) is not { Length: > 0 })
            {
                layer.Add(this);
            }
        }

        public void Invalidate()
        {
            _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            InvalidateVisual();
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _content.Measure(constraint);
            return new Size(AdornedElement.RenderSize.Width, AdornedElement.RenderSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // 悬浮在图表下方：顶部中心对齐选中周槽位中心（即该周日期的正下方），
            // 左右不超出绘图区边界。Adorner 默认不裁剪，超出图表高度的部分正常渲染。
            var half = _content.DesiredSize.Width / 2;
            var x = Math.Clamp(_anchorX - half, _minX, Math.Max(_minX, _maxX - _content.DesiredSize.Width));
            var y = AdornedElement.RenderSize.Height + 4;
            _content.Arrange(new Rect(new Point(x, y), _content.DesiredSize));
            return finalSize;
        }
    }
}
