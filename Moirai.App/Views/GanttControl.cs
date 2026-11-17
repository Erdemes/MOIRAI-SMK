using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class GanttControl : Control
{
    public static readonly StyledProperty<GanttData?> DataProperty =
        AvaloniaProperty.Register<GanttControl, GanttData?>(nameof(Data));

    public GanttData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly StyledProperty<bool> HideEmptyDaysProperty =
        AvaloniaProperty.Register<GanttControl, bool>(nameof(HideEmptyDays));

    public bool HideEmptyDays
    {
        get => GetValue(HideEmptyDaysProperty);
        set => SetValue(HideEmptyDaysProperty, value);
    }

    public static readonly StyledProperty<bool> ShowIdleMarkersProperty =
        AvaloniaProperty.Register<GanttControl, bool>(nameof(ShowIdleMarkers));

    public bool ShowIdleMarkers
    {
        get => GetValue(ShowIdleMarkersProperty);
        set => SetValue(ShowIdleMarkersProperty, value);
    }

    static GanttControl()
    {
        AffectsMeasure<GanttControl>(DataProperty, HideEmptyDaysProperty);
        AffectsRender<GanttControl>(DataProperty, HideEmptyDaysProperty, ShowIdleMarkersProperty);
    }

    private GanttData? _idleFor;
    private MOIRAI.Reporting.IdleReport? _idle;

    private MOIRAI.Reporting.IdleReport IdleOf(GanttData d)
    {
        if (!ReferenceEquals(_idleFor, d) || _idle is null)
        {
            _idle = d.Idle ?? MOIRAI.Reporting.IdleReport.Build(d.Scenario, d.Schedule);
            _idleFor = d;
        }
        return _idle;
    }

    private void DrawIdleCells(DrawingContext ctx, MOIRAI.Reporting.IdleReport idle,
        IReadOnlyList<(string Id, string Name)> rows, double top)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            double y = top + r * RowH;
            for (int i = 0; i < _days; i++)
            {
                var day = _dayList[i];
                if (idle.Explain(rows[r].Id, day) is not { } e) continue;
                var rect = new Rect(LabelW + i * DayW, y, DayW, RowH);
                var tip = new System.Text.StringBuilder();
                tip.Append($"{rows[r].Name} · {day:dd.MM ddd}");
                tip.Append('\n').Append(e.Headline);
                foreach (var line in e.Details.Take(3)) tip.Append("\n• ").Append(line);
                _hits.Add((rect, tip.ToString(), null, null));

                if (ShowIdleMarkers && IdleMarkerBrush(e.Category) is { } brush)
                    ctx.FillRectangle(brush, new Rect(rect.X + 4, rect.Bottom - RowInset - 4, DayW - 8, 3), 1.5f);
            }
        }
    }

    internal static IBrush? IdleMarkerBrush(MOIRAI.Reporting.IdleCategory c) => c switch
    {
        MOIRAI.Reporting.IdleCategory.Unavailable => IdleUnavailableBrush,
        MOIRAI.Reporting.IdleCategory.NoWork => IdleNoWorkBrush,
        MOIRAI.Reporting.IdleCategory.Limits => IdleLimitsBrush,
        MOIRAI.Reporting.IdleCategory.EngineChoice => IdleChoiceBrush,
        _ => null,
    };

    internal static readonly IBrush IdleUnavailableBrush = new SolidColorBrush(Color.Parse("#64748B"));
    internal static readonly IBrush IdleNoWorkBrush = new SolidColorBrush(Color.Parse("#A8A29E"));
    internal static readonly IBrush IdleLimitsBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    internal static readonly IBrush IdleChoiceBrush = new SolidColorBrush(Color.Parse("#8B5CF6"));

    internal const double DayW = 58;

    private const double RowH = 48, AxisH = 44, PanelHdrH = 24, PanelGap = 28, Pad = 12;

    internal const double LabelW = 120;

    private const double RowInset = 3;

    private static readonly IPen DeploymentPen = new Pen(
        new SolidColorBrush(Color.Parse("#7C5Cff")), 1, new DashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen MaintenancePen = new Pen(
        new SolidColorBrush(Color.FromArgb(150, 148, 163, 184)), 1, new DashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen LeavePen = new Pen(
        new SolidColorBrush(Color.FromArgb(150, 234, 179, 8)), 1, new DashStyle(new double[] { 3, 2 }, 0));
    private static readonly Color RepairBand = Color.FromArgb(46, 239, 68, 68);
    private static readonly IPen RepairPen = new Pen(
        new SolidColorBrush(Color.FromArgb(160, 239, 68, 68)), 1, new DashStyle(new double[] { 3, 2 }, 0));
    private static readonly Color GridColor = Color.FromArgb(40, 255, 255, 255);
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 154, 164, 178)), 1);
    private static readonly IPen TickPenStrong = new Pen(new SolidColorBrush(Color.FromArgb(180, 154, 164, 178)), 1);

    private static readonly Color PanelHdrBg = Color.FromArgb(34, 255, 255, 255);
    private static readonly Color RowStripe = Color.FromArgb(14, 255, 255, 255);
    private static readonly IPen RowPen = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1);
    private static readonly IPen LanePen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);

    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush TooltipBg = new SolidColorBrush(Color.FromArgb(244, 20, 26, 34));
    private static readonly IPen TooltipBorder = new Pen(new SolidColorBrush(Color.Parse("#3A4453")), 1);

    private static readonly IBrush FrozenBg = new SolidColorBrush(Color.Parse("#1A2029"));

    private int _days;
    private int _bpd = 3;
    private Dictionary<string, string> _extNameOf = new();
    private Dictionary<string, string> _personNameOf = new();
    private Dictionary<string, string> _simNameOf = new();
    private IReadOnlyDictionary<(string CampaignId, int SortieNo), SortiePin>? _pins;

    private List<DateOnly> _dayList = new();
    private Dictionary<DateOnly, int> _dayIndex = new();

    private readonly List<(Rect rect, string tip, Sortie? sortie, SimulatorSortie? sim)> _hits = new();
    private string? _hoverTip;
    private Point _hoverPos;

    public event EventHandler<Sortie>? SortieClicked;

    public event EventHandler<SimulatorSortie>? SimulatorSortieClicked;

    public event EventHandler<GanttPinRequest>? PinRequested;

    private void OpenPinMenu(Sortie? so, SimulatorSortie? ss)
    {
        string campId = so?.CampaignId ?? ss!.CampaignId;
        int no = so?.SortieNo ?? ss!.SortieNo;
        bool actual = so?.IsActual ?? ss!.IsActual;
        DateOnly date = so?.Date ?? ss!.Date;
        bool pinned = _pins?.ContainsKey((campId, no)) ?? false;
        string campName = Data?.Scenario.Campaigns.FirstOrDefault(c => c.Id == campId)?.Name ?? campId;

        MenuItem Item(string header, GanttPinAction action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => PinRequested?.Invoke(this, new GanttPinRequest(action, so, ss));
            return mi;
        }

        var items = new List<object>();
        if (actual)
            items.Add(new MenuItem { Header = $"#{no} is a recorded actual · cannot be pinned", IsEnabled = false });
        else if (pinned)
        {
            items.Add(Item($"Edit pin #{no}…", GanttPinAction.EditPin));
            items.Add(Item($"Unpin #{no}", GanttPinAction.Unpin));
        }
        else
        {
            items.Add(Item(so is not null ? $"Pin #{no} as planned" : $"Pin #{no} as planned (crew + simulator)",
                GanttPinAction.Pin));
            items.Add(Item($"Pin #{no} to {date:dd.MM ddd} (day only)", GanttPinAction.PinDay));
            items.Add(Item($"Pin #{no} with changes…", GanttPinAction.EditPin));
        }
        items.Add(new Separator());
        items.Add(Item($"Pin all planned sorties of {campName}", GanttPinAction.PinCampaign));
        items.Add(Item($"Pin all planned sorties up to {date:yyyy-MM-dd}", GanttPinAction.PinUpToDate));
        items.Add(Item($"Unpin all of {campName}", GanttPinAction.UnpinCampaign,
            _pins?.Keys.Any(k => k.CampaignId == campId) ?? false));
        items.Add(Item("Clear all pins", GanttPinAction.ClearAll, _pins is { Count: > 0 }));

        var menu = new ContextMenu { ItemsSource = items, Placement = Avalonia.Controls.PlacementMode.Pointer };
        menu.Open(this);
    }

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
        if (d is null) return new Size(0, 0);

        int days = GanttDayMap.VisibleDays(d, HideEmptyDays).Count;
        var (aircraft, crew, sims) = RowsOf(d);
        double w = LabelW + days * DayW + Pad * 2;
        double h = AxisH + PanelHdrH + aircraft.Count * RowH + PanelGap
                   + PanelHdrH + crew.Count * RowH + Pad;
        if (sims.Count > 0) h += PanelGap + PanelHdrH + sims.Count * RowH;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        _hits.Clear();
        var d = Data;
        if (d is null) return;

        var sc = d.Scenario;
        var sch = d.Schedule;
        _pins = d.Pins;
        _dayList = GanttDayMap.VisibleDays(d, HideEmptyDays);
        _days = _dayList.Count;
        _dayIndex = new Dictionary<DateOnly, int>(_days);
        for (int i = 0; i < _days; i++) _dayIndex[_dayList[i]] = i;
        _bpd = Math.Max(1, sc.Config.BlocksPerDay);
        if (_days <= 0) return;

        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));

        var (aircraft, crew, sims) = RowsOf(d);
        var campById = sc.Campaigns.ToDictionary(c => c.Id);
        var campColor = GanttPalette.CampaignColors(sc);
        var nameOf = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).ToDictionary(r => r.Id, r => r.Name);
        var shortOf = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).ToDictionary(r => r.Id, r => r.DisplayShort);
        var acNameOf = sc.Aircraft.ToDictionary(a => a.Id, a => a.Name);
        var simNameOf = sc.Simulators.ToDictionary(s => s.Id, s => s.Name);
        _extNameOf = sc.ExternalSupports.ToDictionary(a => a.Id, a => a.Name);
        _personNameOf = nameOf;
        _simNameOf = simNameOf;
        var acIndex = aircraft.Select((a, i) => (a.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var crewIndex = crew.Select((c, i) => (c.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var simIndex = sims.Select((s, i) => (s.Id, i)).ToDictionary(t => t.Id, t => t.i);

        double acTop = AxisH + PanelHdrH;
        double acBottom = acTop + aircraft.Count * RowH;
        double crewHdr = acBottom + PanelGap;
        double crewTop = crewHdr + PanelHdrH;
        double crewBottom = crewTop + crew.Count * RowH;
        bool hasSims = sims.Count > 0;
        double simHdr = crewBottom + PanelGap;
        double simTop = simHdr + PanelHdrH;
        double simBottom = hasSims ? simTop + sims.Count * RowH : crewBottom;

        DrawPanelChrome(ctx, AxisH, acTop, aircraft.Count);
        DrawPanelChrome(ctx, crewHdr, crewTop, crew.Count);
        if (hasSims) DrawPanelChrome(ctx, simHdr, simTop, sims.Count);
        DrawDayShadingAndGrid(ctx, sc, sch, acTop, acBottom);
        DrawDayShadingAndGrid(ctx, sc, sch, crewTop, crewBottom);
        var idle = IdleOf(d);
        DrawIdleCells(ctx, idle, aircraft.Select(a => (a.Id, a.Name)).ToList(), acTop);
        DrawIdleCells(ctx, idle, crew.Select(c => (c.Id, c.Name)).ToList(), crewTop);
        if (hasSims) DrawDayShadingAndGrid(ctx, sc, sch, simTop, simBottom);

        for (int i = 0; i < aircraft.Count; i++)
        {
            foreach (var w in aircraft[i].MaintenanceWindows)
                Band(ctx, w, acTop + i * RowH, GanttPalette.MaintenanceBand, MaintenancePen,
                    BandTip(aircraft[i].Name, "maintenance", w));
            if (sch.AutoMaintenance.TryGetValue(aircraft[i].Id, out var auto))
                foreach (var w in auto)
                    Band(ctx, w, acTop + i * RowH, GanttPalette.MaintenanceBand, MaintenancePen,
                        BandTip(aircraft[i].Name, "periodic maintenance", w));
            if (sch.UnscheduledRepairs.TryGetValue(aircraft[i].Id, out var repairs))
                foreach (var w in repairs)
                    Band(ctx, w, acTop + i * RowH, RepairBand, RepairPen,
                        BandTip(aircraft[i].Name, "unscheduled repair (failure)", w));
        }
        for (int i = 0; i < crew.Count; i++)
            foreach (var w in crew[i].LeaveWindows)
                Band(ctx, w, crewTop + i * RowH, GanttPalette.LeaveBand, LeavePen,
                    BandTip(crew[i].Name, "on leave", w));
        for (int i = 0; i < sims.Count; i++)
        {
            foreach (var w in sims[i].MaintenanceWindows)
                Band(ctx, w, simTop + i * RowH, GanttPalette.MaintenanceBand, MaintenancePen,
                    BandTip(sims[i].Name, "maintenance", w));
            if (sch.SimulatorRepairs.TryGetValue(sims[i].Id, out var reps))
                foreach (var w in reps)
                    Band(ctx, w, simTop + i * RowH, RepairBand, RepairPen,
                        BandTip(sims[i].Name, "unscheduled repair (failure)", w));
        }

        foreach (var dep in sch.Deployments)
        {
            var w = new DateRange
            {
                Start = dep.Departure,
                End = dep.ReturnEnd ?? sc.HorizonEnd,
                Note = dep.LocationName,
            };
            for (int i = 0; i < aircraft.Count; i++)
                if (dep.AircraftIds.Contains(aircraft[i].Id))
                    Band(ctx, w, acTop + i * RowH, GanttPalette.DeploymentBand, DeploymentPen,
                        BandTip(aircraft[i].Name, $"deployed to {dep.LocationName}", w));
            for (int i = 0; i < crew.Count; i++)
                if (dep.PersonIds.Contains(crew[i].Id))
                    Band(ctx, w, crewTop + i * RowH, GanttPalette.DeploymentBand, DeploymentPen,
                        BandTip(crew[i].Name, $"deployed to {dep.LocationName}", w));

            if (dep.ReturnEnd is { } retEnd && dep.RestEnd is { } restEnd && restEnd > retEnd)
            {
                var rw = new DateRange { Start = retEnd.AddDays(1), End = restEnd, Note = "post-deployment rest" };
                for (int i = 0; i < crew.Count; i++)
                    if (dep.PersonIds.Contains(crew[i].Id))
                        Band(ctx, rw, crewTop + i * RowH, GanttPalette.LeaveBand, LeavePen,
                            BandTip(crew[i].Name, $"post-deployment rest ({dep.LocationName})", rw));
            }
        }

        foreach (var r in sch.Rehearsals)
        {
            if (!TryX(r.Date, out double x)) continue;
            string tip = RehearsalTip(r, campById, nameOf);
            string label = $"R#{r.SortieNo}";
            foreach (var id in r.CrewIds)
                if (crewIndex.TryGetValue(id, out int row))
                    Box(ctx, x, crewTop + row * RowH, r.Block, 1, campColor[r.CampaignId], filled: false, label, tip);
        }
        foreach (var so in sch.Sorties)
        {
            if (!TryX(so.Date, out double x)) continue;
            string tip = SortieTip(so, campById, nameOf, acNameOf);
            string crewLabel = so.IsActual ? $"✓#{so.SortieNo}" : $"#{so.SortieNo}";
            foreach (var id in Crew(so.PilotId, so.AdditionalPilotIds.Concat(so.SupportPilotIds).Concat(so.EngineerIds)))
                if (crewIndex.TryGetValue(id, out int row))
                    Box(ctx, x, crewTop + row * RowH, so.Block, 1, campColor[so.CampaignId],
                        filled: true, crewLabel, tip, so.Aborted, so);
        }

        foreach (var so in sch.Sorties)
        {
            if (!TryX(so.Date, out double x)) continue;
            int span = Math.Max(1, campById[so.CampaignId].BlocksPerSortie);
            string tip = SortieTip(so, campById, nameOf, acNameOf);

            for (int si = 0; si < so.SupportAircraftIds.Count; si++)
            {
                if (!acIndex.TryGetValue(so.SupportAircraftIds[si], out int srow)) continue;
                string spLabel = si < so.SupportPilotIds.Count
                                 && shortOf.TryGetValue(so.SupportPilotIds[si], out var sp)
                    ? $"{sp} ⊕#{so.SortieNo}"
                    : $"⊕#{so.SortieNo}";
                Box(ctx, x, acTop + srow * RowH, so.Block, span, campColor[so.CampaignId],
                    filled: false, spLabel, tip, aborted: false, sortie: so);
            }

            if (!acIndex.TryGetValue(so.AircraftId, out int row)) continue;
            shortOf.TryGetValue(so.PilotId, out var pilotShort);
            string check = so.IsActual ? "✓" : string.Empty;
            string label = string.IsNullOrEmpty(pilotShort)
                ? $"{check}#{so.SortieNo}"
                : $"{check}{pilotShort} #{so.SortieNo}";
            Box(ctx, x, acTop + row * RowH, so.Block, span, campColor[so.CampaignId], filled: true, label, tip,
                so.Aborted, so);
        }

        foreach (var ss in sch.SimulatorSorties)
        {
            if (!TryX(ss.Date, out double x)) continue;
            int span = Math.Max(1, campById[ss.CampaignId].BlocksPerSortie);
            string tip = SimSortieTip(ss, campById, nameOf, simNameOf);
            string label = $"S#{ss.SortieNo}";
            if (simIndex.TryGetValue(ss.SimulatorId, out int drow))
                Box(ctx, x, simTop + drow * RowH, ss.Block, span, campColor[ss.CampaignId],
                    filled: true, label, tip, ss.Aborted, sim: ss);
            foreach (var id in SimCrew(ss))
                if (crewIndex.TryGetValue(id, out int crow))
                    Box(ctx, x, crewTop + crow * RowH, ss.Block, span, campColor[ss.CampaignId],
                        filled: true, label, tip, ss.Aborted);
        }

        DrawDeadlines(ctx, sc, campColor, AxisH, simBottom);

        var origin = FrozenOrigin();
        DrawFrozenLabelColumn(ctx, origin, aircraft, crew, sims, AxisH, acTop, crewHdr, crewTop, simHdr, simTop, simBottom);
        DrawAxis(ctx, sc, origin);

        DrawTooltip(ctx);
    }

    private void DrawFrozenLabelColumn(DrawingContext ctx, Point origin,
        List<Aircraft> aircraft, List<Resource> crew, List<Simulator> sims,
        double acHdr, double acTop, double crewHdr, double crewTop,
        double simHdr, double simTop, double bottom)
    {
        double x = origin.X;
        ctx.FillRectangle(FrozenBg, new Rect(x, 0, LabelW, Math.Max(bottom + Pad, Bounds.Height)));

        ctx.FillRectangle(new SolidColorBrush(PanelHdrBg), new Rect(x, acHdr, LabelW, PanelHdrH - 4), 3);
        DrawText(ctx, "AIRCRAFT", new Point(x + Pad, acHdr + 3), 11, TextPrimary);
        ctx.FillRectangle(new SolidColorBrush(PanelHdrBg), new Rect(x, crewHdr, LabelW, PanelHdrH - 4), 3);
        DrawText(ctx, "CREW", new Point(x + Pad, crewHdr + 3), 11, TextPrimary);
        if (sims.Count > 0)
        {
            ctx.FillRectangle(new SolidColorBrush(PanelHdrBg), new Rect(x, simHdr, LabelW, PanelHdrH - 4), 3);
            DrawText(ctx, "SIMULATORS", new Point(x + Pad, simHdr + 3), 11, TextPrimary);
        }

        double labelY = (RowH - 16) / 2;
        for (int i = 0; i < aircraft.Count; i++)
            DrawText(ctx, aircraft[i].Name, new Point(x + Pad, acTop + i * RowH + labelY), 12, TextPrimary, LabelW - Pad);
        for (int i = 0; i < crew.Count; i++)
            DrawText(ctx, crew[i].DisplayShort, new Point(x + Pad, crewTop + i * RowH + labelY), 12, TextPrimary, LabelW - Pad);
        for (int i = 0; i < sims.Count; i++)
            DrawText(ctx, sims[i].Name, new Point(x + Pad, simTop + i * RowH + labelY), 12, TextPrimary, LabelW - Pad);
    }

    private static IEnumerable<string> SimCrew(SimulatorSortie ss)
    {
        if (ss.PilotId.Length > 0) yield return ss.PilotId;
        foreach (var p in ss.AdditionalPilotIds) yield return p;
        foreach (var e in ss.EngineerIds) yield return e;
    }

    private string SimSortieTip(SimulatorSortie ss, Dictionary<string, Campaign> campById,
        Dictionary<string, string> nameOf, Dictionary<string, string> simNameOf)
    {
        var camp = campById.GetValueOrDefault(ss.CampaignId);
        string engineers = ss.EngineerIds.Count > 0
            ? string.Join(", ", ss.EngineerIds.Select(id => nameOf.GetValueOrDefault(id, id)))
            : "none";
        string pilot = ss.PilotId.Length > 0
            ? nameOf.GetValueOrDefault(ss.PilotId, ss.PilotId)
                + (ss.AdditionalPilotIds.Count > 0
                    ? " + " + string.Join(", ", ss.AdditionalPilotIds.Select(id => nameOf.GetValueOrDefault(id, id)))
                    : "")
            : "none (FTE-only session)";
        string tip = string.Join('\n',
            $"{camp?.Name ?? ss.CampaignId} · sim session {ss.SortieNo}" + (camp is not null ? $"/{camp.RequiredSorties()}" : ""),
            $"Date: {ss.Date:yyyy-MM-dd (ddd)}",
            $"Block: {BlockNames.Name(ss.Block, _bpd)}",
            $"Simulator: {simNameOf.GetValueOrDefault(ss.SimulatorId, ss.SimulatorId)}",
            $"Pilot: {pilot}",
            $"Engineers: {engineers}",
            $"Test points flown: {ss.TestPointsFlown:0.##}");
        if (ss.Aborted) tip += "\n✕ ABORTED · zero progress, resources spent";
        if (ss.IsActual) tip += "\n✓ ACTUAL · recorded execution (history, not plan)";
        tip += PinTip(ss.CampaignId, ss.SortieNo, ss.IsActual);
        return tip;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        string? tip = null;
        Sortie? over = null;
        SimulatorSortie? overSim = null;
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].rect.Contains(pt)) { (_, tip, over, overSim) = _hits[i]; break; }

        Cursor = (over is not null && SortieClicked is not null) || (overSim is not null && SimulatorSortieClicked is not null)
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;

        if (tip != _hoverTip || (tip != null && pt != _hoverPos))
        {
            _hoverTip = tip;
            _hoverPos = pt;
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && PinRequested is not null)
        {
            var pt = e.GetPosition(this);
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (!_hits[i].rect.Contains(pt)) continue;
                if (_hits[i].sortie is null && _hits[i].sim is null) continue;
                OpenPinMenu(_hits[i].sortie, _hits[i].sim);
                e.Handled = true;
                break;
            }
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var pt = e.GetPosition(this);
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (!_hits[i].rect.Contains(pt)) continue;
                if (_hits[i].sortie is { } so && SortieClicked is not null)
                {
                    SortieClicked.Invoke(this, so); e.Handled = true; break;
                }
                if (_hits[i].sim is { } ss && SimulatorSortieClicked is not null)
                {
                    SimulatorSortieClicked.Invoke(this, ss); e.Handled = true; break;
                }
            }
        }
        base.OnPointerPressed(e);
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

    private string SortieTip(Sortie so, Dictionary<string, Campaign> campById,
        Dictionary<string, string> nameOf, Dictionary<string, string> acNameOf)
    {
        var camp = campById[so.CampaignId];
        string engineers = string.Join(", ", so.EngineerIds.Select(id => nameOf.GetValueOrDefault(id, id)));
        string tip = string.Join('\n',
            $"{camp.Name} · sortie {so.SortieNo}/{camp.RequiredSorties()}",
            $"Date: {so.Date:yyyy-MM-dd (ddd)}",
            $"Block: {BlockNames.Name(so.Block, _bpd)}",
            $"Aircraft: {acNameOf.GetValueOrDefault(so.AircraftId, so.AircraftId)} ({so.AircraftId})",
            $"Pilot: {nameOf.GetValueOrDefault(so.PilotId, so.PilotId)}"
                + (so.AdditionalPilotIds.Count > 0
                    ? " + " + string.Join(", ", so.AdditionalPilotIds.Select(id => nameOf.GetValueOrDefault(id, id)))
                    : ""),
            $"Engineers: {engineers}",
            so.RehearsalDate is { } rd ? $"Rehearsal: {rd:yyyy-MM-dd (ddd)}" : "Rehearsal: none (not required)",
            $"Test points flown: {so.TestPointsFlown:0.##}");
        if (so.SupportAircraftIds.Count > 0)
            tip += "\nSupport: " + string.Join(", ", so.SupportAircraftIds.Select((a, i) =>
                i < so.SupportPilotIds.Count
                    ? $"{a} ({nameOf.GetValueOrDefault(so.SupportPilotIds[i], so.SupportPilotIds[i])})"
                    : a));
        if (so.ExternalSupportIds.Count > 0)
            tip += "\nExternal: " + string.Join(", ",
                so.ExternalSupportIds.Select(id => _extNameOf.GetValueOrDefault(id, id)));
        if (so.Aborted) tip += "\n✕ ABORTED · zero progress, resources spent";
        if (so.IsActual) tip += "\n✓ ACTUAL · recorded execution (history, not plan)";
        tip += PinTip(so.CampaignId, so.SortieNo, so.IsActual);
        return tip;
    }

    private string RehearsalTip(Rehearsal r, Dictionary<string, Campaign> campById,
        Dictionary<string, string> nameOf)
    {
        var camp = campById[r.CampaignId];
        string crew = string.Join(", ", r.CrewIds.Select(id => nameOf.GetValueOrDefault(id, id)));
        string tip = string.Join('\n',
            $"{camp.Name} · rehearsal for sortie {r.SortieNo}/{camp.RequiredSorties()}",
            $"Date: {r.Date:yyyy-MM-dd (ddd)}",
            $"Block: {BlockNames.Name(r.Block, _bpd)}",
            $"For sortie on: {r.SortieDate:yyyy-MM-dd (ddd)}",
            $"Crew: {crew}");
        if (r.IsActual) tip += "\n✓ ACTUAL · recorded execution (history, not plan)";
        return tip;
    }

    private void DrawPanelChrome(DrawingContext ctx, double hdrY, double top, int rows)
    {
        double w = LabelW + _days * DayW;

        ctx.FillRectangle(new SolidColorBrush(PanelHdrBg), new Rect(0, hdrY, w, PanelHdrH - 4), 3);

        double laneH = (RowH - 2 * RowInset) / _bpd;
        for (int r = 0; r < rows; r++)
        {
            double y = top + r * RowH;
            if (r % 2 == 1)
                ctx.FillRectangle(new SolidColorBrush(RowStripe), new Rect(0, y, w, RowH));
            for (int b = 1; b < _bpd; b++)
            {
                double ly = y + RowInset + b * laneH;
                ctx.DrawLine(LanePen, new Point(LabelW, ly), new Point(w, ly));
            }
            ctx.DrawLine(RowPen, new Point(0, y), new Point(w, y));
        }
        ctx.DrawLine(RowPen, new Point(0, top + rows * RowH), new Point(w, top + rows * RowH));
    }

    private void DrawDayShadingAndGrid(DrawingContext ctx, Scenario sc, Schedule sch, double top, double bottom)
    {
        for (int i = 0; i < _days; i++)
        {
            var day = _dayList[i];
            double x = LabelW + i * DayW;

            bool extra = sc.Calendar.ExtraWorkdays.Contains(day);
            Color? shade =
                !extra && sc.Calendar.Holidays.Contains(day) ? GanttPalette.HolidayShade
                : !extra && sc.Calendar.WeekendDays.Contains(day.DayOfWeek) ? GanttPalette.WeekendShade
                : !sc.FlyableModel.IsFlyable(day) ? GanttPalette.NotFlyableShade
                : (Color?)null;

            if (shade is { } c)
                ctx.FillRectangle(new SolidColorBrush(c), new Rect(x, top, DayW, bottom - top));

            if (sc.MasterDeadline is { } master && day > master)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(16, 239, 68, 68)),
                    new Rect(x, top, DayW, bottom - top));

            if (DayTip(sc, sch, day) is { } dayTip)
                _hits.Add((new Rect(x, top, DayW, bottom - top), dayTip, null, null));

            if (day.DayOfWeek == DayOfWeek.Monday)
                ctx.DrawLine(new Pen(new SolidColorBrush(GridColor), 1),
                    new Point(x, top), new Point(x, bottom));
        }
    }

    private static string? DayTip(Scenario sc, Schedule sch, DateOnly day)
    {
        var reasons = new List<string>();
        bool extra = sc.Calendar.ExtraWorkdays.Contains(day);
        if (!extra && sc.Calendar.Holidays.Contains(day))
            reasons.Add("Holiday · not a workday");
        else if (!extra && sc.Calendar.WeekendDays.Contains(day.DayOfWeek))
            reasons.Add($"Weekend ({day.DayOfWeek}) · not a workday");
        if (!sc.FlyableModel.IsFlyable(day))
            reasons.Add("Not flyable · weather/NOTAM model says no");
        bool dayUnusable = reasons.Count > 0;

        var campLines = new List<string>();
        int offset = day.DayNumber - sc.HorizonStart.DayNumber;
        foreach (var camp in sc.Campaigns)
        {
            if (sch.DayLog.GetValueOrDefault(camp.Id) is not { } log
                || offset < 0 || offset >= log.Length) continue;
            char c = log[offset];
            if (!DayLogCodes.IsBlocked(c)) continue;
            if (c == DayLogCodes.NotUsable && dayUnusable) continue;
            string text = c == DayLogCodes.NotUsable
                ? "not flyable at its location"
                : DayLogCodes.Describe(c);
            campLines.Add($"{camp.Name}: {text}");
        }

        if (!dayUnusable && campLines.Count == 0) return null;

        if (extra) reasons.Add("Authorized extra workday (overtime)");
        if (sc.MasterDeadline is { } master && day > master)
            reasons.Add($"Past the master deadline ({master:yyyy-MM-dd})");
        if (sc.FreezeDate is { } freeze && day < freeze)
            reasons.Add($"Frozen history · replanned from {freeze:yyyy-MM-dd}");

        string head = dayUnusable
            ? $"{day:yyyy-MM-dd (ddd)} · no sorties possible"
            : $"{day:yyyy-MM-dd (ddd)} · usable day";
        var lines = reasons.Concat(campLines).ToList();
        return head + "\n• " + string.Join("\n• ", lines);
    }

    private void DrawAxis(DrawingContext ctx, Scenario sc, Point origin)
    {
        double w = Math.Max(LabelW + _days * DayW + Pad * 2, Bounds.Width);
        ctx.FillRectangle(FrozenBg, new Rect(0, origin.Y, w, AxisH - 4));

        double tickBottom = origin.Y + AxisH - 5;
        for (int i = 0; i < _days; i++)
        {
            double x = LabelW + i * DayW;
            if (x < origin.X + LabelW) continue;

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

        if (sc.MasterDeadline is { } master && TryXAtOrBefore(master, out double mx))
        {
            double cx = mx + DayW;
            if (cx >= origin.X + LabelW)
                DrawText(ctx, "master deadline", new Point(cx + 3, origin.Y + 4), 10, MasterDeadlineBrush);
        }

        if (sc.FreezeDate is { } freeze && TryXAtOrAfter(freeze, out double fx) && fx >= origin.X + LabelW)
            DrawText(ctx, "replanned from", new Point(fx + 3, origin.Y + 4), 10, FreezeBrush);
    }

    private static readonly IBrush MasterDeadlineBrush = new SolidColorBrush(Color.Parse("#EF4444"));

    private void DrawDeadlines(DrawingContext ctx, Scenario sc, Dictionary<string, Color> campColor,
        double top, double bottom)
    {
        foreach (var camp in sc.Campaigns)
        {
            if (!TryXAtOrBefore(camp.TargetEndDate, out double x)) continue;
            double cx = x + DayW;
            var pen = new Pen(new SolidColorBrush(campColor[camp.Id]), 1.5, new DashStyle(new double[] { 4, 3 }, 0));
            ctx.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
        }

        if (sc.MasterDeadline is { } master && TryXAtOrBefore(master, out double mx))
        {
            var pen = new Pen(MasterDeadlineBrush, 2.2, new DashStyle(new double[] { 6, 3 }, 0));
            ctx.DrawLine(pen, new Point(mx + DayW, top), new Point(mx + DayW, bottom));
        }

        if (sc.FreezeDate is { } freeze && TryXAtOrAfter(freeze, out double fx))
        {
            var pen = new Pen(FreezeBrush, 2.0, new DashStyle(new double[] { 2, 2 }, 0));
            ctx.DrawLine(pen, new Point(fx, top), new Point(fx, bottom));
        }
    }

    private static readonly IBrush FreezeBrush = new SolidColorBrush(Color.Parse("#3FB950"));

    private static readonly IPen AbortCrossPen = new Pen(new SolidColorBrush(Color.Parse("#EF4444")), 1.6);
    private static readonly IBrush PinFlagBrush = new SolidColorBrush(Color.Parse("#111827"));
    private static readonly IPen PinFlagPen = new Pen(Brushes.White, 0.8);

    private bool IsPinned(Sortie? so, SimulatorSortie? ss) =>
        _pins is { Count: > 0 } pins && !(so?.IsActual ?? ss?.IsActual ?? true)
        && (so is not null ? pins.ContainsKey((so.CampaignId, so.SortieNo))
            : pins.ContainsKey((ss!.CampaignId, ss.SortieNo)));

    private string PinTip(string campaignId, int sortieNo, bool isActual)
    {
        if (isActual || _pins is null || !_pins.TryGetValue((campaignId, sortieNo), out var pin)) return string.Empty;
        var parts = new List<string>();
        if (pin.Date is { } pd) parts.Add($"day {pd:dd.MM ddd}");
        if (!string.IsNullOrEmpty(pin.AircraftId)) parts.Add(pin.AircraftId);
        if (!string.IsNullOrEmpty(pin.SimulatorId)) parts.Add(_simNameOf.GetValueOrDefault(pin.SimulatorId, pin.SimulatorId));
        parts.AddRange(pin.PilotIds.Concat(pin.SupportPilotIds).Concat(pin.EngineerIds)
            .Select(id => _personNameOf.GetValueOrDefault(id, id)));
        return "\n▲ PINNED · " + string.Join(", ", parts);
    }

    private void Box(DrawingContext ctx, double x, double rowY, int block, int span, Color color,
        bool filled, string? label, string tip, bool aborted = false, Sortie? sortie = null, SimulatorSortie? sim = null)
    {
        double laneH = (RowH - 2 * RowInset) / _bpd;
        int b = Math.Clamp(block, 1, _bpd);
        int s = Math.Clamp(span, 1, _bpd - b + 1);

        double top = rowY + RowInset + (b - 1) * laneH;
        var rect = new Rect(x + 1, top + 0.5, DayW - 2, s * laneH - 1);
        if (filled)
        {
            ctx.DrawRectangle(new SolidColorBrush(color), null, rect, 3, 3);
        }
        else
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B)), null, rect, 3, 3);
            ctx.DrawRectangle(null,
                new Pen(new SolidColorBrush(color), 1.2, new DashStyle(new double[] { 3, 2 }, 0)), rect, 3, 3);
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

        if (IsPinned(sortie, sim))
        {
            double f = Math.Min(9, Math.Min(rect.Height, rect.Width) * 0.6);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(rect.TopRight, true);
                g.LineTo(new Point(rect.Right - f, rect.Top));
                g.LineTo(new Point(rect.Right, rect.Top + f));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(PinFlagBrush, PinFlagPen, geo);
        }

        _hits.Add((rect, tip, sortie, sim));
    }

    private static string BandTip(string name, string word, DateRange w)
    {
        string head = string.IsNullOrWhiteSpace(w.Note) ? $"{name} · {word}" : $"{name} · {word}: {w.Note}";
        return $"{head}\n{w.Start:yyyy-MM-dd} … {w.End:yyyy-MM-dd} ({w.Days} day{(w.Days == 1 ? "" : "s")})";
    }

    private void Band(DrawingContext ctx, DateRange window, double rowY, Color fill, IPen outline, string tip)
    {
        if (_days == 0) return;

        int i0 = _dayList.BinarySearch(window.Start);
        if (i0 < 0) i0 = ~i0;
        int i1 = _dayList.BinarySearch(window.End);
        if (i1 < 0) i1 = ~i1 - 1;
        if (i0 >= _days || i1 < i0) return;

        var rect = new Rect(LabelW + i0 * DayW, rowY + 1.5, (i1 - i0 + 1) * DayW, RowH - 3);
        ctx.FillRectangle(new SolidColorBrush(fill), rect);
        ctx.DrawRectangle(null, outline, rect);
        _hits.Add((rect, tip, null, null));
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

    private static double Estimate(string text, double size) => text.Length * size * 0.55;

    private bool TryX(DateOnly day, out double x)
    {
        if (!_dayIndex.TryGetValue(day, out int i)) { x = 0; return false; }
        x = LabelW + i * DayW;
        return true;
    }

    private bool TryXAtOrBefore(DateOnly day, out double x)
    {
        x = 0;
        if (_days == 0 || day < _dayList[0]) return false;
        int i = _dayList.BinarySearch(day);
        if (i < 0) i = ~i - 1;
        if (i < 0) return false;
        x = LabelW + i * DayW;
        return true;
    }

    private bool TryXAtOrAfter(DateOnly day, out double x)
    {
        x = 0;
        if (_days == 0 || day > _dayList[^1]) return false;
        int i = _dayList.BinarySearch(day);
        if (i < 0) i = ~i;
        x = LabelW + i * DayW;
        return true;
    }

    private static IEnumerable<string> Crew(string pilotId, IEnumerable<string> engineers)
    {
        yield return pilotId;
        foreach (var e in engineers) yield return e;
    }

    private static (List<Aircraft> aircraft, List<Resource> crew, List<Simulator> sims) RowsOf(GanttData d)
    {
        var used = new HashSet<string>();
        foreach (var s in d.Schedule.Sorties)
        {
            used.Add(s.PilotId);
            used.UnionWith(s.AdditionalPilotIds);
            used.UnionWith(s.EngineerIds);
        }
        foreach (var r in d.Schedule.Rehearsals) { used.UnionWith(r.CrewIds); }
        foreach (var ss in d.Schedule.SimulatorSorties)
        {
            if (ss.PilotId.Length > 0) used.Add(ss.PilotId);
            used.UnionWith(ss.AdditionalPilotIds);
            used.UnionWith(ss.EngineerIds);
        }

        var crew = d.Scenario.Pilots.Cast<Resource>().Where(p => used.Contains(p.Id))
            .Concat(d.Scenario.Engineers.Where(e => used.Contains(e.Id)))
            .ToList();

        var usedSims = new HashSet<string>(d.Schedule.SimulatorSorties.Select(s => s.SimulatorId));
        usedSims.UnionWith(d.Schedule.SimulatorRepairs.Keys);
        var sims = d.Scenario.Simulators.Where(s => usedSims.Contains(s.Id)).ToList();

        return (d.Scenario.Aircraft.ToList(), crew, sims);
    }
}

public enum GanttPinAction { Pin, PinDay, EditPin, Unpin, PinCampaign, UnpinCampaign, PinUpToDate, ClearAll }

public sealed record GanttPinRequest(GanttPinAction Action, Sortie? Sortie, SimulatorSortie? SimSortie);
