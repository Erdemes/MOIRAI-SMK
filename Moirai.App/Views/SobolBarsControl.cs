using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class SobolBarsControl : Control
{
    public static readonly StyledProperty<SobolChartData?> DataProperty =
        AvaloniaProperty.Register<SobolBarsControl, SobolChartData?>(nameof(Data));

    public SobolChartData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    static SobolBarsControl()
    {
        AffectsMeasure<SobolBarsControl>(DataProperty);
        AffectsRender<SobolBarsControl>(DataProperty);
    }

    private const double PadL = 172, PadR = 150, PadT = 26, PadB = 26, RowH = 34, BarH = 16;

    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush Direct = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush Interact = new SolidColorBrush(Color.FromArgb(90, 76, 141, 255));
    private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 1);

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Data?.Rows.Count ?? 0;
        double w = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        return new Size(w, PadT + PadB + Math.Max(1, rows) * RowH);
    }

    public override void Render(DrawingContext ctx)
    {
        var data = Data;
        double w = Bounds.Width, h = Bounds.Height;
        if (data is null || data.Rows.Count == 0 || w < 120) return;

        double plotL = PadL, plotR = w - PadR, plotW = plotR - plotL;
        double X(double share) => plotL + Math.Clamp(share, 0, 1) * plotW;

        for (int g = 0; g <= 4; g++)
        {
            double p = g / 4.0;
            double x = X(p);
            ctx.DrawLine(GridPen, new Point(x, PadT), new Point(x, h - PadB));
            if (g % 2 == 0)
                DrawText(ctx, $"{p * 100:0}%", new Point(x - 8, h - PadB + 4), 9, TextMuted);
        }
        DrawText(ctx, "Share of P(program) variation →", new Point(plotL, 6), 10, TextMuted);

        for (int i = 0; i < data.Rows.Count; i++)
        {
            var r = data.Rows[i];
            double yMid = PadT + i * RowH + RowH / 2;
            double yBar = yMid - BarH / 2;

            ctx.FillRectangle(Track, new Rect(plotL, yBar, plotW, BarH), 3);

            double xDirect = X(r.DirectShare), xTotal = X(r.TotalShare);
            if (xTotal > xDirect)
                ctx.FillRectangle(Interact, new Rect(xDirect, yBar, xTotal - xDirect, BarH), 3);
            if (xDirect > plotL)
                ctx.FillRectangle(Direct, new Rect(plotL, yBar, xDirect - plotL, BarH), 3);

            DrawTextRight(ctx, r.Name, plotL - 10, yMid - 7, 12, TextPrimary);

            DrawText(ctx, $"{r.DirectShare:P0} alone · {r.TotalShare:P0} total",
                new Point(plotR + 8, yMid - 7), 9, TextPrimary);
        }
    }

    private static void DrawTextRight(DrawingContext ctx, string text, double right, double y, double size, IBrush brush)
    {
        var ft = Format(text, size, brush);
        ctx.DrawText(ft, new Point(right - ft.Width, y));
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, double size, IBrush brush) =>
        ctx.DrawText(Format(text, size, brush), at);

    private static FormattedText Format(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
}