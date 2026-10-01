using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using QuotaTray.Core;

namespace QuotaTray.App;

/// <summary>
/// Draws the tray icon: a ring whose filled arc is the remaining headroom, coloured by severity,
/// with the percentage in the middle. Rendered at 64 px and scaled down by the OS.
/// </summary>
public static class GaugeIconRenderer
{
    private const int Size = 64;
    private const double Stroke = 9;

    private static readonly Color Normal = Color.Parse("#3B82F6");
    private static readonly Color Warning = Color.Parse("#F59E0B");
    private static readonly Color Critical = Color.Parse("#EF4444");
    private static readonly Color ErrorColor = Color.Parse("#9CA3AF");
    private static readonly Color Track = Color.FromArgb(70, 128, 128, 128);

    private static readonly Dictionary<(int? Percent, Severity Severity), WindowIcon> Cache = [];

    /// <summary>
    /// Cached per (rounded percent, severity). Icons are never discarded: the macOS backend releases
    /// the wrapped NSImage from the finalizer thread, and churning icons every poll crashed AppKit.
    /// </summary>
    public static WindowIcon Get(double? remainingPercent, Severity severity)
    {
        var key = (remainingPercent is { } p ? (int?)Math.Round(Math.Clamp(p, 0, 100)) : null, severity);
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var icon))
            {
                icon = Render(key.Item1, severity);
                Cache[key] = icon;
            }

            return icon;
        }
    }

    public static WindowIcon Render(double? remainingPercent, Severity severity)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            Draw(ctx, remainingPercent, severity);
        }

        var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    private static void Draw(DrawingContext ctx, double? remainingPercent, Severity severity)
    {
        var centre = new Point(Size / 2d, Size / 2d);
        var radius = Size / 2d - Stroke / 2d - 1;
        var colour = severity switch
        {
            Severity.Critical => Critical,
            Severity.Warning => Warning,
            Severity.Error => ErrorColor,
            _ => Normal,
        };

        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Track), Stroke), centre, radius, radius);

        var fraction = remainingPercent is { } p ? Math.Clamp(p, 0, 100) / 100d : 0;
        if (fraction > 0)
        {
            var arc = BuildArc(centre, radius, fraction);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(colour), Stroke) { LineCap = PenLineCap.Round }, arc);
        }

        var label = severity == Severity.Error && remainingPercent is null
            ? "!"
            : remainingPercent is { } r ? Math.Round(r).ToString("0", CultureInfo.InvariantCulture) : "…";

        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
            label.Length >= 3 ? 20 : 26,
            new SolidColorBrush(colour));
        ctx.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    /// <summary>Arc from 12 o'clock, clockwise, covering <paramref name="fraction"/> of the circle.</summary>
    private static StreamGeometry BuildArc(Point centre, double radius, double fraction)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        var start = PointAt(centre, radius, 0);
        g.BeginFigure(start, isFilled: false);

        if (fraction >= 0.999)
        {
            // Two half arcs; a single 360° ArcTo degenerates to nothing.
            g.ArcTo(PointAt(centre, radius, 0.5), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.ArcTo(start, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        }
        else
        {
            g.ArcTo(PointAt(centre, radius, fraction), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise);
        }

        g.EndFigure(false);
        return geometry;
    }

    private static Point PointAt(Point centre, double radius, double fraction)
    {
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        return new Point(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
    }
}
