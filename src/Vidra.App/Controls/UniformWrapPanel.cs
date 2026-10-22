using System;
using System.Windows;
using System.Windows.Controls;

namespace Vidra.App.Controls;

/// <summary>
/// CSS "grid-template-columns: repeat(auto-fill, minmax(N, 1fr))" karşılığı:
/// genişliğe sığan kadar eşit sütun açar, öğeleri sütunlara yayar. Öğe Margin'leri boşluk görevi görür.
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(150d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    private int Columns(double width) =>
        double.IsInfinity(width) || width <= 0 ? Math.Max(1, InternalChildren.Count) : Math.Max(1, (int)(width / MinItemWidth));

    protected override Size MeasureOverride(Size available)
    {
        var cols = Columns(available.Width);
        var cellW = double.IsInfinity(available.Width) ? MinItemWidth : available.Width / cols;
        double rowH = 0, total = 0;
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellW, double.PositiveInfinity));
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            if (++i % cols == 0) { total += rowH; rowH = 0; }
        }
        total += rowH;
        var w = double.IsInfinity(available.Width) ? cellW * Math.Min(cols, InternalChildren.Count) : available.Width;
        return new Size(w, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var cols = Columns(final.Width);
        var cellW = final.Width / cols;
        double y = 0, rowH = 0;
        var i = 0;
        var count = InternalChildren.Count;
        while (i < count)
        {
            // Satırdaki en uzun öğeye göre yükseklik: kartlar aynı boyda görünür
            rowH = 0;
            for (var k = i; k < Math.Min(i + cols, count); k++) rowH = Math.Max(rowH, InternalChildren[k].DesiredSize.Height);
            for (var c = 0; c < cols && i < count; c++, i++)
                InternalChildren[i].Arrange(new Rect(c * cellW, y, cellW, rowH));
            y += rowH;
        }
        return final;
    }
}
