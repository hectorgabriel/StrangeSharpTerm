using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// A line of numbers, drawn small.
///
/// The Swift dashboard draws the load average this way, and it is the one place
/// in this app where a shape says something a number cannot: whether the last
/// few minutes have been going up.
///
/// It draws what it has been given and nothing else — no axes, no labels, no
/// scale. A sparkline that needed explaining would be a chart.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.5);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, StrokeThicknessProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        // One point is not a line. Two are, and that is the first refresh worth
        // drawing: before then the card shows the numbers alone.
        if (Values is not { Count: > 1 } values || Stroke is not { } stroke)
            return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        // Scaled to what it holds, from zero: load is a number with no ceiling,
        // and a line that rescaled its own floor would make a quiet server look
        // busy.
        var ceiling = Math.Max(values.Max(), 0.0001);
        var step = width / (values.Count - 1);

        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var point = new Point(index * step, height - Math.Clamp(values[index] / ceiling, 0, 1) * height);
                if (index == 0)
                    draw.BeginFigure(point, false);
                else
                    draw.LineTo(point);
            }
            draw.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
