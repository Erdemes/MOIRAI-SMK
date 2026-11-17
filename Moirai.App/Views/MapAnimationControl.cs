using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public sealed class MapAnimationControl : Control
{
    private const double BaseLon = 32.565, BaseLat = 40.078;

    private const double GlyphNose = 0;

    public static readonly StyledProperty<GanttData?> DataProperty =
        AvaloniaProperty.Register<MapAnimationControl, GanttData?>(nameof(Data));

    public GanttData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly StyledProperty<double> CurrentBlockProperty =
        AvaloniaProperty.Register<MapAnimationControl, double>(nameof(CurrentBlock));

    public double CurrentBlock
    {
        get => GetValue(CurrentBlockProperty);
        set => SetValue(CurrentBlockProperty, value);
    }

    static MapAnimationControl()
    {
        AffectsRender<MapAnimationControl>(DataProperty, CurrentBlockProperty);
    }

    private static readonly Color SeaNight = Color.Parse("#070C18");
    private static readonly Color SeaDay = Color.Parse("#21344E");
    private static readonly Color LandNight = Color.Parse("#18212C");
    private static readonly Color LandDay = Color.Parse("#34434F");
    private static readonly Color Overcast = Color.Parse("#3A3F46");

    private static readonly IBrush NodeLabel = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush LocLabel = new SolidColorBrush(Color.Parse("#C2CAD6"));
    private static readonly Color TailColor = Color.Parse("#D2D9E4");
    private static readonly IBrush StandDown = new SolidColorBrush(Color.Parse("#C7CDD6"));
    private static readonly Color Accent = Color.Parse("#8B78FF");
    private static readonly IPen FerryPen = new Pen(new SolidColorBrush(Color.Parse("#7E8AA6")), 1.3,
        new DashStyle(new double[] { 5, 5 }, 0));

    private StreamGeometry? _geo;
    private Size _geoSize;

    public override void Render(DrawingContext ctx)
    {
        var data = Data;
        if (data is null) return;
        var sc = data.Scenario;
        var sch = data.Schedule;
        double W = Bounds.Width, H = Bounds.Height;
        if (W < 20 || H < 20) return;

        var map = TurkeyMap.Load();

        int bpd = Math.Max(1, sc.Config.BlocksPerDay);
        int days = Math.Max(1, sc.HorizonEnd.DayNumber - sc.HorizonStart.DayNumber + 1);
        int totalBlocks = days * bpd;
        double cb = Math.Clamp(CurrentBlock, 0, totalBlocks - 1);
        int blk = (int)cb;
        int dayIdx = blk / bpd, inDay = blk % bpd;
        double frac = cb - blk;
        var date = sc.HorizonStart.AddDays(dayIdx);
        double dayProgress = (inDay + frac) / bpd;
        double sun = Math.Sin(Math.PI * dayProgress);
        bool usable = sc.Calendar.IsWorkday(date) && sc.FlyableModel.IsFlyable(date);

        const double pad = 18;
        double midLat = (map.LatMin + map.LatMax) / 2;
        double kx = Math.Cos(midLat * Math.PI / 180);
        double geoW = (map.LonMax - map.LonMin) * kx, geoH = map.LatMax - map.LatMin;
        double scale = Math.Min((W - 2 * pad) / geoW, (H - 2 * pad) / geoH);
        double offX = (W - geoW * scale) / 2, offY = (H - geoH * scale) / 2;
        Point P(double lon, double lat) =>
            new(offX + (lon - map.LonMin) * kx * scale, offY + (map.LatMax - lat) * scale);

        Color sea = LerpC(SeaNight, SeaDay, sun), land = LerpC(LandNight, LandDay, sun);
        if (!usable) { sea = LerpC(sea, Overcast, 0.45); land = LerpC(land, Overcast, 0.45); }

        ctx.FillRectangle(new SolidColorBrush(sea), new Rect(0, 0, W, H));

        if (_geo is null || _geoSize != Bounds.Size)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
                foreach (var prov in map.Provinces)
                    foreach (var ring in prov.Rings)
                    {
                        c.BeginFigure(P(ring[0].Lon, ring[0].Lat), true);
                        for (int i = 1; i < ring.Length; i++) c.LineTo(P(ring[i].Lon, ring[i].Lat));
                        c.EndFigure(true);
                    }
            _geo = g;
            _geoSize = Bounds.Size;
        }
        var borderPen = new Pen(new SolidColorBrush(Color.Parse("#8893A6"), 0.45), 0.7);
        ctx.DrawGeometry(new SolidColorBrush(land), borderPen, _geo);

        var locs = sc.Locations;
        int nNodes = 1 + locs.Count;
        var pos = new Point[nNodes];
        pos[0] = P(BaseLon, BaseLat);
        var country = P((map.LonMin + map.LonMax) / 2, (map.LatMin + map.LatMax) / 2);
        for (int i = 0; i < locs.Count; i++)
        {
            var loc = locs[i];
            if (loc.Latitude is { } la && loc.Longitude is { } lo)
                pos[i + 1] = P(lo, la);
            else
            {
                var c = map.Locate(loc.Name);
                pos[i + 1] = c is { } ll ? P(ll.Lon, ll.Lat) : country;
            }
        }

        int NodeOf(string locId)
        {
            if (string.IsNullOrEmpty(locId)) return 0;
            int idx = locs.FindIndex(l => l.Id == locId);
            return idx < 0 ? 0 : idx + 1;
        }

        foreach (var dep in sch.Deployments)
        {
            int n = NodeOf(dep.LocationId);
            if (n != 0) ctx.DrawLine(FerryPen, pos[0], pos[n]);
        }

        var campColors = GanttPalette.CampaignColors(sc);
        var pilotById = sc.Pilots.ToDictionary(p => p.Id, p => p.DisplayShort);
        var acName = sc.Aircraft.ToDictionary(a => a.Id, a => a.Name);
        var flying = new Dictionary<string, Color>();
        var pilotOf = new Dictionary<string, string>();
        var support = new HashSet<string>();
        var areaSeedOf = new Dictionary<string, int>();
        var formIndexOf = new Dictionary<string, int>();
        foreach (var so in sch.Sorties)
        {
            if (so.Date != date || so.Block != inDay + 1) continue;
            var col = campColors.TryGetValue(so.CampaignId, out var c) ? c : Colors.White;
            flying[so.AircraftId] = col;
            if (pilotById.TryGetValue(so.PilotId, out var ps)) pilotOf[so.AircraftId] = ps;
            int areaSeed = so.AircraftId.GetHashCode() ^ (dayIdx * 73856093) ^ (inDay * 19349663);
            areaSeedOf[so.AircraftId] = areaSeed;
            formIndexOf[so.AircraftId] = 0;
            for (int si = 0; si < so.SupportAircraftIds.Count; si++)
            {
                string stail = so.SupportAircraftIds[si];
                flying[stail] = col;
                support.Add(stail);
                areaSeedOf[stail] = areaSeed;
                formIndexOf[stail] = si + 1;
                if (si < so.SupportPilotIds.Count && pilotById.TryGetValue(so.SupportPilotIds[si], out var sps))
                    pilotOf[stail] = sps;
            }
        }

        var down = new HashSet<string>();
        foreach (var ac in sc.Aircraft)
        {
            bool isDown = ac.IsInMaintenance(date)
                || (sch.AutoMaintenance.TryGetValue(ac.Id, out var am) && am.Any(w => date >= w.Start && date <= w.End))
                || (sch.UnscheduledRepairs.TryGetValue(ac.Id, out var ur) && ur.Any(w => date >= w.Start && date <= w.End));
            if (isDown) down.Add(ac.Id);
        }

        double dayCont = dayIdx + dayProgress;
        var parkedByNode = new List<string>[nNodes];
        for (int i = 0; i < nNodes; i++) parkedByNode[i] = new List<string>();
        var ferryDaysOf = locs.ToDictionary(l => l.Id, l => Math.Max(1, l.FerryDays));
        var ferrying = new List<(string Tail, int Node, double T, bool Outbound)>();

        foreach (var ac in sc.Aircraft)
        {
            string tail = ac.Id;
            int node = 0;
            double? ferryT = null;
            bool outbound = true;
            foreach (var dep in sch.Deployments)
            {
                if (!dep.AircraftIds.Contains(tail)) continue;
                int departIdx = dep.Departure.DayNumber - sc.HorizonStart.DayNumber;
                int flyIdx = dep.FlyFrom.DayNumber - sc.HorizonStart.DayNumber;
                int retIdx = (dep.ReturnEnd ?? sc.HorizonEnd).DayNumber - sc.HorizonStart.DayNumber;
                if (dayCont < departIdx || dayCont > retIdx) continue;
                node = NodeOf(dep.LocationId);

                int ferryDays = ferryDaysOf.GetValueOrDefault(dep.LocationId, 1);
                int retStartIdx = retIdx - ferryDays;
                if (dayCont < flyIdx && flyIdx > departIdx)
                {
                    ferryT = Math.Clamp((dayCont - departIdx) / (flyIdx - departIdx), 0, 1);
                    outbound = true;
                }
                else if (dep.ReturnEnd is not null && retStartIdx > flyIdx && dayCont >= retStartIdx)
                {
                    ferryT = Math.Clamp((dayCont - retStartIdx) / (double)ferryDays, 0, 1);
                    outbound = false;
                }
                break;
            }
            if (ferryT is { } t) ferrying.Add((tail, node, t, outbound));
            else parkedByNode[node].Add(tail);
        }

        var transit = new List<(string Tail, Point P, double Heading)>();
        foreach (var grp in ferrying.GroupBy(e => (e.Node, e.Outbound)))
        {
            var list = grp.ToList();
            Point a = grp.Key.Outbound ? pos[0] : pos[grp.Key.Node];
            Point b = grp.Key.Outbound ? pos[grp.Key.Node] : pos[0];
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double nx = len > 0 ? -dy / len : 0, ny = len > 0 ? dx / len : 0;
            double heading = Math.Atan2(dy, dx);
            const double spacing = 30;
            for (int k = 0; k < list.Count; k++)
            {
                double centered = k - (list.Count - 1) / 2.0;
                double tt = Math.Clamp(list[k].T + centered * 0.012, 0, 1);
                Point bp = Lerp(a, b, tt);
                transit.Add((list[k].Tail, new Point(bp.X + nx * centered * spacing, bp.Y + ny * centered * spacing), heading));
            }
        }

        DrawBaseMarker(ctx, pos[0]);
        for (int i = 0; i < locs.Count; i++) DrawLocMarker(ctx, pos[i + 1], locs[i].Name);

        for (int i = 0; i < nNodes; i++)
        {
            var list = parkedByNode[i];
            for (int k = 0; k < list.Count; k++)
            {
                string t = list[k];
                int seed = areaSeedOf.TryGetValue(t, out var sd)
                    ? sd : t.GetHashCode() ^ (dayIdx * 73856093) ^ (inDay * 19349663);
                int form = formIndexOf.GetValueOrDefault(t);
                DrawTail(ctx, CascadePos(pos[i], k), t, flying, support, down, pilotOf, acName,
                    frac, parked: true, sortieSeed: seed, formationIndex: form);
            }
        }
        foreach (var (tail, p, heading) in transit)
            DrawTail(ctx, p, tail, flying, support, down, pilotOf, acName, frac, parked: false, ferryHeading: heading);

        DrawSunMoon(ctx, W, H, dayProgress, sun);
        if (!usable)
            Text(ctx, $"STAND-DOWN · {StandDownReason(sc, date)}", new Point(W * 0.5, 18), 13, StandDown, center: true);
    }

    private static string StandDownReason(Scheduling.Scenario sc, DateOnly date)
    {
        var reasons = new List<string>();
        bool extra = sc.Calendar.ExtraWorkdays.Contains(date);
        if (!extra && sc.Calendar.Holidays.Contains(date))
            reasons.Add("holiday");
        else if (!extra && sc.Calendar.WeekendDays.Contains(date.DayOfWeek))
            reasons.Add($"weekend ({date.DayOfWeek})");
        if (!sc.FlyableModel.IsFlyable(date))
            reasons.Add("not flyable (weather/NOTAM)");
        return reasons.Count > 0 ? string.Join(" + ", reasons) : "not a usable day";
    }

    private static void DrawBaseMarker(DrawingContext ctx, Point p)
    {
        ctx.DrawEllipse(new SolidColorBrush(Accent, 0.30), null, p, 12, 12);
        ctx.DrawEllipse(new SolidColorBrush(Accent), new Pen(Brushes.White, 1.2), p, 5, 5);
        Text(ctx, "MAIN BASE", new Point(p.X, p.Y - 24), 12, NodeLabel, center: true);
    }

    private static void DrawLocMarker(DrawingContext ctx, Point p, string name)
    {
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#9AA4B2")), new Pen(Brushes.White, 1), p, 4, 4);
        Text(ctx, name, new Point(p.X, p.Y - 20), 11.5, LocLabel, center: true);
    }

    private static void DrawTail(DrawingContext ctx, Point p, string tail,
        Dictionary<string, Color> flying, HashSet<string> support, HashSet<string> down,
        Dictionary<string, string> pilotOf, Dictionary<string, string> acName, double frac, bool parked,
        double? ferryHeading = null, int sortieSeed = 0, int formationIndex = 0)
    {
        if (flying.TryGetValue(tail, out var col))
        {
            double bearing = Hash01((uint)sortieSeed) * 2 * Math.PI;
            double dist = 72 + Hash01((uint)sortieSeed ^ 0x9E3779B9u) * 70;
            var area = new Point(p.X + dist * Math.Cos(bearing), p.Y + dist * Math.Sin(bearing));
            double phase = formationIndex * 0.9;
            double rDelta = formationIndex * 7;

            var pp = SortiePath(p, area, frac, phase, rDelta);

            Point? prev = null;
            for (int j = 16; j >= 0; j--)
            {
                double s = frac - 0.5 * j / 16.0;
                if (s < 0) { prev = null; continue; }
                var q = SortiePath(p, area, s, phase, rDelta);
                if (prev is { } pr)
                    ctx.DrawLine(new Pen(new SolidColorBrush(col, 0.5 * (1 - j / 16.0)), 1.6), pr, q);
                prev = q;
            }

            double env = Math.Sin(Math.PI * frac);
            ctx.DrawEllipse(new SolidColorBrush(col, 0.22), null, pp, 9 + 4 * env, 9 + 4 * env);

            if (support.Contains(tail))
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(col), 1.2,
                    new DashStyle(new double[] { 2, 2 }, 0)), pp, 12, 12);

            var ahead = SortiePath(p, area, Math.Min(1, frac + 0.01), phase, rDelta);
            double heading = Math.Atan2(ahead.Y - pp.Y, ahead.X - pp.X);
            var m = Matrix.CreateTranslation(-pp.X, -pp.Y)
                    * Matrix.CreateRotation(heading - GlyphNose)
                    * Matrix.CreateTranslation(pp.X, pp.Y);
            using (ctx.PushTransform(m))
                Text(ctx, "✈", new Point(pp.X, pp.Y - 9), 16, new SolidColorBrush(col), center: true);

            string nm = acName.TryGetValue(tail, out var n) ? n : tail;
            Text(ctx, nm, new Point(pp.X, pp.Y + 11), 9.5, new SolidColorBrush(col), center: true);
            if (pilotOf.TryGetValue(tail, out var ps))
                Text(ctx, ps, new Point(pp.X, pp.Y - 30), 9.5, new SolidColorBrush(col), center: true);
            return;
        }

        double op = down.Contains(tail) ? 0.32 : 1.0;
        string name = acName.TryGetValue(tail, out var nn) ? nn : tail;

        if (ferryHeading is { } hdg)
        {
            var m = Matrix.CreateTranslation(-p.X, -p.Y)
                    * Matrix.CreateRotation(hdg - GlyphNose)
                    * Matrix.CreateTranslation(p.X, p.Y);
            using (ctx.PushTransform(m))
                Text(ctx, "✈", new Point(p.X, p.Y - 9), 15, new SolidColorBrush(TailColor, op), center: true);
            Text(ctx, name, new Point(p.X, p.Y + 9), 9.5, new SolidColorBrush(TailColor, op), center: true);
            return;
        }

        Text(ctx, "✈", new Point(p.X - 7, p.Y - 9), 15, new SolidColorBrush(TailColor, op));
        Text(ctx, name, new Point(p.X + 7, p.Y - 8), 9.5, new SolidColorBrush(TailColor, op));
    }

    private const double OutEnd = 0.22, BackStart = 0.78, LoiterR = 18;
    private const int LoiterLoops = 2;

    private static Point SortiePath(Point home, Point area, double s, double phase = 0, double radiusDelta = 0)
    {
        double r = LoiterR + radiusDelta;
        var entry = new Point(area.X + r * Math.Cos(phase), area.Y + r * Math.Sin(phase));
        if (s <= OutEnd) return Lerp(home, entry, Ease(s / OutEnd));
        if (s >= BackStart) return Lerp(entry, home, Ease((s - BackStart) / (1 - BackStart)));
        double u = (s - OutEnd) / (BackStart - OutEnd);
        double ang = phase + 2 * Math.PI * LoiterLoops * u;
        return new Point(area.X + r * Math.Cos(ang), area.Y + r * Math.Sin(ang));
    }

    private static double Ease(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    private static double Hash01(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return (x & 0xFFFFFF) / (double)0x1000000;
    }

    private static void DrawSunMoon(DrawingContext ctx, double W, double H, double dayProgress, double sun)
    {
        double x = W * (0.10 + 0.80 * dayProgress);
        double y = H * 0.13 - sun * H * 0.06;
        if (sun > 0.12)
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#FFD27A"), Math.Clamp(sun, 0, 1)), null,
                new Point(x, y), 12, 12);
        else
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#C9D2E0"), 0.8), null, new Point(x, y), 9, 9);
    }

    private static Point CascadePos(Point center, int k)
    {
        const double dy = 16, stagger = 5;
        return new Point(center.X + k * stagger, center.Y + 18 + k * dy);
    }

    private static Point Lerp(Point a, Point b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static Color LerpC(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static void Text(DrawingContext ctx, string s, Point at, double size, IBrush brush, bool center = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        if (center) at = new Point(at.X - ft.Width / 2, at.Y);
        ctx.DrawText(ft, at);
    }
}
