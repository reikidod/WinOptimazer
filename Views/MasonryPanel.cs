using System.Windows;
using System.Windows.Controls;

namespace WinOptimizer.Views;

/// <summary>
/// Раскладка «кирпичиками»: число колонок подбирается по ширине, колонки занимают всю ширину,
/// а каждая следующая карточка попадает в самую короткую колонку — без пустых провалов, как в строках WrapPanel.
/// </summary>
public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(340d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    internal static int ColumnCount(double width, double minColumnWidth) =>
        width <= 0 || minColumnWidth <= 0 ? 1 : Math.Max(1, (int)Math.Floor(width / minColumnWidth));

    internal static int ShortestColumn(double[] heights)
    {
        var best = 0;
        for (var i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[best])
            {
                best = i;
            }
        }

        return best;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        var columns = ColumnCount(width, MinColumnWidth);
        var columnWidth = Math.Floor(width / columns);
        var heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            heights[ShortestColumn(heights)] += child.DesiredSize.Height;
        }

        return new Size(width, heights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width, MinColumnWidth);
        var columnWidth = Math.Floor(finalSize.Width / columns);
        var heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            var column = ShortestColumn(heights);
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(column * columnWidth, heights[column], columnWidth, height));
            heights[column] += height;
        }

        return finalSize;
    }
}
