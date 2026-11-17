using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class AnimationGanttControl : Control
{
    public static readonly StyledProperty<GanttData?> DataProperty =
        AvaloniaProperty.Register<AnimationGanttControl, GanttData?>(nameof(Data));

    public GanttData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly StyledProperty<double> CurrentBlockProperty =
        AvaloniaProperty.Register<AnimationGanttControl, double>(nameof(CurrentBlock));

    public double CurrentBlock
    {
        get => GetValue(CurrentBlockProperty);
        set => SetValue(CurrentBlockProperty, value);
    }

    static AnimationGanttControl()
    {
        AffectsRender<AnimationGanttControl>(DataProperty, CurrentBlockProperty);
        AffectsMeasure<AnimationGanttControl>(DataProperty);
    }

    private const double DayW = GanttControl.DayW, LabelW = 96, AxisH = 22, RowH = 46, RowInset = 3, Pad = 12;

    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush FrozenBg = new SolidColorBrush(Color.Parse("#1A2029"));
    private static readonly Color PanelHdrBg = Color.FromArgb(34, 255, 255, 255);
    private static readonly Color RowStripe = Color.FromArgb(14, 255, 255, 255);
    private static readonly IPen RowPen = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1);
    private static readonly IPen LanePen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 154, 164, 178)), 1);
    private static readonly IPen TickPenStrong = new Pen(new SolidColorBrush(Color.FromArgb(180, 154, 164, 178)), 1);
    private static readonly Color RepairBand = Color.FromArgb(46, 239, 68, 68);
    private static readonly Color PlayheadColor = Color.Parse("#8B78FF");
    private static readonly IPen AbortCrossPen = new Pen(new SolidColorBrush(Color.Parse("#EF4444")), 1.4);

    private ScrollViewer? _scroll;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentBlockProperty) AutoScroll();
    }

    private void AutoScroll()
    {
        if (_scroll is null || Data is not { } d) return;
        int bpd = Math.Max(1, d.Scenario.Config.BlocksPerDay);
        double px = LabelW + (CurrentBlock / bpd) * DayW;
        double viewW = _scroll.Viewport.Width;
        double offX = _scroll.Offset.X;
        double maxOff = Math.Max(0, _scroll.Extent.Width - viewW);
        if (viewW <= 0 || maxOff <= 0) return;

        double leftEdge = offX + LabelW;
        double rightTrigger = offX + viewW * 0.70;
        if (px < leftEdge || px > rightTrigger)
        {
            double target = Math.Clamp(px - viewW * 0.35, 0, maxOff);
            if (Math.Abs(target - offX) > 0.5)
                _scroll.Offset = _scroll.Offset.WithX(target);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var d = Data;
        if (d is null) return default;
        int n = d.Scenario.Aircraft.Count;
        int days = Math.Max(1, d.Scenario.HorizonEnd.DayNumber - d.Scenario.HorizonStart.DayNumber + 1);
        return new Size(LabelW + days * DayW + Pad, n == 0 ? 0 : AxisH + n * RowH + 2);
    }

    public override void Render(DrawingContext ctx)
    {
        var d = Data;
        if (d is null) return;
        var sc = d.Scenario;
        var sch = d.Schedule;

        int bpd = Math.Max(1, sc.Config.BlocksPerDay);
        int days = Math.Max(1, sc.HorizonEnd.DayNumber - sc.HorizonStart.DayNumber + 1);
        var start = sc.HorizonStart;
        var aircraft = sc.Aircraft.ToList();
        if (aircraft.Count == 0) return;

        double contentW = LabelW + days * DayW;
        double top = AxisH;
        double bottom = top + aircraft.Count * RowH;

        var acIndex = aircraft.Select((a, i) => (a.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var campColor = GanttPalette.CampaignColors(sc);
        var campById = sc.Campaigns.ToDictionary(c => c.Id);
        var shortOf = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).ToDictionary(r => r.Id, r => r.DisplayShort);

        double X(int dayIdx) => LabelW + dayIdx * DayW;
        double laneH = (RowH - 2 * RowInset) / bpd;

        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));

        ctx.FillRectangle(new SolidColorBrush(PanelHdrBg), new Rect(0, 0, contentW, AxisH - 2), 3);
        for (int r = 0; r < aircraft.Count; r++)
        {
            double y = top + r * RowH;
            if (r % 2 == 1) ctx.FillRectangle(new SolidColorBrush(RowStripe), new Rect(0, y, contentW, RowH));
            for (int b = 1; b < bpd; b++)
            {
                double ly = y + RowInset + b * laneH;
                ctx.DrawLine(LanePen, new Point(LabelW, ly), new Point(contentW, ly));
            }
            ctx.DrawLine(RowPen, new Point(0, y), new Point(contentW, y));
        }
        ctx.DrawLine(RowPen, new Point(0, bottom), new Point(contentW, bottom));

        for (int i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            bool extra = sc.Calendar.ExtraWorkdays.Contains(day);
            Color? shade =
                !extra && sc.Calendar.Holidays.Contains(day) ? GanttPalette.HolidayShade
                : !extra && sc.Calendar.WeekendDays.Contains(day.DayOfWeek) ? GanttPalette.WeekendShade
                : !sc.FlyableModel.IsFlyable(day) ? GanttPalette.NotFlyableShade
                : (Color?)null;
            if (shade is { } c)
                ctx.FillRectangle(new SolidColorBrush(c), new Rect(X(i), top, DayW, bottom - top));
        }

        void Band(int row, DateOnly s, DateOnly e, Color fill)
        {
            int i0 = Math.Max(0, s.DayNumber - start.DayNumber);
            int i1 = Math.Min(days - 1, e.DayNumber - start.DayNumber);
            if (i1 < i0) return;
            double y = top + row * RowH;
            ctx.FillRectangle(new SolidColorBrush(fill), new Rect(X(i0), y + 1.5, (i1 - i0 + 1) * DayW, RowH - 3));
        }
        for (int i = 0; i < aircraft.Count; i++)
        {
            foreach (var w in aircraft[i].MaintenanceWindows) Band(i, w.Start, w.End, GanttPalette.MaintenanceBand);
            if (sch.AutoMaintenance.TryGetValue(aircraft[i].Id, out var auto))
                foreach (var w in auto) Band(i, w.Start, w.End, GanttPalette.MaintenanceBand);
            if (sch.UnscheduledRepairs.TryGetValue(aircraft[i].Id, out var reps))
                foreach (var w in reps) Band(i, w.Start, w.End, RepairBand);
        }
        foreach (var dep in sch.Deployments)
            for (int i = 0; i < aircraft.Count; i++)
                if (dep.AircraftIds.Contains(aircraft[i].Id))
                    Band(i, dep.Departure, dep.ReturnEnd ?? sc.HorizonEnd, GanttPalette.DeploymentBand);

        foreach (var so in sch.Sorties)
        {
            int dayIdx = so.Date.DayNumber - start.DayNumber;
            if (dayIdx < 0 || dayIdx >= days) continue;
            int span = Math.Max(1, campById.TryGetValue(so.CampaignId, out var camp) ? camp.BlocksPerSortie : 1);
            var color = campColor.TryGetValue(so.CampaignId, out var cc) ? cc : Colors.White;
            double x = X(dayIdx);

            for (int si = 0; si < so.SupportAircraftIds.Count; si++)
            {
                if (!acIndex.TryGetValue(so.SupportAircraftIds[si], out int srow)) continue;
                string sLabel = si < so.SupportPilotIds.Count && shortOf.TryGetValue(so.SupportPilotIds[si], out var sp)
                    ? $"{sp} ⊕#{so.SortieNo}"
                    : $"⊕#{so.SortieNo}";
                DrawBox(ctx, x, top + srow * RowH, so.Block, span, bpd, laneH, color, filled: false, aborted: false, sLabel);
            }

            if (!acIndex.TryGetValue(so.AircraftId, out int prow)) continue;
            shortOf.TryGetValue(so.PilotId, out var pilotShort);
            string label = string.IsNullOrEmpty(pilotShort) ? $"#{so.SortieNo}" : $"{pilotShort} #{so.SortieNo}";
            DrawBox(ctx, x, top + prow * RowH, so.Block, span, bpd, laneH, color, filled: true, so.Aborted, label);
        }

        double cb = Math.Clamp(CurrentBlock, 0, days * bpd - 1);
        double px = LabelW + (cb / bpd) * DayW;
        ctx.DrawLine(new Pen(new SolidColorBrush(PlayheadColor), 1.8), new Point(px, AxisH - 2), new Point(px, bottom));

        double offX = _scroll?.Offset.X ?? 0;

        for (int i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            double x = X(i);
            if (x < offX + LabelW) continue;
            bool labeled = day.Day == 1 || day.DayOfWeek == DayOfWeek.Monday;
            ctx.DrawLine(labeled ? TickPenStrong : TickPen, new Point(x, AxisH - 7), new Point(x, AxisH - 2));
            if (labeled)
                DrawText(ctx, day.ToString("MMM d", CultureInfo.InvariantCulture), new Point(x + 1, 3), 9, TextMuted);
        }

        ctx.FillRectangle(FrozenBg, new Rect(offX, 0, LabelW, Math.Max(bottom + 2, Bounds.Height)));
        ctx.FillRectangle(new SolidColorBrush(PanelHdrBg), new Rect(offX, 0, LabelW, AxisH - 2), 3);
        DrawText(ctx, "AIRCRAFT", new Point(offX + 8, 4), 10, TextPrimary);
        for (int i = 0; i < aircraft.Count; i++)
            DrawText(ctx, aircraft[i].Name, new Point(offX + 8, top + i * RowH + (RowH - 12) / 2), 11, TextPrimary, LabelW - 12);

        if (px >= offX + LabelW)
        {
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(px - 5, AxisH - 9), true);
                g.LineTo(new Point(px + 5, AxisH - 9));
                g.LineTo(new Point(px, AxisH - 1));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(PlayheadColor), null, tri);
        }
    }

    private static void DrawBox(DrawingContext ctx, double x, double rowY, int block, int span, int bpd,
        double laneH, Color color, bool filled, bool aborted, string? label = null)
    {
        int b = Math.Clamp(block, 1, bpd);
        int s = Math.Clamp(span, 1, bpd - b + 1);
        double boxTop = rowY + RowInset + (b - 1) * laneH;
        var rect = new Rect(x + 1, boxTop + 0.5, DayW - 2, s * laneH - 1);
        if (filled)
            ctx.DrawRectangle(new SolidColorBrush(color), null, rect, 2, 2);
        else
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B)), null, rect, 2, 2);
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(color), 1, new DashStyle(new double[] { 3, 2 }, 0)), rect, 2, 2);
        }
        if (aborted)
        {
            ctx.DrawLine(AbortCrossPen, rect.TopLeft, rect.BottomRight);
            ctx.DrawLine(AbortCrossPen, rect.BottomLeft, rect.TopRight);
        }

        if (!string.IsNullOrEmpty(label) && rect.Height >= 9)
        {
            using var _ = ctx.PushClip(rect);
            DrawText(ctx, label!, new Point(rect.X + 2, rect.Y + (rect.Height - 11) / 2), 9,
                filled ? Brushes.White : new SolidColorBrush(color));
        }
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, double size, IBrush brush, double maxW = 0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        if (maxW > 0)
        {
            ft.MaxTextWidth = maxW;
            ft.MaxTextHeight = size * 1.4;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        ctx.DrawText(ft, at);
    }
}
