using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class TestPointsChartControl : Control
{
    public static readonly StyledProperty<GanttData?> DataProperty =
        AvaloniaProperty.Register<TestPointsChartControl, GanttData?>(nameof(Data));

    public GanttData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly StyledProperty<bool> HideEmptyDaysProperty =
        AvaloniaProperty.Register<TestPointsChartControl, bool>(nameof(HideEmptyDays));

    public bool HideEmptyDays
    {
        get => GetValue(HideEmptyDaysProperty);
        set => SetValue(HideEmptyDaysProperty, value);
    }

    public static readonly StyledProperty<bool> FillHeightProperty =
        AvaloniaProperty.Register<TestPointsChartControl, bool>(nameof(FillHeight));

    public bool FillHeight
    {
        get => GetValue(FillHeightProperty);
        set => SetValue(FillHeightProperty, value);
    }

    static TestPointsChartControl()
    {
        AffectsMeasure<TestPointsChartControl>(DataProperty, HideEmptyDaysProperty, FillHeightProperty);
        AffectsRender<TestPointsChartControl>(DataProperty, HideEmptyDaysProperty, FillHeightProperty);
    }

    private const double AxisH = 44, PlotH = 280, PadB = 34, Pad = 12;
    private const double MinPlotH = 160;
    private double _plotH = PlotH;

    private static readonly Color GridColor = Color.FromArgb(40, 255, 255, 255);
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 154, 164, 178)), 1);
    private static readonly IPen TickPenStrong = new Pen(new SolidColorBrush(Color.FromArgb(180, 154, 164, 178)), 1);
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush TooltipBg = new SolidColorBrush(Color.FromArgb(244, 20, 26, 34));
    private static readonly IPen TooltipBorder = new Pen(new SolidColorBrush(Color.Parse("#3A4453")), 1);
    private static readonly IBrush FrozenBg = new SolidColorBrush(Color.Parse("#1A2029"));
    private static readonly IBrush MasterDeadlineBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush FreezeBrush = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 230, 234, 242)), 1,
        new DashStyle(new double[] { 2, 2 }, 0));

    private int _days;
    private List<DateOnly> _dayList = new();

    private readonly List<(string Name, Color Color, double[] Remaining)> _series = new();
    private int _hoverDay = -1;
    private Point _hoverPos;

    private readonly List<ScrollViewer> _scrolls = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var sv in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(this).OfType<ScrollViewer>())
        {
            _scrolls.Add(sv);
            sv.ScrollChanged += OnAncestorScrolled;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var sv in _scrolls) sv.ScrollChanged -= OnAncestorScrolled;
        _scrolls.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAncestorScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private Point FrozenOrigin()
    {
        double x = 0, y = 0;
        foreach (var sv in _scrolls)
            if (sv.TranslatePoint(default, this) is { } p)
            {
                x = Math.Max(x, p.X);
                y = Math.Max(y, p.Y);
            }
        return new Point(x, y);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var d = Data;
        if (d is null) return new Size(0, 0);
        int days = GanttDayMap.VisibleDays(d, HideEmptyDays).Count;
        double w = GanttControl.LabelW + days * GanttControl.DayW + Pad * 2;
        double h = AxisH + PlotH + PadB;
        if (FillHeight && !double.IsInfinity(availableSize.Height) && availableSize.Height > 0)
            h = Math.Max(AxisH + MinPlotH + PadB, availableSize.Height);
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var d = Data;
        if (d is null) return;

        var sc = d.Scenario;
        var sch = d.Schedule;
        _dayList = GanttDayMap.VisibleDays(d, HideEmptyDays);
        _days = _dayList.Count;
        if (_days <= 0) return;

        _plotH = FillHeight ? Math.Max(MinPlotH, Bounds.Height - AxisH - PadB) : PlotH;
        var dayIndex = new Dictionary<DateOnly, int>(_days);
        for (int i = 0; i < _days; i++) dayIndex[_dayList[i]] = i;

        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));

        _series.Clear();
        var campColor = GanttPalette.CampaignColors(sc);
        double maxY = 1;
        bool Show(Domain.Campaign c) => d.CampaignIds is null || d.CampaignIds.Contains(c.Id);
        foreach (var c in sc.Campaigns)
        {
            if (!Show(c)) continue;
            var flown = new double[_days];
            foreach (var so in sch.Sorties)
            {
                if (so.CampaignId != c.Id) continue;
                if (dayIndex.TryGetValue(so.Date, out int i)) flown[i] += so.TestPointsFlown;
            }
            foreach (var ss in sch.SimulatorSorties)
            {
                if (ss.CampaignId != c.Id) continue;
                if (dayIndex.TryGetValue(ss.Date, out int i)) flown[i] += ss.TestPointsFlown;
            }

            double start = Math.Max(c.TotalTestPoints, flown.Sum());
            var remaining = new double[_days];
            double cum = 0;
            for (int i = 0; i < _days; i++)
            {
                cum += flown[i];
                remaining[i] = Math.Max(0, start - cum);
            }
            maxY = Math.Max(maxY, start);
            _series.Add((c.Name, campColor[c.Id], remaining));
        }

        double plotBottom = AxisH + _plotH;
        double X(int i) => GanttControl.LabelW + i * GanttControl.DayW + GanttControl.DayW / 2;
        double Y(double pts) => AxisH + (1 - pts / maxY) * _plotH;

        for (int i = 0; i < _days; i++)
        {
            var day = _dayList[i];
            double x = GanttControl.LabelW + i * GanttControl.DayW;

            Color? shade =
                sc.Calendar.Holidays.Contains(day) ? GanttPalette.HolidayShade
                : sc.Calendar.WeekendDays.Contains(day.DayOfWeek) ? GanttPalette.WeekendShade
                : !sc.FlyableModel.IsFlyable(day) ? GanttPalette.NotFlyableShade
                : (Color?)null;
            if (shade is { } s)
                ctx.FillRectangle(new SolidColorBrush(s), new Rect(x, AxisH, GanttControl.DayW, _plotH));
            if (sc.MasterDeadline is { } master && day > master)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(16, 239, 68, 68)),
                    new Rect(x, AxisH, GanttControl.DayW, _plotH));

            if (day.DayOfWeek == DayOfWeek.Monday)
                ctx.DrawLine(new Pen(new SolidColorBrush(GridColor), 1),
                    new Point(x, AxisH), new Point(x, plotBottom));
        }

        for (int g = 0; g <= 4; g++)
        {
            double y = AxisH + g / 4.0 * _plotH;
            ctx.DrawLine(new Pen(new SolidColorBrush(GridColor), 1),
                new Point(GanttControl.LabelW, y),
                new Point(GanttControl.LabelW + _days * GanttControl.DayW, y));
        }

        foreach (var c in sc.Campaigns)
        {
            if (!Show(c)) continue;
            if (ColumnAtOrBefore(c.TargetEndDate) is not { } i) continue;
            double cx = GanttControl.LabelW + (i + 1) * GanttControl.DayW;
            var pen = new Pen(new SolidColorBrush(campColor[c.Id]), 1.5,
                new DashStyle(new double[] { 4, 3 }, 0));
            ctx.DrawLine(pen, new Point(cx, AxisH), new Point(cx, plotBottom));
        }
        if (sc.MasterDeadline is { } md && ColumnAtOrBefore(md) is { } mi)
        {
            double cx = GanttControl.LabelW + (mi + 1) * GanttControl.DayW;
            var pen = new Pen(MasterDeadlineBrush, 2.2, new DashStyle(new double[] { 6, 3 }, 0));
            ctx.DrawLine(pen, new Point(cx, AxisH), new Point(cx, plotBottom));
        }

        int freezeCol = sc.FreezeDate is { } fz ? (ColumnAtOrAfter(fz) ?? _days) : _days;
        foreach (var (_, color, remaining) in _series)
        {
            var solid = new Pen(new SolidColorBrush(color), 2.2);
            var dashed = new Pen(new SolidColorBrush(color), 2.0, new DashStyle(new double[] { 4, 3 }, 0));
            for (int i = 1; i < _days; i++)
                ctx.DrawLine(i <= freezeCol ? solid : dashed,
                    new Point(X(i - 1), Y(remaining[i - 1])),
                    new Point(X(i), Y(remaining[i])));
        }

        if (sc.FreezeDate is { } freeze && ColumnAtOrAfter(freeze) is { } fcol)
        {
            double fxp = GanttControl.LabelW + fcol * GanttControl.DayW;
            ctx.DrawLine(new Pen(FreezeBrush, 2.0, new DashStyle(new double[] { 2, 2 }, 0)),
                new Point(fxp, AxisH), new Point(fxp, plotBottom));
        }

        var origin = FrozenOrigin();
        DrawFrozenValueColumn(ctx, origin, maxY, plotBottom);
        DrawFrozenAxis(ctx, sc, origin);
        DrawLegend(ctx, origin);
        DrawEarnedValue(ctx, sc, sch, origin);

        DrawHover(ctx, sc, plotBottom);
    }

    private void DrawLegend(DrawingContext ctx, Point origin)
    {
        if (_series.Count == 0) return;

        const double pad = 8, fontSize = 11, lineH = 16, swatch = 14;
        double w = _series.Max(s => s.Name.Length) * fontSize * 0.55 + swatch + 6 + pad * 2;
        double h = _series.Count * lineH + pad * 2;
        double x = origin.X + GanttControl.LabelW + 10;
        double y = origin.Y + AxisH + 8;

        ctx.DrawRectangle(TooltipBg, TooltipBorder, new Rect(x, y, w, h), 5, 5);
        for (int i = 0; i < _series.Count; i++)
        {
            double ly = y + pad + i * lineH;
            ctx.DrawLine(new Pen(new SolidColorBrush(_series[i].Color), 2.5),
                new Point(x + pad, ly + 7), new Point(x + pad + swatch, ly + 7));
            DrawText(ctx, _series[i].Name, new Point(x + pad + swatch + 6, ly), fontSize, TextPrimary);
        }
    }

    private void DrawFrozenValueColumn(DrawingContext ctx, Point origin, double maxY, double plotBottom)
    {
        double x = origin.X;
        ctx.FillRectangle(FrozenBg, new Rect(x, 0, GanttControl.LabelW, Math.Max(plotBottom + PadB, Bounds.Height)));
        DrawText(ctx, "remaining TPs", new Point(x + Pad, AxisH - 20), 10, TextMuted);
        for (int g = 0; g <= 4; g++)
        {
            double v = maxY * (4 - g) / 4.0;
            double y = AxisH + g / 4.0 * _plotH;
            DrawText(ctx, v.ToString("0.#"), new Point(x + Pad, y - 7), 10, TextMuted);
        }
    }

    private void DrawFrozenAxis(DrawingContext ctx, Scheduling.Scenario sc, Point origin)
    {
        double w = Math.Max(GanttControl.LabelW + _days * GanttControl.DayW + Pad * 2, Bounds.Width);
        ctx.FillRectangle(FrozenBg, new Rect(0, origin.Y, w, AxisH - 4));

        double tickBottom = origin.Y + AxisH - 5;
        for (int i = 0; i < _days; i++)
        {
            double x = GanttControl.LabelW + i * GanttControl.DayW;
            if (x < origin.X + GanttControl.LabelW) continue;

            var day = _dayList[i];
            bool gap = i > 0 && _dayList[i - 1].DayNumber != day.DayNumber - 1;
            bool labeled = day.Day == 1 || day.DayOfWeek == DayOfWeek.Monday || gap;

            double tickH = labeled ? 9 : 4;
            ctx.DrawLine(labeled ? TickPenStrong : TickPen,
                new Point(x, tickBottom - tickH), new Point(x, tickBottom));

            if (labeled)
                DrawText(ctx, day.ToString("MMM d", CultureInfo.InvariantCulture),
                    new Point(x + 1, origin.Y + AxisH - 18), 10, TextMuted);
        }

        if (sc.MasterDeadline is { } md && ColumnAtOrBefore(md) is { } mi)
        {
            double cx = GanttControl.LabelW + (mi + 1) * GanttControl.DayW;
            if (cx >= origin.X + GanttControl.LabelW)
                DrawText(ctx, "master deadline", new Point(cx + 3, origin.Y + 4), 10, MasterDeadlineBrush);
        }
    }

    private int? ColumnAtOrBefore(DateOnly day)
    {
        if (_days == 0 || day < _dayList[0]) return null;
        int i = _dayList.BinarySearch(day);
        if (i < 0) i = ~i - 1;
        return i < 0 ? null : i;
    }

    private int? ColumnAtOrAfter(DateOnly day)
    {
        if (_days == 0 || day > _dayList[^1]) return null;
        int i = _dayList.BinarySearch(day);
        if (i < 0) i = ~i;
        return i;
    }

    private void DrawEarnedValue(DrawingContext ctx, Scheduling.Scenario sc, Scheduling.Schedule sch, Point origin)
    {
        if (sc.FreezeDate is not { } freeze) return;

        double total = sc.Campaigns.Sum(c => c.TotalTestPoints);
        if (total <= 0) return;
        double flown = sch.Sorties.Where(s => s.IsActual).Sum(s => s.TestPointsFlown)
            + sch.SimulatorSorties.Where(s => s.IsActual).Sum(s => s.TestPointsFlown);
        double pct = flown / total;

        DateOnly start = sc.Campaigns.Count > 0 ? sc.Campaigns.Min(c => c.StartDate) : sc.HorizonStart;
        if (start < sc.HorizonStart) start = sc.HorizonStart;

        int usableElapsed = 0;
        for (var d = start; d < freeze; d = d.AddDays(1))
            if (sc.Calendar.IsWorkday(d) && sc.FlyableModel.IsFlyable(d)) usableElapsed++;
        double pace = usableElapsed > 0 ? flown / usableElapsed : 0;
        double remaining = Math.Max(0, total - flown);

        var lines = new List<string>
        {
            $"Earned value · as of {freeze:yyyy-MM-dd}",
            $"Flown: {flown:0.#}/{total:0.#} pts ({pct:0%})",
            pace > 0 ? $"Pace: {pace:0.0#} good pts / usable day" : "Pace: · (no progress yet)",
        };

        if (remaining <= 0)
            lines.Add("At this pace: complete");
        else if (pace > 0)
        {
            int need = (int)Math.Ceiling(remaining / pace);
            var d = freeze;
            int got = 0, guard = 0;
            while (got < need && guard++ < 4000)
            {
                if (sc.Calendar.IsWorkday(d) && sc.FlyableModel.IsFlyable(d)) got++;
                if (got < need) d = d.AddDays(1);
            }
            lines.Add($"At this pace: finish {d:yyyy-MM-dd}{VsMaster(d, sc.MasterDeadline)}");
        }

        var planDates = sch.Sorties.Select(s => s.Date).Concat(sch.SimulatorSorties.Select(s => s.Date)).ToList();
        if (planDates.Count > 0)
        {
            var fin = planDates.Max();
            lines.Add($"Plan re-forecast: finish {fin:yyyy-MM-dd}{VsMaster(fin, sc.MasterDeadline)}");
        }

        const double pad = 8, fontSize = 11, lineH = 15.5;
        double w = lines.Max(l => l.Length) * fontSize * 0.52 + pad * 2;
        double x = origin.X + GanttControl.LabelW + 10;
        double y = origin.Y + AxisH + 8 + (_series.Count * 16 + 16) + 10;
        double h = lines.Count * lineH + pad * 2;

        ctx.DrawRectangle(TooltipBg, TooltipBorder, new Rect(x, y, w, h), 5, 5);
        for (int i = 0; i < lines.Count; i++)
            DrawText(ctx, lines[i], new Point(x + pad, y + pad + i * lineH), fontSize,
                i == 0 ? TextPrimary : TextMuted);
    }

    private static string VsMaster(DateOnly finish, DateOnly? master)
    {
        if (master is not { } m) return "";
        int d = finish.DayNumber - m.DayNumber;
        return d <= 0 ? $"  ({-d}d before master)" : $"  ({d}d past master)";
    }

    private void DrawHover(DrawingContext ctx, Scheduling.Scenario sc, double plotBottom)
    {
        if (_hoverDay < 0 || _hoverDay >= _days || _series.Count == 0) return;

        double gx = GanttControl.LabelW + _hoverDay * GanttControl.DayW + GanttControl.DayW / 2;
        ctx.DrawLine(HoverPen, new Point(gx, AxisH), new Point(gx, plotBottom));

        var lines = new List<string> { _dayList[_hoverDay].ToString("yyyy-MM-dd (ddd)") };
        lines.AddRange(_series.Select(s => $"{s.Name}: {s.Remaining[_hoverDay]:0.#} left"));

        const double pad = 8, fontSize = 11, lineH = 15.5;
        double w = lines.Max(l => l.Length) * fontSize * 0.55 + pad * 2;
        double h = lines.Count * lineH + pad * 2;
        double tx = _hoverPos.X + 14, ty = _hoverPos.Y + 14;
        if (tx + w > Bounds.Width) tx = _hoverPos.X - w - 8;
        if (ty + h > Bounds.Height) ty = Bounds.Height - h - 2;
        if (tx < 0) tx = 2;
        if (ty < 0) ty = 2;

        ctx.DrawRectangle(TooltipBg, TooltipBorder, new Rect(tx, ty, w, h), 5, 5);
        for (int i = 0; i < lines.Count; i++)
            DrawText(ctx, lines[i], new Point(tx + pad, ty + pad + i * lineH), fontSize,
                i == 0 ? TextPrimary : TextMuted);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        int day = -1;
        if (pt.X >= GanttControl.LabelW && pt.Y >= AxisH && pt.Y <= AxisH + _plotH && _days > 0)
            day = Math.Clamp((int)((pt.X - GanttControl.LabelW) / GanttControl.DayW), 0, _days - 1);

        if (day != _hoverDay || (day >= 0 && pt != _hoverPos))
        {
            _hoverDay = day;
            _hoverPos = pt;
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverDay >= 0)
        {
            _hoverDay = -1;
            InvalidateVisual();
        }
        base.OnPointerExited(e);
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, double size, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        ctx.DrawText(ft, at);
    }
}
