using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace OhMyPc.App.Controls;

public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(
        nameof(ColumnWidth),
        typeof(double),
        typeof(MasonryPanel),
        new FrameworkPropertyMetadata(
            290d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double number && double.IsFinite(number) && number > 0);

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(MasonryPanel),
        new FrameworkPropertyMetadata(
            12d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsValidSpacing);

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(MasonryPanel),
        new FrameworkPropertyMetadata(
            12d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsValidSpacing);

    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columnWidth = ResolveColumnWidth(availableSize.Width);
        var columnHeights = new double[GetColumnCount(availableSize.Width, columnWidth)];

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;

            var column = FindShortestColumn(columnHeights);
            if (columnHeights[column] > 0) columnHeights[column] += VerticalSpacing;
            columnHeights[column] += child.DesiredSize.Height;
        }

        var desiredWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : columnHeights.Length * columnWidth + (columnHeights.Length - 1) * HorizontalSpacing;
        return new Size(desiredWidth, columnHeights.Length == 0 ? 0 : columnHeights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = ResolveColumnWidth(finalSize.Width);
        var columnHeights = new double[GetColumnCount(finalSize.Width, columnWidth)];

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            var column = FindShortestColumn(columnHeights);
            var y = columnHeights[column];
            if (y > 0) y += VerticalSpacing;
            child.Arrange(new Rect(
                column * (columnWidth + HorizontalSpacing),
                y,
                columnWidth,
                child.DesiredSize.Height));
            columnHeights[column] = y + child.DesiredSize.Height;
        }

        return finalSize;
    }

    private double ResolveColumnWidth(double availableWidth) =>
        double.IsFinite(availableWidth) && availableWidth > 0
            ? Math.Min(ColumnWidth, availableWidth)
            : ColumnWidth;

    private int GetColumnCount(double availableWidth, double columnWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return 1;
        return Math.Max(1, (int)Math.Floor((availableWidth + HorizontalSpacing) / (columnWidth + HorizontalSpacing)));
    }

    private static int FindShortestColumn(IReadOnlyList<double> heights)
    {
        var shortest = 0;
        for (var index = 1; index < heights.Count; index++)
        {
            if (heights[index] < heights[shortest]) shortest = index;
        }
        return shortest;
    }

    private static bool IsValidSpacing(object value) =>
        value is double number && double.IsFinite(number) && number >= 0;
}
