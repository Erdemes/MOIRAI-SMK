using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Views;

public sealed class YieldPreviewControl : Control
{
    public static readonly StyledProperty<Campaign?> CampaignProperty =
        AvaloniaProperty.Register<YieldPreviewControl, Campaign?>(nameof(Campaign));

    public Campaign? Campaign
    {
        get => GetValue(CampaignProperty);
        set => SetValue(CampaignProperty, value);
    }

    static YieldPreviewControl()
    {
        AffectsRender<YieldPreviewControl>(CampaignProperty);
    }

    private const double PadL = 52, PadR = 56, PadT = 40, PadB = 44;

    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 1);
    private static readonly IPen TargetPen = new Pen(new SolidColorBrush(Color.Parse("#22C55E")), 1.4,
        new DashStyle(new double[] { 5, 3 }, 0));
    private static readonly IPen ExpectedPen = new Pen(new SolidColorBrush(Color.Parse("#6366F1")), 2.2);
    private static readonly IPen IdealPen = new Pen(new SolidColorBrush(Color.Parse("#22D3EE")), 1.6,
        new DashStyle(new double[] { 1, 2 }, 0));
    private static readonly IPen SamplePen = new Pen(new SolidColorBrush(Color.Parse("#9698F4")), 1.4);
    private static readonly IPen RetestPen = new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), 1.6,
        new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IBrush AbortBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush TooltipBg = new SolidColorBrush(Color.FromArgb(244, 20, 26, 34));
    private static readonly IPen TooltipBorder = new Pen(new SolidColorBrush(Color.Parse("#3A4453")), 1);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 230, 234, 242)), 1,
        new DashStyle(new double[] { 2, 2 }, 0));

    private readonly List<double> _expected = new();
    private readonly List<double> _ideal = new();
    private readonly List<double> _sample = new();
    private readonly HashSet<int> _abortedSet = new();
    private int _n = 1;
    private int _hoverSortie = -1;
    private Point _hoverPos;

    public override void Render(DrawingContext ctx)
    {
        var c = Campaign;
        double w = Bounds.Width, h = Bounds.Height;
        if (c is null || w < 100 || h < 100) return;

        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        double ttp = Math.Max(1e-9, c.TotalTestPoints);
        double tps = c.TestPointsPerSortie;
        if (tps <= 0)
        {
            DrawText(ctx, "Set test points per sortie > 0 to preview.", new Point(PadL, PadT), 12, TextMuted);
            return;
        }
        double abort = Math.Clamp(c.AbortProbability, 0, 0.95);

        int maxSorties = Math.Clamp(c.RequiredSorties() * 8, 1000, 50000);

        var expected = _expected;
        expected.Clear();
        expected.Add(0);
        double good = 0;
        while (good < ttp && expected.Count <= maxSorties)
        {
            double gain = Math.Min(tps, ttp - good) * (1 - abort) * (1 - c.RetestProbabilityAt(good));
            if (gain <= 1e-9) break;
            good += gain;
            if (ttp - good <= ttp * 0.005) good = ttp;
            expected.Add(good);
        }

        var ideal = _ideal;
        ideal.Clear();
        ideal.Add(0);
        double iGood = 0;
        while (iGood < ttp - 1e-9 && ideal.Count <= maxSorties)
        {
            iGood += Math.Min(tps, ttp - iGood);
            ideal.Add(Math.Min(iGood, ttp));
        }

        var sample = _sample;
        sample.Clear();
        sample.Add(0);
        _abortedSet.Clear();
        var yield = new StochasticYieldModel(new Random(42));
        double sGood = 0;
        while (sGood < ttp - 1e-9 && sample.Count <= maxSorties)
        {
            double attempted = Math.Min(tps, ttp - sGood);
            var outcome = yield.Evaluate(c, attempted, sGood);
            if (outcome.Aborted) _abortedSet.Add(sample.Count);
            sGood += outcome.Aborted ? 0 : outcome.GoodPoints;
            sample.Add(Math.Min(sGood, ttp));
        }
        var aborted = _abortedSet;

        int n = Math.Max(2, Math.Max(expected.Count, sample.Count));
        _n = n;

        double X(double sortie) => PadL + sortie / Math.Max(1, n - 1) * (w - PadL - PadR);
        double YPts(double pts) => PadT + (1 - pts / ttp) * (h - PadT - PadB);
        double YProb(double p) => PadT + (1 - p) * (h - PadT - PadB);

        ctx.DrawLine(GridPen, new Point(PadL, PadT), new Point(PadL, h - PadB));
        ctx.DrawLine(GridPen, new Point(PadL, h - PadB), new Point(w - PadR, h - PadB));
        ctx.DrawLine(GridPen, new Point(w - PadR, PadT), new Point(w - PadR, h - PadB));
        for (int g = 1; g <= 3; g++)
        {
            double y = PadT + g / 4.0 * (h - PadT - PadB);
            ctx.DrawLine(GridPen, new Point(PadL, y), new Point(w - PadR, y));
        }

        DrawText(ctx, ttp.ToString("0.#"), new Point(8, YPts(ttp) - 7), 10, TextMuted);
        DrawText(ctx, "0", new Point(8, YPts(0) - 7), 10, TextMuted);
        DrawText(ctx, "CL1 Test points", new Point(8, PadT - 22), 10, TextMuted);
        DrawText(ctx, "100%", new Point(w - PadR + 6, YProb(1) - 7), 10, TextMuted);
        DrawText(ctx, "0%", new Point(w - PadR + 6, YProb(0) - 7), 10, TextMuted);
        DrawText(ctx, "retest", new Point(w - PadR + 6, PadT - 22), 10, TextMuted);
        DrawText(ctx, "sorties →", new Point(w / 2 - 24, h - PadB + 18), 10, TextMuted);
        for (int s = 0; s < n; s += Math.Max(1, n / 8))
            DrawText(ctx, s.ToString(), new Point(X(s) - 4, h - PadB + 4), 9, TextMuted);

        ctx.DrawLine(TargetPen, new Point(PadL, YPts(ttp)), new Point(w - PadR, YPts(ttp)));

        for (int i = 1; i < expected.Count; i++)
            ctx.DrawLine(RetestPen,
                new Point(X(i - 1), YProb(c.RetestProbabilityAt(expected[i - 1]))),
                new Point(X(i), YProb(c.RetestProbabilityAt(expected[i]))));

        for (int i = 1; i < sample.Count; i++)
            ctx.DrawLine(SamplePen,
                new Point(X(i - 1), YPts(sample[i - 1])),
                new Point(X(i), YPts(sample[i])));
        foreach (int s in aborted)
        {
            double x = X(s), y = h - PadB - 7;
            ctx.DrawLine(new Pen(AbortBrush, 1.6), new Point(x - 4, y - 4), new Point(x + 4, y + 4));
            ctx.DrawLine(new Pen(AbortBrush, 1.6), new Point(x - 4, y + 4), new Point(x + 4, y - 4));
        }

        for (int i = 1; i < ideal.Count; i++)
            ctx.DrawLine(IdealPen,
                new Point(X(i - 1), YPts(ideal[i - 1])),
                new Point(X(i), YPts(ideal[i])));

        for (int i = 1; i < expected.Count; i++)
            ctx.DrawLine(ExpectedPen,
                new Point(X(i - 1), YPts(expected[i - 1])),
                new Point(X(i), YPts(expected[i])));

        double lx = PadL, ly = 10;
        lx = LegendItem(ctx, lx, ly, IdealPen, "ideal (efficiency off)");
        lx = LegendItem(ctx, lx, ly, ExpectedPen, "expected CL1 Test points");
        lx = LegendItem(ctx, lx, ly, SamplePen, "sample run");
        lx = LegendItem(ctx, lx, ly, RetestPen, "retest probability");
        lx = LegendItem(ctx, lx, ly, TargetPen, "planned points");
        ctx.DrawLine(new Pen(AbortBrush, 1.6), new Point(lx + 2, ly + 3), new Point(lx + 10, ly + 11));
        ctx.DrawLine(new Pen(AbortBrush, 1.6), new Point(lx + 2, ly + 11), new Point(lx + 10, ly + 3));
        DrawText(ctx, $"aborted sortie ({aborted.Count} in sample)", new Point(lx + 14, ly), 10, TextMuted);

        if (_hoverSortie >= 0 && _hoverSortie < n)
        {
            int s = _hoverSortie;
            double gx = X(s);
            ctx.DrawLine(HoverPen, new Point(gx, PadT), new Point(gx, h - PadB));

            var lines = new List<string> { $"Sortie {s}" };
            lines.Add(s < ideal.Count
                ? $"Ideal good: {ideal[s]:0.##} / {ttp:0.##}"
                : $"Ideal good: {ttp:0.##} (done)");
            if (s < expected.Count)
            {
                lines.Add($"Expected good: {expected[s]:0.##} / {ttp:0.##}");
                lines.Add($"Retest prob.: {c.RetestProbabilityAt(expected[s]):P0}");
            }
            else
            {
                lines.Add($"Expected good: {ttp:0.##} (done)");
            }
            if (s < sample.Count)
            {
                string mark = aborted.Contains(s) ? "  ✕ aborted" : "";
                lines.Add($"Sample good: {sample[s]:0.##} / {ttp:0.##}{mark}");
            }
            else
            {
                lines.Add($"Sample good: {ttp:0.##} (done)");
            }

            const double pad = 8, fontSize = 11, lineH = 15.5;
            double tw = lines.Max(l => l.Length) * fontSize * 0.55 + pad * 2;
            double th = lines.Count * lineH + pad * 2;
            double tx = _hoverPos.X + 14, ty = _hoverPos.Y + 14;
            if (tx + tw > w) tx = _hoverPos.X - tw - 8;
            if (ty + th > h) ty = h - th - 2;
            if (tx < 0) tx = 2;
            if (ty < 0) ty = 2;

            ctx.DrawRectangle(TooltipBg, TooltipBorder, new Rect(tx, ty, tw, th), 5, 5);
            for (int i = 0; i < lines.Count; i++)
                DrawText(ctx, lines[i], new Point(tx + pad, ty + pad + i * lineH), fontSize,
                    i == 0 ? TextPrimary : TextMuted);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        double w = Bounds.Width, h = Bounds.Height;
        int sortie = -1;
        if (pt.X >= PadL && pt.X <= w - PadR && pt.Y >= PadT && pt.Y <= h - PadB && _n > 1)
            sortie = Math.Clamp(
                (int)Math.Round((pt.X - PadL) / (w - PadL - PadR) * (_n - 1)), 0, _n - 1);

        if (sortie != _hoverSortie || (sortie >= 0 && pt != _hoverPos))
        {
            _hoverSortie = sortie;
            _hoverPos = pt;
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverSortie >= 0)
        {
            _hoverSortie = -1;
            InvalidateVisual();
        }
        base.OnPointerExited(e);
    }

    private static double LegendItem(DrawingContext ctx, double x, double y, IPen pen, string label)
    {
        ctx.DrawLine(pen, new Point(x, y + 7), new Point(x + 18, y + 7));
        DrawText(ctx, label, new Point(x + 22, y), 10, TextMuted);
        return x + 22 + label.Length * 5.6 + 16;
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, double size, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        ctx.DrawText(ft, at);
    }
}
