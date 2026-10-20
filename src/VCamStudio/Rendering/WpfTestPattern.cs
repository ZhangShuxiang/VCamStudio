using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VCamStudio.Rendering;

internal static class WpfTestPattern
{
    private static readonly Brush Background = CreateBackground();
    private static readonly Brush White = Freeze(new SolidColorBrush(Color.FromRgb(241, 245, 249)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromArgb(190, 203, 213, 225)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(56, 189, 248)));
    private static readonly Brush Glow = Freeze(new SolidColorBrush(Color.FromArgb(26, 96, 165, 250)));
    private static readonly Brush Grid = Freeze(new SolidColorBrush(Color.FromArgb(24, 148, 163, 184)));
    private static readonly Brush CenterDot = Freeze(new SolidColorBrush(Color.FromArgb(110, 14, 165, 233)));

    private static readonly Pen GridPen = Freeze(new Pen(Grid, 1));
    private static readonly Pen OuterRingPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(45, 125, 211, 252)), 2));
    private static readonly Pen InnerRingPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(80, 125, 211, 252)), 2));
    private static readonly Brush Sweep = CreateSweep();

    private static readonly FontFamily UiFont = new("Segoe UI");
    private static readonly Typeface RegularTypeface =
        new(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface =
        new(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface BoldTypeface =
        new(UiFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static void Draw(DrawingContext dc, int width, int height, string label, TimeSpan time)
    {
        var rect = new Rect(0, 0, width, height);
        dc.DrawRectangle(Background, null, rect);

        double step = Math.Max(24, width / 16.0);
        for (double x = step; x < width; x += step)
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
        for (double y = step; y < height; y += step)
            dc.DrawLine(GridPen, new Point(0, y), new Point(width, y));

        double radius = Math.Min(width, height) * 0.32;
        var center = new Point(width * 0.72, height * 0.48);
        dc.DrawEllipse(Glow, null, center, radius, radius);
        dc.DrawEllipse(null, OuterRingPen, center, radius * 0.72, radius * 0.72);
        dc.DrawEllipse(null, InnerRingPen, center, radius * 0.46, radius * 0.46);
        dc.DrawEllipse(CenterDot, null, center, radius * 0.22, radius * 0.22);

        double fontUnit = Math.Max(10, Math.Min(width, height) / 36.0);
        DrawText(dc, "VCAM STUDIO", width * 0.07, height * 0.10, fontUnit * 1.6, White, FontWeights.Bold);
        DrawText(dc, label, width * 0.07, height * 0.10 + fontUnit * 2.4, fontUnit * 0.92, Muted, FontWeights.Normal);
        DrawText(dc, "MEDIA FOUNDATION VIRTUAL CAMERA", width * 0.07, height * 0.76, fontUnit * 0.62, Accent, FontWeights.SemiBold);
        DrawText(dc, time.ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture), width * 0.07, height * 0.84, fontUnit * 0.62, Muted, FontWeights.Normal);

        double sweep = (time.TotalSeconds * 0.45) % 1.0;
        double lineX = width * sweep;
        dc.DrawRectangle(Sweep, null, new Rect(lineX, 0, Math.Max(2, width * 0.06), height));
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            weight == FontWeights.Bold ? BoldTypeface
                : weight == FontWeights.SemiBold ? SemiBoldTypeface
                : RegularTypeface,
            Math.Max(8, size),
            brush,
            1.0);

        formatted.MaxTextWidth = 10000;
        dc.DrawText(formatted, new Point(x, y));
    }

    private static Brush CreateBackground()
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(7, 12, 25),
            Color.FromRgb(15, 33, 66),
            new Point(0, 0),
            new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateSweep()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(0, 56, 189, 248),
            Color.FromArgb(100, 56, 189, 248),
            new Point(0, 0),
            new Point(1, 0));
        brush.Freeze();
        return brush;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
