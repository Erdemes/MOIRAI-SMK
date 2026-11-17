using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MOIRAI.Domain;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class WindowsCalendarControl : Control
{
    public static readonly StyledProperty<WindowsCalendarData?> DataProperty =
        AvaloniaProperty.Register<WindowsCalendarControl, WindowsCalendarData?>(nameof(Data));

    public WindowsCalendarData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly StyledProperty<bool> InteractiveProperty =
        AvaloniaProperty.Register<WindowsCalendarControl, bool>(nameof(Interactive));

    public bool Interactive
    {
        get => GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    public static readonly StyledProperty<ICommand?> WindowChangedCommandProperty =
        AvaloniaProperty.Register<WindowsCalendarControl, ICommand?>(nameof(WindowChangedCommand));

    public ICommand? WindowChangedCommand
    {
        get => GetValue(WindowChangedCommandProperty);
        set => SetValue(WindowChangedCommandProperty, value);
    }

    static WindowsCalendarControl()
    {
        AffectsMeasure<WindowsCalendarControl>(DataProperty);
        AffectsRender<WindowsCalendarControl>(DataProperty);
    }

    private enum DragMode { Move, ResizeStart, ResizeEnd }

    private sealed class DragState
    {
        public required int Row;
        public required DateRange Win;
        public required DragMode Mode;
        public required int OrigStartDay;
        public required int OrigEndDay;
        public required int PressDay;
        public int PreviewStartDay;
        public int PreviewEndDay;
    }

    private const double EdgeGrip = 7;
    private readonly List<(Rect rect, int row, DateRange win)> _bands = new();
    private DragState? _drag;

    private const double DayW = 26, RowH = 26, AxisH = 40, LabelW = 120, Pad = 12;

    private static readonly Color WeekendShade = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color HolidayShade = Color.FromArgb(54, 150, 150, 165);
    private static readonly Color MaintenanceFill = Color.FromArgb(165, 148, 163, 184);
    private static readonly Color LeaveFill = Color.FromArgb(74, 234, 179, 8);
    private static readonly Color CampaignFill = Color.FromArgb(84, 99, 102, 241);
    private static readonly IPen MaintenancePen = new Pen(
        new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)), 1.5, new DashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen LeavePen = new Pen(
        new SolidColorBrush(Color.FromArgb(170, 234, 179, 8)), 1, new DashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen CampaignPen = new Pen(
        new SolidColorBrush(Color.FromArgb(210, 99, 102, 241)), 1.5);
    private static readonly IPen DragPen = new Pen(new SolidColorBrush(Color.Parse("#E6EAF2")), 2);
    private static readonly Color HighlightRow = Color.FromArgb(26, 99, 102, 241);
    private static readonly IPen TodayPen = new Pen(new SolidColorBrush(Color.Parse("#6366F1")), 1.5);
    private static readonly Color GridColor = Color.FromArgb(40, 255, 255, 255);
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 154, 164, 178)), 1);
    private static readonly IPen TickPenStrong = new Pen(new SolidColorBrush(Color.FromArgb(180, 154, 164, 178)), 1);
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush TooltipBg = new SolidColorBrush(Color.FromArgb(244, 20, 26, 34));
    private static readonly IPen TooltipBorder = new Pen(new SolidColorBrush(Color.Parse("#3A4453")), 1);

    private static readonly IBrush FrozenBg = new SolidColorBrush(Color.Parse("#1A2029"));

    private int _days;

    private readonly List<(Rect rect, string tip)> _hits = new();
    private string? _hoverTip;
    private Point _hoverPos;

    private readonly List<ScrollViewer> _scrolls = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var sv in this.GetVisualAncestors().OfType<ScrollViewer>())
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
        if (d is null || d.Rows.Count == 0) return new Size(0, 0);

        int days = d.End.DayNumber - d.Start.DayNumber + 1;
        return new Size(LabelW + days * DayW + Pad, AxisH + d.Rows.Count * RowH + Pad);
    }

    public override void Render(DrawingContext ctx)
    {
        _hits.Clear();
        _bands.Clear();
        var d = Data;
        if (d is null || d.Rows.Count == 0) return;

        _days = d.End.DayNumber - d.Start.DayNumber + 1;
        if (_days <= 0) return;

        var (fillColor, outline, word) = d.Kind switch
        {
            WindowsCalendarKind.Leave => (LeaveFill, LeavePen, "on leave"),
            WindowsCalendarKind.Campaign => (CampaignFill, CampaignPen, "planned window"),
            _ => (MaintenanceFill, MaintenancePen, "maintenance"),
        };
        var fill = new SolidColorBrush(fillColor);

        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));

        double bottom = AxisH + d.Rows.Count * RowH;

        for (int r = 0; r < d.Rows.Count; r++)
            if (d.Rows[r].Id == d.HighlightId)
                ctx.FillRectangle(new SolidColorBrush(HighlightRow),
                    new Rect(0, AxisH + r * RowH, LabelW + _days * DayW, RowH));

        for (int i = 0; i < _days; i++)
        {
            var day = d.Start.AddDays(i);
            double x = LabelW + i * DayW;

            Color? shade =
                d.Calendar.Holidays.Contains(day) ? HolidayShade
                : d.Calendar.WeekendDays.Contains(day.DayOfWeek) ? WeekendShade
                : (Color?)null;
            if (shade is { } c)
                ctx.FillRectangle(new SolidColorBrush(c), new Rect(x, AxisH, DayW, bottom - AxisH));

            if (day.DayOfWeek == DayOfWeek.Monday)
                ctx.DrawLine(new Pen(new SolidColorBrush(GridColor), 1),
                    new Point(x, AxisH), new Point(x, bottom));
        }

        for (int r = 0; r < d.Rows.Count; r++)
        {
            double y = AxisH + r * RowH;
            ctx.DrawLine(new Pen(new SolidColorBrush(GridColor), 1),
                new Point(0, y), new Point(LabelW + _days * DayW, y));
        }

        for (int r = 0; r < d.Rows.Count; r++)
        {
            var (rowFill, rowOutline) = BandStyle(d.Kind, d.Rows[r].Accent, fill, outline);
            foreach (var w in d.Rows[r].Windows)
            {
                int i0 = Math.Max(0, w.Start.DayNumber - d.Start.DayNumber);
                int i1 = Math.Min(_days - 1, w.End.DayNumber - d.Start.DayNumber);
                if (i1 < i0) continue;

                var rect = new Rect(LabelW + i0 * DayW, AxisH + r * RowH + 3,
                    (i1 - i0 + 1) * DayW, RowH - 6);

                if (_drag is { } dr && ReferenceEquals(dr.Win, w)) { _bands.Add((rect, r, w)); continue; }

                ctx.DrawRectangle(rowFill, rowOutline, rect, 3, 3);
                if (Interactive) _bands.Add((rect, r, w));

                if (!string.IsNullOrWhiteSpace(w.Note) && rect.Width > 38)
                    DrawText(ctx, w.Note, new Point(rect.X + 5, rect.Y + (rect.Height - 13) / 2),
                        10, TextPrimary, rect.Width - 10);

                string tip = $"{d.Rows[r].Name} · {word}";
                if (!string.IsNullOrWhiteSpace(w.Note)) tip += $": {w.Note}";
                tip += $"\n{w.Start:yyyy-MM-dd} … {w.End:yyyy-MM-dd} ({w.Days} day{(w.Days == 1 ? "" : "s")})";
                _hits.Add((rect, tip));
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        int ti = today.DayNumber - d.Start.DayNumber;
        if (ti >= 0 && ti < _days)
        {
            double tx = LabelW + ti * DayW + DayW / 2;
            ctx.DrawLine(TodayPen, new Point(tx, AxisH - 4), new Point(tx, bottom));
        }

        if (_drag is { } drag)
        {
            int i0 = Math.Max(0, drag.PreviewStartDay - d.Start.DayNumber);
            int i1 = Math.Min(_days - 1, drag.PreviewEndDay - d.Start.DayNumber);
            if (i1 >= i0)
            {
                var (dragFill, _) = BandStyle(d.Kind, d.Rows[drag.Row].Accent, fill, outline);
                ctx.DrawRectangle(dragFill, DragPen,
                    new Rect(LabelW + i0 * DayW, AxisH + drag.Row * RowH + 3, (i1 - i0 + 1) * DayW, RowH - 6), 3, 3);
            }
        }

        var origin = FrozenOrigin();
        DrawFrozenLabelColumn(ctx, origin, d, bottom);
        DrawFrozenAxis(ctx, origin, d, ti);

        if (_drag is { } dragLabel) DrawDragLabel(ctx, dragLabel);
        else DrawTooltip(ctx);
    }

    private void DrawDragLabel(DrawingContext ctx, DragState drag)
    {
        var s = DateOnly.FromDayNumber(drag.PreviewStartDay);
        var e = DateOnly.FromDayNumber(drag.PreviewEndDay);
        int days = drag.PreviewEndDay - drag.PreviewStartDay + 1;
        string verb = drag.Mode switch
        {
            DragMode.ResizeStart => "↤ start",
            DragMode.ResizeEnd => "end ↦",
            _ => "move",
        };
        var lines = new[]
        {
            verb,
            $"{s:ddd MMM d} … {e:ddd MMM d}",
            $"{days} day{(days == 1 ? "" : "s")}",
        };
        const double pad = 8, fontSize = 11, lineH = 15.5;
        double w = lines.Max(l => Estimate(l, fontSize)) + pad * 2;
        double h = lines.Length * lineH + pad * 2;
        double x = _hoverPos.X + 14, y = _hoverPos.Y - h - 10;
        if (x + w > Bounds.Width) x = _hoverPos.X - w - 8;
        if (y < 0) y = _hoverPos.Y + 18;
        if (x < 0) x = 2;

        ctx.DrawRectangle(TooltipBg, TooltipBorder, new Rect(x, y, w, h), 5, 5);
        for (int i = 0; i < lines.Length; i++)
            DrawText(ctx, lines[i], new Point(x + pad, y + pad + i * lineH), fontSize,
                i == 0 ? TextMuted : TextPrimary);
    }

    private void DrawFrozenLabelColumn(DrawingContext ctx, Point origin, WindowsCalendarData d, double bottom)
    {
        double x = origin.X;
        ctx.FillRectangle(FrozenBg, new Rect(x, 0, LabelW, Math.Max(bottom + Pad, Bounds.Height)));

        double labelY = (RowH - 14) / 2;
        for (int r = 0; r < d.Rows.Count; r++)
        {
            double y = AxisH + r * RowH;
            if (d.Rows[r].Id == d.HighlightId)
                ctx.FillRectangle(new SolidColorBrush(HighlightRow), new Rect(x, y, LabelW, RowH));
            DrawText(ctx, d.Rows[r].Name, new Point(x + Pad, y + labelY), 12,
                d.Rows[r].Id == d.HighlightId ? TextPrimary : TextMuted, LabelW - Pad);
        }
    }

    private static (IBrush fill, IPen outline) BandStyle(
        WindowsCalendarKind kind, Color? accent, IBrush defaultFill, IPen defaultOutline)
    {
        if (kind != WindowsCalendarKind.Campaign || accent is not { } a)
            return (defaultFill, defaultOutline);
        var fill = new SolidColorBrush(Color.FromArgb(84, a.R, a.G, a.B));
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(210, a.R, a.G, a.B)), 1.5);
        return (fill, outline);
    }

    private void DrawFrozenAxis(DrawingContext ctx, Point origin, WindowsCalendarData d, int todayIndex)
    {
        double w = Math.Max(LabelW + _days * DayW + Pad, Bounds.Width);
        ctx.FillRectangle(FrozenBg, new Rect(0, origin.Y, w, AxisH - 4));

        double tickBottom = origin.Y + AxisH - 5;
        for (int i = 0; i < _days; i++)
        {
            double x = LabelW + i * DayW;
            if (x < origin.X + LabelW) continue;

            var day = d.Start.AddDays(i);
            bool labeled = day.Day == 1 || day.DayOfWeek == DayOfWeek.Monday;

            double tickH = labeled ? 9 : 4;
            ctx.DrawLine(labeled ? TickPenStrong : TickPen,
                new Point(x, tickBottom - tickH), new Point(x, tickBottom));

            if (labeled)
                DrawText(ctx, day.ToString("MMM d", CultureInfo.InvariantCulture),
                    new Point(x + 2, origin.Y + AxisH - 18), 10, TextMuted);
        }

        if (todayIndex >= 0 && todayIndex < _days)
        {
            double tx = LabelW + todayIndex * DayW + DayW / 2;
            if (tx >= origin.X + LabelW)
                DrawText(ctx, "today", new Point(tx + 3, origin.Y + AxisH - 18), 10,
                    new SolidColorBrush(Color.Parse("#6366F1")));
        }
    }

    private int DayAtX(WindowsCalendarData d, double x) =>
        d.Start.DayNumber + Math.Clamp((int)Math.Floor((x - LabelW) / DayW), 0, _days - 1);

    private (int row, DateRange win, DragMode mode)? BandAt(Point pt)
    {
        for (int i = _bands.Count - 1; i >= 0; i--)
        {
            var (rect, row, win) = _bands[i];
            if (!rect.Contains(pt)) continue;
            DragMode mode =
                pt.X - rect.X <= EdgeGrip ? DragMode.ResizeStart
                : rect.Right - pt.X <= EdgeGrip ? DragMode.ResizeEnd
                : DragMode.Move;
            return (row, win, mode);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var d = Data;
        if (Interactive && d is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && BandAt(e.GetPosition(this)) is { } hit)
        {
            int press = DayAtX(d, e.GetPosition(this).X);
            _drag = new DragState
            {
                Row = hit.row, Win = hit.win, Mode = hit.mode,
                OrigStartDay = hit.win.Start.DayNumber, OrigEndDay = hit.win.End.DayNumber,
                PressDay = press,
                PreviewStartDay = hit.win.Start.DayNumber, PreviewEndDay = hit.win.End.DayNumber,
            };
            _hoverTip = null;
            _hoverPos = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        var d = Data;

        if (_drag is { } drag && d is not null)
        {
            _hoverPos = pt;
            int delta = DayAtX(d, pt.X) - drag.PressDay;
            int minDay = d.Start.DayNumber, maxDay = d.Start.DayNumber + _days - 1;
            switch (drag.Mode)
            {
                case DragMode.Move:
                    int len = drag.OrigEndDay - drag.OrigStartDay;
                    int ns = Math.Clamp(drag.OrigStartDay + delta, minDay, maxDay - len);
                    drag.PreviewStartDay = ns; drag.PreviewEndDay = ns + len;
                    break;
                case DragMode.ResizeStart:
                    drag.PreviewStartDay = Math.Clamp(drag.OrigStartDay + delta, minDay, drag.OrigEndDay);
                    drag.PreviewEndDay = drag.OrigEndDay;
                    break;
                case DragMode.ResizeEnd:
                    drag.PreviewStartDay = drag.OrigStartDay;
                    drag.PreviewEndDay = Math.Clamp(drag.OrigEndDay + delta, drag.OrigStartDay, maxDay);
                    break;
            }
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }

        if (Interactive)
            Cursor = BandAt(pt) is { } b
                ? new Cursor(b.mode == DragMode.Move ? StandardCursorType.SizeAll : StandardCursorType.SizeWestEast)
                : Cursor.Default;

        string? tip = null;
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].rect.Contains(pt)) { tip = _hits[i].tip; break; }

        if (tip != _hoverTip || (tip != null && pt != _hoverPos))
        {
            _hoverTip = tip;
            _hoverPos = pt;
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is { } drag && Data is { } d)
        {
            bool changed = drag.PreviewStartDay != drag.OrigStartDay || drag.PreviewEndDay != drag.OrigEndDay;
            if (changed && WindowChangedCommand is { } cmd)
            {
                var change = new WindowChange(d.Rows[drag.Row].Id, drag.Win.Id,
                    DateOnly.FromDayNumber(drag.PreviewStartDay), DateOnly.FromDayNumber(drag.PreviewEndDay));
                if (cmd.CanExecute(change)) cmd.Execute(change);
            }
            _drag = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_drag is not null) { _drag = null; InvalidateVisual(); }
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverTip != null)
        {
            _hoverTip = null;
            InvalidateVisual();
        }
        base.OnPointerExited(e);
    }

    private void DrawTooltip(DrawingContext ctx)
    {
        if (string.IsNullOrEmpty(_hoverTip)) return;

        var lines = _hoverTip.Split('\n');
        const double pad = 8, fontSize = 11, lineH = 15.5;
        double w = lines.Max(l => Estimate(l, fontSize)) + pad * 2;
        double h = lines.Length * lineH + pad * 2;

        double x = _hoverPos.X + 14, y = _hoverPos.Y + 16;
        if (x + w > Bounds.Width) x = _hoverPos.X - w - 8;
        if (y + h > Bounds.Height) y = Bounds.Height - h - 2;
        if (x < 0) x = 2;
        if (y < 0) y = 2;

        ctx.DrawRectangle(TooltipBg, TooltipBorder, new Rect(x, y, w, h), 5, 5);
        for (int i = 0; i < lines.Length; i++)
            DrawText(ctx, lines[i], new Point(x + pad, y + pad + i * lineH), fontSize,
                i == 0 ? TextPrimary : TextMuted);
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, double size, IBrush brush, double maxW = 0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        if (maxW > 0) ft.MaxTextWidth = maxW;
        ctx.DrawText(ft, at);
    }

    private static double Estimate(string text, double size) => text.Length * size * 0.55;
}
