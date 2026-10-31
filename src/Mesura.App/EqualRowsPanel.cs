using System.Windows;
using System.Windows.Controls;

namespace Mesura.App;

/// <summary>
/// A grid of cards that adapts to the room: as many columns as fit at <see cref="MinColumnWidth"/> (at most <see cref="MaxColumns"/>), every column the
/// same width, and every card as tall as the tallest card of its own row. A card with a long name or a long description makes its row taller; its
/// neighbours in that row stretch to match, so a row always reads as one line of equal cards, and nothing is cut to make that happen. (A UniformGrid
/// makes every cell as tall as the tallest card of the whole group, which is wasteful with a few long texts, and a WrapPanel lets neighbours differ.)
/// </summary>
public sealed class EqualRowsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(EqualRowsPanel), new FrameworkPropertyMetadata(340d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(EqualRowsPanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a column may get before a column is dropped.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    private double[] _rowHeights = [];
    private int _columns = 1;

    private int ColumnsFor(double width) =>
        double.IsInfinity(width) ? MaxColumns : Math.Clamp((int)Math.Floor(width / Math.Max(1, MinColumnWidth)), 1, Math.Max(1, MaxColumns));

    protected override Size MeasureOverride(Size available)
    {
        _columns = ColumnsFor(available.Width);
        var cell = double.IsInfinity(available.Width) ? MinColumnWidth : available.Width / _columns;
        var rows = (InternalChildren.Count + _columns - 1) / _columns;
        _rowHeights = new double[rows];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(cell, double.PositiveInfinity));
            _rowHeights[i / _columns] = Math.Max(_rowHeights[i / _columns], child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(available.Width) ? cell * _columns : available.Width, _rowHeights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cell = finalSize.Width / _columns;
        var y = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var row = i / _columns;
            if (row >= _rowHeights.Length)
            {
                break;
            }

            if (i % _columns == 0 && i > 0)
            {
                y += _rowHeights[row - 1];
            }

            InternalChildren[i].Arrange(new Rect((i % _columns) * cell, y, cell, _rowHeights[row]));
        }

        return finalSize;
    }
}
