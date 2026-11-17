using System.Globalization;
using System.Text;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Reporting;

internal static class ReportCharts
{
    private static readonly string[] Palette =
    {
        "#5B8DEF", "#F25C54", "#2EC4B6", "#FFBF1F", "#B388FF",
        "#7AC74F", "#FF7AB6", "#FF8C42", "#4DD0E1", "#D4E157",
    };

    private const string Ink = "#1A1F27", Muted = "#5A636F", Grid = "#DFE3EA", GridStrong = "#B9C0CB";
    private const string WeekendFill = "#F1F3F6", NotFlyableFill = "#FDEBEC";
    private const string MaintFill = "#94A3B8", LeaveFill = "#EAB308", RepairFill = "#EF4444";
    private const string Font = "Arial";

    public static string AircraftGantt(Scenario sc, Schedule sch, float w, float h) =>
        BuildGantt(sc, sch, w, h, personnel: false);

    public static string PersonnelGantt(Scenario sc, Schedule sch, float w, float h) =>
        BuildGantt(sc, sch, w, h, personnel: true);

    private sealed class Geo
    {
        public required List<DateOnly> Days { get; init; }
        public required float LabelW { get; init; }
        public required float PlotX { get; init; }
        public required float PlotY { get; init; }
        public required float PlotW { get; init; }
        public required float DayW { get; init; }
        public float X(int dayIdx) => PlotX + dayIdx * DayW;
    }

    private static List<DateOnly> HorizonDays(Scenario sc)
    {
        var days = new List<DateOnly>();
        for (var d = sc.HorizonStart; d <= sc.HorizonEnd; d = d.AddDays(1)) days.Add(d);
        return days;
    }

    private static int Idx(Scenario sc, DateOnly d) => d.DayNumber - sc.HorizonStart.DayNumber;

    private static Dictionary<string, string> CampaignColors(Scenario sc) =>
        sc.Campaigns.Select((c, i) => (c.Id, Color: Palette[i % Palette.Length]))
                    .ToDictionary(t => t.Id, t => t.Color);

    private static string BuildGantt(Scenario sc, Schedule sch, float w, float h, bool personnel)
    {
        var days = HorizonDays(sc);
        var colors = CampaignColors(sc);
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);
        int bpd = Math.Max(1, sc.Config.BlocksPerDay);

        var rows = personnel ? PersonnelRows(sc, sch) : AircraftRows(sc, sch);
        var svg = new StringBuilder();
        Open(svg, w, h);

        if (rows.Count == 0 || days.Count == 0)
        {
            Text(svg, w / 2, h / 2, "No scheduled activity to chart.", 11, Muted, anchor: "middle");
            svg.Append("</svg>");
            return svg.ToString();
        }

        const float labelW = 104f, axisH = 26f, legendH = 34f, pad = 4f;
        float plotW = Math.Max(40f, w - labelW - pad * 2);
        float plotY = axisH + pad;
        float plotH = Math.Max(40f, h - axisH - legendH - pad * 3);
        float rowH = Math.Clamp(plotH / rows.Count, 7f, 46f);
        plotH = rowH * rows.Count;

        var geo = new Geo
        {
            Days = days, LabelW = labelW, PlotX = labelW + pad, PlotY = plotY,
            PlotW = plotW, DayW = plotW / days.Count,
        };

        DayShading(svg, sc, geo, plotY, plotH);
        RowBands(svg, sc, sch, geo, rows, rowH, plotY, personnel);
        GridAndAxis(svg, sc, geo, plotY, plotH);

        for (int r = 0; r < rows.Count; r++)
        {
            float y = plotY + r * rowH;
            svg.Append($"<line x1=\"{F(pad)}\" y1=\"{F(y)}\" x2=\"{F(geo.PlotX + plotW)}\" y2=\"{F(y)}\" stroke=\"{Grid}\" stroke-width=\"0.5\"/>");
            Text(svg, pad + 1, y + rowH / 2 + 3, Clip(rows[r].Label, 20), Math.Min(8f, rowH * 0.5f), Ink);
        }
        svg.Append($"<line x1=\"{F(pad)}\" y1=\"{F(plotY + plotH)}\" x2=\"{F(geo.PlotX + plotW)}\" y2=\"{F(plotY + plotH)}\" stroke=\"{GridStrong}\" stroke-width=\"0.7\"/>");

        var rowIndex = rows.Select((r, i) => (r.Key, i)).ToDictionary(t => t.Key, t => t.i);
        if (personnel) PersonnelBlocks(svg, sc, sch, geo, rowIndex, rowH, plotY, bpd, colors);
        else AircraftBlocks(svg, sc, sch, geo, rowIndex, rowH, plotY, bpd, colors);

        DeadlineLines(svg, sc, geo, plotY, plotH);
        Legend(svg, sc, colors, campName, pad, plotY + plotH + 12, w, personnel);

        svg.Append("</svg>");
        return svg.ToString();
    }

    private sealed record Row(string Key, string Label);

    private static List<Row> AircraftRows(Scenario sc, Schedule sch)
    {
        var used = new HashSet<string>(sch.Sorties.SelectMany(s => s.SupportAircraftIds.Prepend(s.AircraftId)));
        foreach (var a in sc.Aircraft)
            if (a.MaintenanceWindows.Count > 0 || sch.AutoMaintenance.ContainsKey(a.Id)
                || sch.UnscheduledRepairs.ContainsKey(a.Id)) used.Add(a.Id);
        return sc.Aircraft.Where(a => used.Contains(a.Id))
                 .Select(a => new Row(a.Id, a.Name.Length > 0 ? a.Name : a.Id)).ToList();
    }

    private static List<Row> PersonnelRows(Scenario sc, Schedule sch)
    {
        var active = new HashSet<string>();
        foreach (var s in sch.Sorties)
        {
            active.Add(s.PilotId);
            foreach (var p in s.AdditionalPilotIds) active.Add(p);
            foreach (var p in s.SupportPilotIds) active.Add(p);
            foreach (var e in s.EngineerIds) active.Add(e);
        }
        foreach (var r in sch.Rehearsals)
        {
            active.UnionWith(r.CrewIds);
        }
        foreach (var s in sch.SimulatorSorties)
        {
            if (s.PilotId.Length > 0) active.Add(s.PilotId);
            foreach (var p in s.AdditionalPilotIds) active.Add(p);
            foreach (var e in s.EngineerIds) active.Add(e);
        }
        return sc.Pilots.Cast<Resource>().Concat(sc.Engineers)
                 .Where(p => active.Contains(p.Id))
                 .Select(p => new Row(p.Id, p.DisplayShort.Length > 0 ? p.DisplayShort : p.Name))
                 .ToList();
    }

    private static void DayShading(StringBuilder svg, Scenario sc, Geo geo, float y, float hgt)
    {
        for (int i = 0; i < geo.Days.Count; i++)
        {
            var d = geo.Days[i];
            bool work = sc.Calendar.IsWorkday(d);
            bool fly = sc.FlyableModel.IsFlyable(d);
            if (work && fly) continue;
            string fill = !work ? WeekendFill : NotFlyableFill;
            svg.Append($"<rect x=\"{F(geo.X(i))}\" y=\"{F(y)}\" width=\"{F(geo.DayW)}\" height=\"{F(hgt)}\" fill=\"{fill}\"/>");
        }
    }

    private static void RowBands(StringBuilder svg, Scenario sc, Schedule sch, Geo geo,
        List<Row> rows, float rowH, float plotY, bool personnel)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            float y = plotY + r * rowH;
            if (personnel)
            {
                var person = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).FirstOrDefault(p => p.Id == rows[r].Key);
                if (person is null) continue;
                foreach (var wnd in person.LeaveWindows) Band(svg, sc, geo, wnd, y, rowH, LeaveFill, 0.30f);
            }
            else
            {
                var ac = sc.Aircraft.FirstOrDefault(a => a.Id == rows[r].Key);
                if (ac is null) continue;
                foreach (var wnd in ac.MaintenanceWindows) Band(svg, sc, geo, wnd, y, rowH, MaintFill, 0.38f);
                if (sch.AutoMaintenance.TryGetValue(ac.Id, out var auto))
                    foreach (var wnd in auto) Band(svg, sc, geo, wnd, y, rowH, MaintFill, 0.30f);
                if (sch.UnscheduledRepairs.TryGetValue(ac.Id, out var rep))
                    foreach (var wnd in rep) Band(svg, sc, geo, wnd, y, rowH, RepairFill, 0.28f);
            }
        }
    }

    private static void Band(StringBuilder svg, Scenario sc, Geo geo, DateRange wnd,
        float y, float rowH, string fill, float opacity)
    {
        int a = Math.Max(0, Idx(sc, wnd.Start));
        int b = Math.Min(geo.Days.Count - 1, Idx(sc, wnd.End));
        if (b < a) return;
        svg.Append($"<rect x=\"{F(geo.X(a))}\" y=\"{F(y + 0.5f)}\" width=\"{F((b - a + 1) * geo.DayW)}\" " +
                   $"height=\"{F(rowH - 1)}\" fill=\"{fill}\" fill-opacity=\"{F(opacity)}\"/>");
    }

    private static void GridAndAxis(StringBuilder svg, Scenario sc, Geo geo, float plotY, float plotH)
    {
        for (int i = 0; i < geo.Days.Count; i++)
        {
            var d = geo.Days[i];
            if (d.Day == 1)
                svg.Append($"<line x1=\"{F(geo.X(i))}\" y1=\"{F(plotY)}\" x2=\"{F(geo.X(i))}\" y2=\"{F(plotY + plotH)}\" stroke=\"{GridStrong}\" stroke-width=\"0.7\"/>");
            else if (geo.DayW >= 6 && d.DayOfWeek == DayOfWeek.Monday)
                svg.Append($"<line x1=\"{F(geo.X(i))}\" y1=\"{F(plotY)}\" x2=\"{F(geo.X(i))}\" y2=\"{F(plotY + plotH)}\" stroke=\"{Grid}\" stroke-width=\"0.4\"/>");
        }

        const float minGap = 34f;
        var placed = new List<int>();
        for (int i = 0; i < geo.Days.Count; i++)
            if (geo.Days[i].Day == 1) placed.Add(i);

        int step = Math.Max(1, (int)Math.Ceiling(minGap / Math.Max(0.01f, geo.DayW)));
        for (int i = 0; i < geo.Days.Count; i += step)
            if (placed.TrueForAll(p => Math.Abs(geo.X(p) - geo.X(i)) >= minGap)) placed.Add(i);

        foreach (int i in placed.OrderBy(x => x))
        {
            var d = geo.Days[i];
            bool monthStart = d.Day == 1;
            Text(svg, geo.X(i) + 1, plotY - 12, d.ToString("dd MMM", CultureInfo.InvariantCulture), 6.5f,
                 monthStart ? Ink : Muted, anchor: "start");
            svg.Append($"<line x1=\"{F(geo.X(i))}\" y1=\"{F(plotY - 8)}\" x2=\"{F(geo.X(i))}\" y2=\"{F(plotY)}\" stroke=\"{GridStrong}\" stroke-width=\"0.5\"/>");
        }
        Text(svg, geo.PlotX, plotY - 22, geo.Days[0].Year == geo.Days[^1].Year
            ? geo.Days[0].Year.ToString()
            : $"{geo.Days[0].Year}-{geo.Days[^1].Year}", 7f, Muted);
    }

    private static void DeadlineLines(StringBuilder svg, Scenario sc, Geo geo, float plotY, float plotH)
    {
        if (sc.MasterDeadline is { } m)
        {
            int i = Idx(sc, m);
            if (i >= 0 && i < geo.Days.Count)
            {
                svg.Append($"<line x1=\"{F(geo.X(i))}\" y1=\"{F(plotY)}\" x2=\"{F(geo.X(i))}\" y2=\"{F(plotY + plotH)}\" " +
                           $"stroke=\"#C03A33\" stroke-width=\"1.2\" stroke-dasharray=\"4,2\"/>");
                Text(svg, geo.X(i) + 2, plotY + 8, "master", 6.5f, "#C03A33");
            }
        }
    }

    private static void AircraftBlocks(StringBuilder svg, Scenario sc, Schedule sch, Geo geo,
        Dictionary<string, int> rowIndex, float rowH, float plotY, int bpd, Dictionary<string, string> colors)
    {
        float laneH = (rowH - 2) / bpd;
        foreach (var s in sch.Sorties)
        {
            int di = Idx(sc, s.Date);
            if (di < 0 || di >= geo.Days.Count) continue;
            var camp = sc.Campaigns.FirstOrDefault(c => c.Id == s.CampaignId);
            int span = Math.Clamp(camp?.BlocksPerSortie ?? 1, 1, bpd);
            string color = colors.GetValueOrDefault(s.CampaignId, "#888888");

            foreach (var (tail, isSupport) in s.SupportAircraftIds.Select(t => (t, true)).Prepend((s.AircraftId, false)))
            {
                if (!rowIndex.TryGetValue(tail, out int r)) continue;
                float y = plotY + r * rowH + 1 + (Math.Clamp(s.Block, 1, bpd) - 1) * laneH;
                Block(svg, geo.X(di), y, geo.DayW, laneH * span, color, isSupport, s.Aborted);
            }
        }
    }

    private static void PersonnelBlocks(StringBuilder svg, Scenario sc, Schedule sch, Geo geo,
        Dictionary<string, int> rowIndex, float rowH, float plotY, int bpd, Dictionary<string, string> colors)
    {
        float laneH = (rowH - 2) / bpd;

        void Draw(string personId, DateOnly date, int block, int span, string campId, bool hollow, bool aborted)
        {
            int di = Idx(sc, date);
            if (di < 0 || di >= geo.Days.Count) return;
            if (!rowIndex.TryGetValue(personId, out int r)) return;
            float y = plotY + r * rowH + 1 + (Math.Clamp(block, 1, bpd) - 1) * laneH;
            Block(svg, geo.X(di), y, geo.DayW, laneH * span, colors.GetValueOrDefault(campId, "#888888"), hollow, aborted);
        }

        foreach (var r in sch.Rehearsals)
            foreach (var p in r.CrewIds)
                Draw(p, r.Date, r.Block, 1, r.CampaignId, hollow: true, aborted: false);

        foreach (var s in sch.Sorties)
        {
            var camp = sc.Campaigns.FirstOrDefault(c => c.Id == s.CampaignId);
            int span = Math.Clamp(camp?.BlocksPerSortie ?? 1, 1, bpd);
            foreach (var p in s.EngineerIds.Concat(s.SupportPilotIds).Concat(s.AdditionalPilotIds).Prepend(s.PilotId))
                Draw(p, s.Date, s.Block, 1, s.CampaignId, hollow: false, aborted: s.Aborted);
            _ = span;
        }

        foreach (var s in sch.SimulatorSorties)
        {
            var camp = sc.Campaigns.FirstOrDefault(c => c.Id == s.CampaignId);
            int span = Math.Clamp(camp?.BlocksPerSortie ?? 1, 1, bpd);
            var crew = s.EngineerIds.Concat(s.AdditionalPilotIds).ToList();
            if (s.PilotId.Length > 0) crew.Insert(0, s.PilotId);
            foreach (var p in crew) Draw(p, s.Date, s.Block, span, s.CampaignId, hollow: false, aborted: s.Aborted);
        }
    }

    private static void Block(StringBuilder svg, float x, float y, float w, float h,
        string color, bool hollow, bool aborted)
    {
        float bw = Math.Max(0.9f, w - 0.4f);
        float bh = Math.Max(1.2f, h - 0.6f);
        if (hollow)
            svg.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" fill=\"{color}\" " +
                       $"fill-opacity=\"0.22\" stroke=\"{color}\" stroke-width=\"0.5\" stroke-dasharray=\"1.5,1\"/>");
        else
            svg.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" fill=\"{color}\" " +
                       $"stroke=\"#FFFFFF\" stroke-width=\"0.25\"/>");
        if (aborted)
            svg.Append($"<line x1=\"{F(x)}\" y1=\"{F(y)}\" x2=\"{F(x + bw)}\" y2=\"{F(y + bh)}\" stroke=\"#C03A33\" stroke-width=\"0.7\"/>");
    }

    private static void Legend(StringBuilder svg, Scenario sc, Dictionary<string, string> colors,
        Dictionary<string, string> campName, float x0, float y, float w, bool personnel)
    {
        var items = new List<(string Label, string Color, float Opacity, bool Hollow)>
        {
            ("Weekend / holiday", WeekendFill, 1f, false),
            ("Not flyable", NotFlyableFill, 1f, false),
            personnel ? ("Leave", LeaveFill, 0.30f, false) : ("Maintenance", MaintFill, 0.38f, false),
        };
        if (personnel) items.Add(("Rehearsal", "#5A636F", 0.22f, true));
        foreach (var c in sc.Campaigns)
            items.Add((campName.GetValueOrDefault(c.Id, c.Id), colors.GetValueOrDefault(c.Id, "#888888"), 1f, false));

        float x = x0, lineY = y;
        foreach (var (label, color, opacity, hollow) in items)
        {
            string txt = Clip(label, 26);
            float itemW = 12 + txt.Length * 3.9f + 10;
            if (x + itemW > w - 4) { x = x0; lineY += 12; }
            svg.Append($"<rect x=\"{F(x)}\" y=\"{F(lineY - 6)}\" width=\"8\" height=\"8\" fill=\"{color}\" " +
                       $"fill-opacity=\"{F(opacity)}\"" + (hollow ? $" stroke=\"{color}\" stroke-width=\"0.6\" stroke-dasharray=\"1.5,1\"" : " stroke=\"#B9C0CB\" stroke-width=\"0.4\"") + "/>");
            Text(svg, x + 11, lineY, txt, 6.6f, Ink);
            x += itemW;
        }
    }

    public static string Burndown(Scenario sc, Schedule sch, float w, float h)
    {
        var days = HorizonDays(sc);
        var colors = CampaignColors(sc);
        var svg = new StringBuilder();
        Open(svg, w, h);

        var camps = sc.Campaigns.Where(c => c.TotalTestPoints > 0).ToList();
        if (camps.Count == 0 || days.Count == 0)
        {
            Text(svg, w / 2, h / 2, "No test points to chart.", 11, Muted, anchor: "middle");
            svg.Append("</svg>");
            return svg.ToString();
        }

        const float padL = 46f, padR = 8f, padT = 16f, legendH = 30f;
        float plotX = padL, plotY = padT;
        float plotW = Math.Max(40f, w - padL - padR);
        float plotH = Math.Max(40f, h - padT - legendH - 26f);
        var geo = new Geo
        {
            Days = days, LabelW = padL, PlotX = plotX, PlotY = plotY,
            PlotW = plotW, DayW = plotW / days.Count,
        };

        double maxTp = camps.Max(c => c.TotalTestPoints);
        float Y(double tp) => (float)(plotY + plotH - (tp / maxTp) * plotH);

        DayShading(svg, sc, geo, plotY, plotH);
        svg.Append($"<rect x=\"{F(plotX)}\" y=\"{F(plotY)}\" width=\"{F(plotW)}\" height=\"{F(plotH)}\" fill=\"none\" stroke=\"{GridStrong}\" stroke-width=\"0.7\"/>");

        for (int k = 0; k <= 4; k++)
        {
            double tp = maxTp * k / 4.0;
            float y = Y(tp);
            svg.Append($"<line x1=\"{F(plotX)}\" y1=\"{F(y)}\" x2=\"{F(plotX + plotW)}\" y2=\"{F(y)}\" stroke=\"{Grid}\" stroke-width=\"0.5\"/>");
            Text(svg, plotX - 4, y + 2.5f, tp.ToString("0.#", CultureInfo.InvariantCulture), 6.5f, Muted, anchor: "end");
        }
        TextRotated(svg, 9, plotY + plotH / 2, "Remaining test points", 7f, Muted);
        GridAndAxis(svg, sc, geo, plotY, plotH);

        foreach (var c in camps)
        {
            var flownByDay = new double[days.Count];
            foreach (var s in sch.Sorties.Where(s => s.CampaignId == c.Id))
            {
                int i = Idx(sc, s.Date);
                if (i >= 0 && i < days.Count) flownByDay[i] += s.TestPointsFlown;
            }
            foreach (var s in sch.SimulatorSorties.Where(s => s.CampaignId == c.Id))
            {
                int i = Idx(sc, s.Date);
                if (i >= 0 && i < days.Count) flownByDay[i] += s.TestPointsFlown;
            }

            var pts = new StringBuilder();
            double remaining = c.TotalTestPoints;
            pts.Append($"{F(geo.X(0))},{F(Y(remaining))} ");
            for (int i = 0; i < days.Count; i++)
            {
                if (flownByDay[i] <= 0) continue;
                pts.Append($"{F(geo.X(i))},{F(Y(remaining))} ");
                remaining = Math.Max(0, remaining - flownByDay[i]);
                pts.Append($"{F(geo.X(i) + geo.DayW)},{F(Y(remaining))} ");
            }
            pts.Append($"{F(geo.X(days.Count - 1) + geo.DayW)},{F(Y(remaining))}");

            string color = colors.GetValueOrDefault(c.Id, "#888888");
            svg.Append($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.4\" " +
                       "stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");

            if (c.TargetEndDate is { } t)
            {
                int ti = Idx(sc, t);
                if (ti >= 0 && ti < days.Count)
                    svg.Append($"<line x1=\"{F(geo.X(ti))}\" y1=\"{F(plotY + plotH)}\" x2=\"{F(geo.X(ti))}\" y2=\"{F(plotY + plotH - 7)}\" " +
                               $"stroke=\"{color}\" stroke-width=\"1.4\"/>");
            }
        }

        DeadlineLines(svg, sc, geo, plotY, plotH);

        float x = 4, lineY = plotY + plotH + 24;
        foreach (var c in camps)
        {
            string txt = Clip(c.Name, 26);
            float itemW = 12 + txt.Length * 3.9f + 10;
            if (x + itemW > w - 4) { x = 4; lineY += 12; }
            string color = colors.GetValueOrDefault(c.Id, "#888888");
            svg.Append($"<line x1=\"{F(x)}\" y1=\"{F(lineY - 2)}\" x2=\"{F(x + 9)}\" y2=\"{F(lineY - 2)}\" stroke=\"{color}\" stroke-width=\"1.8\"/>");
            Text(svg, x + 12, lineY, txt, 6.6f, Ink);
            x += itemW;
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static void Open(StringBuilder svg, float w, float h) =>
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\" " +
                   $"viewBox=\"0 0 {F(w)} {F(h)}\"><rect width=\"{F(w)}\" height=\"{F(h)}\" fill=\"#FFFFFF\"/>");

    private static void Text(StringBuilder svg, float x, float y, string text, float size,
        string color, string anchor = "start") =>
        svg.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" font-family=\"{Font}\" font-size=\"{F(size)}\" " +
                   $"fill=\"{color}\" text-anchor=\"{anchor}\">{Esc(text)}</text>");

    private static void TextRotated(StringBuilder svg, float x, float y, string text, float size, string color) =>
        svg.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" font-family=\"{Font}\" font-size=\"{F(size)}\" " +
                   $"fill=\"{color}\" text-anchor=\"middle\" transform=\"rotate(-90 {F(x)} {F(y)})\">{Esc(text)}</text>");

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
