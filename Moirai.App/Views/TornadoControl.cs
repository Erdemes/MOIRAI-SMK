using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class TornadoControl : Control
{
    public static readonly StyledProperty<SensitivityChartData?> DataProperty =
        AvaloniaProperty.Register<TornadoControl, SensitivityChartData?>(nameof(Data));

    public SensitivityChartData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    static TornadoControl()
    {
        AffectsMeasure<TornadoControl>(DataProperty);
        AffectsRender<TornadoControl>(DataProperty);
    }

    private const double PadL = 172, PadR = 84, PadT = 26, PadB = 26, RowH = 34, BarH = 16;

    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush Better = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Worse = new SolidColorBrush(Color.Parse("#F25C54"));
    private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 1);
    private static readonly IPen BaselinePen = new Pen(new SolidColorBrush(Color.Parse("#E6EAF2")), 1.4,
        new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen WhiskerPen = new Pen(new SolidColorBrush(Color.Parse("#FFBF1F")), 1.4);

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
        double X(double p) => plotL + Math.Clamp(p, 0, 1) * plotW;

        for (int g = 0; g <= 4; g++)
        {
            double p = g / 4.0;
            double x = X(p);
            ctx.DrawLine(GridPen, new Point(x, PadT), new Point(x, h - PadB));
            if (g % 2 == 0)
                DrawText(ctx, $"{p * 100:0}%", new Point(x - 8, h - PadB + 4), 9, TextMuted);
        }
        DrawText(ctx, "P(program) →", new Point(plotL, 6), 10, TextMuted);

        double bx = X(data.Baseline);
        ctx.DrawLine(BaselinePen, new Point(bx, PadT), new Point(bx, h - PadB));
        DrawText(ctx, $"baseline {data.Baseline:P0}", new Point(bx + 4, 6), 9, TextMuted);

        for (int i = 0; i < data.Rows.Count; i++)
        {
            var r = data.Rows[i];
            double yMid = PadT + i * RowH + RowH / 2;
            double yBar = yMid - BarH / 2;

            ctx.FillRectangle(Track, new Rect(plotL, yBar, plotW, BarH), 3);

            double lo = Math.Min(r.LowP, r.HighP), hi = Math.Max(r.LowP, r.HighP);
            double xLo = X(lo), xHi = X(hi);

            double split = Math.Clamp(bx, xLo, xHi);
            if (split > xLo)
                ctx.FillRectangle(Worse, new Rect(xLo, yBar, split - xLo, BarH), 3);
            if (xHi > split)
                ctx.FillRectangle(Better, new Rect(split, yBar, xHi - split, BarH), 3);

            if (r.HasBand)
            {
                double yW = yBar + BarH + 3;
                void Whisker(double a, double b)
                {
                    double xa = X(a), xb = X(b);
                    ctx.DrawLine(WhiskerPen, new Point(xa, yW), new Point(xb, yW));
                    ctx.DrawLine(WhiskerPen, new Point(xa, yW - 3), new Point(xa, yW + 3));
                    ctx.DrawLine(WhiskerPen, new Point(xb, yW - 3), new Point(xb, yW + 3));
                }
                Whisker(r.LowLo, r.LowHi);
                Whisker(r.HighLo, r.HighHi);
            }

            DrawTextRight(ctx, r.Name, plotL - 10, yMid - 7, 12, TextPrimary);

            bool lowIsLeft = r.LowP <= r.HighP;
            DrawTextRight(ctx, lowIsLeft ? r.LowLabel : r.HighLabel, xLo - 6, yMid - 7, 9, TextMuted);
            DrawText(ctx, lowIsLeft ? r.HighLabel : r.LowLabel, new Point(xHi + 6, yMid - 7), 9, TextMuted);

            DrawText(ctx, $"Δ {r.Swing:P0}", new Point(w - PadR + 8, yMid - 7), 10, TextPrimary);
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
