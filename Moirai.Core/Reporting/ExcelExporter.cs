using ClosedXML.Excel;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Reporting;

public static class ExcelExporter
{
    private static readonly string[] Palette =
    {
        "#6366F1", "#22C55E", "#F59E0B", "#EF4444", "#06B6D4", "#A855F7", "#EC4899", "#84CC16",
    };

    private static readonly XLColor UnavailableGray = XLColor.FromHtml("#9CA3AF");

    public static void Export(Scenario sc, Schedule sch, string path)
    {
        using var wb = new XLWorkbook();
        WriteScheduleSheets(wb, sc, sch, prefix: "");
        wb.SaveAs(path);
    }

    internal static void WriteScheduleSheets(XLWorkbook wb, Scenario sc, Schedule sch, string prefix)
    {
        var campColor = sc.Campaigns
            .Select((c, i) => (c.Id, Color: XLColor.FromHtml(Palette[i % Palette.Length])))
            .ToDictionary(t => t.Id, t => t.Color);
        var campById = sc.Campaigns.ToDictionary(c => c.Id);
        var nameById = People(sc).ToDictionary(r => r.Id, r => r.Name);
        var shortById = People(sc).ToDictionary(r => r.Id, r => r.DisplayShort);
        var acNameById = sc.Aircraft.ToDictionary(a => a.Id, a => a.Name);

        WriteSummary(wb, prefix, sc, sch, campColor, campById);
        WriteSorties(wb, prefix, sc, sch, campColor, campById, nameById, acNameById);
        if (sch.SimulatorSorties.Count > 0)
            WriteSimulatorSorties(wb, prefix, sc, sch, campColor, campById, nameById);
        WriteAircraftUtilization(wb, prefix, sc, sch, campColor);
        WriteAircraftCalendar(wb, prefix, sc, sch, campColor, shortById);
        WriteCrewCalendar(wb, prefix, sc, sch, campColor);
        WriteIdleReasons(wb, prefix, sc, IdleReport.Build(sc, sch));
    }

    private static XLColor IdleColor(IdleCategory c) => c switch
    {
        IdleCategory.Unavailable => XLColor.FromHtml("#64748B"),
        IdleCategory.NoWork => XLColor.FromHtml("#A8A29E"),
        IdleCategory.Limits => XLColor.FromHtml("#F59E0B"),
        IdleCategory.EngineChoice => XLColor.FromHtml("#8B5CF6"),
        _ => XLColor.NoColor,
    };

    private static void WriteIdleReasons(XLWorkbook wb, string prefix, Scenario sc, IdleReport idle)
    {
        var days = Days(sc);
        var rows = People(sc).Select(p => (p.Id, p.Name, Type: p is Pilot ? "Pilot" : "Engineer"))
            .Concat(sc.Aircraft.Select(a => (a.Id, Name: a.Id == a.Name || string.IsNullOrWhiteSpace(a.Name)
                ? a.Id : $"{a.Id} · {a.Name}", Type: "Aircraft")))
            .ToList();

        var ws = wb.Worksheets.Add(prefix + "Idle-Reasons");
        ws.Cell(1, 1).Value = "Resource";
        ws.Cell(1, 2).Value = "Type";
        ws.Cell(1, 3).Value = "Idle days";
        for (int j = 0; j < days.Count; j++) ws.Cell(1, j + 4).Value = days[j].ToString("MM-dd");
        ws.Row(1).Style.Font.Bold = true;

        for (int i = 0; i < rows.Count; i++)
        {
            var (id, name, type) = rows[i];
            int row = i + 2;
            ws.Cell(row, 1).Value = name;
            ws.Cell(row, 2).Value = type;
            ws.Cell(row, 3).Value = idle.IdleDays(id);
            var log = idle.PersonLog.GetValueOrDefault(id) ?? idle.AircraftLog.GetValueOrDefault(id) ?? "";
            for (int j = 0; j < log.Length && j < days.Count; j++)
            {
                char c = log[j];
                var cat = IdleCodes.Category(c);
                if (cat == IdleCategory.None) continue;
                SetCalCell(ws, row, j + 4, IdleCodes.Label(c), IdleColor(cat), XLColor.White);
            }
        }

        int lr = rows.Count + 3;
        ws.Cell(lr, 1).Value = "Legend";
        ws.Cell(lr, 1).Style.Font.Bold = true;
        foreach (var cat in new[] { IdleCategory.Unavailable, IdleCategory.NoWork, IdleCategory.Limits, IdleCategory.EngineChoice })
        {
            lr++;
            SetCalCell(ws, lr, 1, IdleCodes.CategoryLabel(cat), IdleColor(cat), XLColor.White);
        }
        ws.Cell(lr + 1, 1).Value = "Usable days only.";
        ws.Column(1).AdjustToContents();
        ws.Column(2).AdjustToContents();
        ws.SheetView.FreezeColumns(3);
        ws.SheetView.FreezeRows(1);
        for (int j = 0; j < days.Count; j++) ws.Column(j + 4).Width = 14;

        var wl = wb.Worksheets.Add(prefix + "Idle-Detail");
        Header(wl, 1, "Resource", "Type", "Date", "Category", "Reason", "Details");
        int r = 2;
        foreach (var (id, name, type) in rows)
            foreach (var d in days)
            {
                if (idle.Explain(id, d) is not { } e || e.Category == IdleCategory.None) continue;
                wl.Cell(r, 1).Value = name;
                wl.Cell(r, 2).Value = type;
                wl.Cell(r, 3).Value = d.ToDateTime(TimeOnly.MinValue);
                wl.Cell(r, 3).Style.DateFormat.Format = "yyyy-MM-dd";
                SetCalCell(wl, r, 4, IdleCodes.CategoryLabel(e.Category), IdleColor(e.Category), XLColor.White);
                wl.Cell(r, 5).Value = e.Headline;
                wl.Cell(r, 6).Value = string.Join(" | ", e.Details);
                r++;
            }
        wl.Columns(1, 5).AdjustToContents();
        wl.Column(6).Width = 120;
        wl.SheetView.FreezeRows(1);
        if (r > 2) wl.Range(1, 1, r - 1, 6).SetAutoFilter();
    }

    private static void WriteAircraftUtilization(
        XLWorkbook wb, string prefix, Scenario sc, Schedule sch, Dictionary<string, XLColor> campColor)
    {
        var ws = wb.Worksheets.Add(prefix + "Aircraft-Utilization");
        var campaigns = sc.Campaigns;
        var campIndex = campaigns.Select((c, i) => (c.Id, i)).ToDictionary(t => t.Id, t => t.i);
        int totalCol = campaigns.Count + 2, daysCol = campaigns.Count + 3;

        ws.Cell(1, 1).Value = "Aircraft";
        for (int j = 0; j < campaigns.Count; j++)
        {
            var cell = ws.Cell(1, j + 2);
            cell.Value = campaigns[j].Name;
            cell.Style.Fill.BackgroundColor = campColor[campaigns[j].Id];
            cell.Style.Font.FontColor = XLColor.White;
        }
        ws.Cell(1, totalCol).Value = "Total sorties";
        ws.Cell(1, daysCol).Value = "Days used";
        ws.Row(1).Style.Font.Bold = true;

        for (int i = 0; i < sc.Aircraft.Count; i++)
        {
            var a = sc.Aircraft[i];
            int row = i + 2;
            ws.Cell(row, 1).Value = string.IsNullOrWhiteSpace(a.Name) || a.Name == a.Id ? a.Id : $"{a.Id} · {a.Name}";

            var tailSorties = sch.Sorties.Where(s => s.AircraftId == a.Id).ToList();
            var perCamp = new int[campaigns.Count];
            foreach (var s in tailSorties)
                if (campIndex.TryGetValue(s.CampaignId, out int ci)) perCamp[ci]++;

            for (int j = 0; j < campaigns.Count; j++)
                if (perCamp[j] > 0)
                {
                    var cell = ws.Cell(row, j + 2);
                    cell.Value = perCamp[j];
                    cell.Style.Fill.BackgroundColor = Tint(campColor[campaigns[j].Id]);
                }
            ws.Cell(row, totalCol).Value = tailSorties.Count;
            ws.Cell(row, daysCol).Value = tailSorties.Select(s => s.Date).Distinct().Count();
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeColumns(1);
        ws.SheetView.FreezeRows(1);
    }

    private static IEnumerable<Resource> People(Scenario sc) =>
        sc.Pilots.Cast<Resource>().Concat(sc.Engineers);

    public static void ExportAnalysis(
        Scenario sc,
        MonteCarlo.Result? mc,
        IReadOnlyList<(string Label, MonteCarlo.Result Result)>? decomposition,
        MonteCarlo.SensitivityResult? sensitivity,
        string path,
        IReadOnlyList<(string Label, Scenario Scenario, Schedule Schedule)>? percentileSchedules = null)
    {
        using var wb = new XLWorkbook();
        if (mc is not null) WriteMcRisk(wb, sc, mc);
        if (decomposition is { Count: > 0 }) WriteDecomposition(wb, decomposition);
        if (sensitivity is not null) WriteSensitivity(wb, sensitivity);
        if (percentileSchedules is not null)
            foreach (var (label, psc, psch) in percentileSchedules)
                WriteScheduleSheets(wb, psc, psch, $"{label} ");
        if (!wb.Worksheets.Any())
            wb.Worksheets.Add("Analysis").Cell(1, 1).Value = "No analysis results to export.";
        wb.SaveAs(path);
    }

    private static void WriteMcRisk(XLWorkbook wb, Scenario sc, MonteCarlo.Result mc)
    {
        var ws = wb.Worksheets.Add("MC-Risk");
        var targets = sc.Campaigns.ToDictionary(c => c.Id, c => c.TargetEndDate);

        ws.Cell(1, 1).Value = $"Monte Carlo · {mc.Runs} runs";
        ws.Cell(2, 1).Value = sc.MasterDeadline is { } md
            ? $"P(master deadline {md:yyyy-MM-dd} met) = {mc.PDelivery:0%}"
            : $"P(all campaigns complete) = {mc.PDelivery:0%}";
        ws.Cell(3, 1).Value = $"Effort: sorties P50 {mc.SortiesP50} · P90 {mc.SortiesP90} · " +
                              $"mean {mc.SortiesMean:0.#}" +
                              (mc.AbortedSortiesMean > 0 ? $" · avg {mc.AbortedSortiesMean:0.#} aborted" : "");
        ws.Range(1, 1, 1, 1).Style.Font.Bold = true;

        int r = 5;
        Header(ws, r, "Campaign", "Target", "P(meet)", "P10 end", "P50 end", "P90 end",
            "P(incomplete)", "Sorties P10", "Sorties P50", "Sorties P90", "Aborted (mean)");
        r++;

        ws.Cell(r, 1).Value = sc.MasterDeadline is { } ? "★ PROGRAM · master deadline" : "★ PROGRAM · all campaigns";
        ws.Cell(r, 1).Style.Font.Bold = true;
        ws.Cell(r, 2).Value = sc.MasterDeadline is { } m ? m.ToString("yyyy-MM-dd") : "-";
        Pct(ws.Cell(r, 3), mc.PDelivery);
        ws.Cell(r, 4).Value = Day(mc.FinishP10);
        ws.Cell(r, 5).Value = Day(mc.FinishP50);
        ws.Cell(r, 6).Value = Day(mc.FinishP90);
        Pct(ws.Cell(r, 7), mc.PProgramIncomplete);
        ws.Cell(r, 8).Value = mc.SortiesP10;
        ws.Cell(r, 9).Value = mc.SortiesP50;
        ws.Cell(r, 10).Value = mc.SortiesP90;
        ws.Cell(r, 11).Value = Math.Round(mc.AbortedSortiesMean, 1);
        r++;

        foreach (var c in mc.Campaigns)
        {
            var target = targets.GetValueOrDefault(c.CampaignId, DateOnly.MaxValue);
            ws.Cell(r, 1).Value = c.CampaignName;
            ws.Cell(r, 2).Value = target == DateOnly.MaxValue ? "-" : target.ToString("yyyy-MM-dd");
            Pct(ws.Cell(r, 3), c.PMeet);
            ws.Cell(r, 4).Value = Day(c.P10);
            ws.Cell(r, 5).Value = Day(c.P50);
            ws.Cell(r, 6).Value = Day(c.P90);
            Pct(ws.Cell(r, 7), c.PIncomplete);
            ws.Cell(r, 8).Value = c.SortiesP10;
            ws.Cell(r, 9).Value = c.SortiesP50;
            ws.Cell(r, 10).Value = c.SortiesP90;
            ws.Cell(r, 11).Value = Math.Round(c.AbortedMean, 1);
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteDecomposition(
        XLWorkbook wb, IReadOnlyList<(string Label, MonteCarlo.Result Result)> decomposition)
    {
        var ws = wb.Worksheets.Add("Risk-Decomposition");
        Header(ws, 1, "Source", "P(program)", "Finish P50", "Finish P90", "Sorties P50", "Sorties P90");
        int r = 2;
        foreach (var (label, res) in decomposition)
        {
            ws.Cell(r, 1).Value = label;
            Pct(ws.Cell(r, 2), res.PDelivery);
            ws.Cell(r, 3).Value = Day(res.FinishP50);
            ws.Cell(r, 4).Value = Day(res.FinishP90);
            ws.Cell(r, 5).Value = res.SortiesP50;
            ws.Cell(r, 6).Value = res.SortiesP90;
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteSensitivity(XLWorkbook wb, MonteCarlo.SensitivityResult s)
    {
        var ws = wb.Worksheets.Add("Sensitivity");

        ws.Cell(1, 1).Value = "Tornado · P(program) swing per parameter (sorted by influence)";
        ws.Cell(1, 1).Style.Font.Bold = true;
        Header(ws, 2, "Parameter", "Low setting", "Low P(program)", "High setting", "High P(program)", "Swing");
        ws.Cell(3, 1).Value = "Baseline (current parameters)";
        Pct(ws.Cell(3, 3), s.BaselinePDelivery);
        Pct(ws.Cell(3, 5), s.BaselinePDelivery);
        int r = 4;
        foreach (var f in s.Factors)
        {
            ws.Cell(r, 1).Value = f.Name;
            ws.Cell(r, 2).Value = f.LowLabel;
            Pct(ws.Cell(r, 3), f.LowPDelivery);
            ws.Cell(r, 4).Value = f.HighLabel;
            Pct(ws.Cell(r, 5), f.HighPDelivery);
            Pct(ws.Cell(r, 6), f.Swing);
            r++;
        }

        if (s.GlobalImportance.Count > 0)
        {
            r += 1;
            ws.Cell(r, 1).Value = "Global importance · share of the P(program) variation each parameter drives";
            ws.Cell(r, 1).Style.Font.Bold = true;
            r++;
            Header(ws, r, "Parameter", "On its own", "With interactions", "Interaction only");
            r++;
            foreach (var g in s.GlobalImportance)
            {
                ws.Cell(r, 1).Value = g.Name;
                Pct(ws.Cell(r, 2), g.DirectShare);
                Pct(ws.Cell(r, 3), g.TotalShare);
                Pct(ws.Cell(r, 4), g.InteractionShare);
                r++;
            }
            ws.Cell(r, 1).Value = "Uncertainty from parameters acting together";
            Pct(ws.Cell(r, 2), s.JointInteractionShare);
            r++;
        }

        r += 1;
        ws.Cell(r, 1).Value = "Parameter sweeps · P(program) across each parameter's range";
        ws.Cell(r, 1).Style.Font.Bold = true;
        r++;
        foreach (var sweep in s.Sweeps)
        {
            ws.Cell(r, 1).Value = sweep.Name;
            ws.Cell(r, 1).Style.Font.Bold = true;
            r++;
            Header(ws, r, "Setting", "P(program)", "Finish P50");
            r++;
            foreach (var p in sweep.Points)
            {
                ws.Cell(r, 1).Value = p.Label;
                Pct(ws.Cell(r, 2), p.PDelivery);
                ws.Cell(r, 3).Value = Day(p.FinishP50);
                r++;
            }
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "-";

    private static void Pct(IXLCell cell, double fraction)
    {
        cell.Value = fraction;
        cell.Style.NumberFormat.Format = "0%";
    }

    private static List<DateOnly> Days(Scenario sc)
    {
        var days = new List<DateOnly>();
        for (var d = sc.HorizonStart; d <= sc.HorizonEnd; d = d.AddDays(1)) days.Add(d);
        return days;
    }

    private static void WriteSummary(
        XLWorkbook wb, string prefix, Scenario sc, Schedule sch,
        Dictionary<string, XLColor> campColor, Dictionary<string, Campaign> campById)
    {
        var ws = wb.Worksheets.Add(prefix + "Summary");

        ws.Cell(1, 1).Value = "Scenario";
        ws.Cell(1, 2).Value = sc.Name;
        ws.Range(1, 1, 1, 2).Style.Font.Bold = true;

        int r = 3;
        Header(ws, r, "Campaign", "Required", "Done", "End date", "Target", "Status");
        r++;
        foreach (var camp in sc.Campaigns)
        {
            int required = camp.RequiredSorties();
            int done = sch.Sorties.Count(s => s.CampaignId == camp.Id);
            bool completed = sch.CampaignCompleted.GetValueOrDefault(camp.Id);
            sch.CampaignEndDates.TryGetValue(camp.Id, out var end);

            var target = CampaignDeadlines.EffectiveMilestone(sc, camp);
            string status; XLColor statusColor;
            if (!completed) { status = "Incomplete"; statusColor = XLColor.FromHtml("#EF4444"); }
            else if (target is not { } t) { status = "Done"; statusColor = XLColor.FromHtml("#3B82F6"); }
            else if (end.HasValue && end.Value > t) { status = "Late"; statusColor = XLColor.FromHtml("#F59E0B"); }
            else { status = "Met"; statusColor = XLColor.FromHtml("#22C55E"); }

            ws.Cell(r, 1).Value = camp.Name;
            ws.Cell(r, 1).Style.Fill.BackgroundColor = campColor[camp.Id];
            ws.Cell(r, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(r, 2).Value = required;
            ws.Cell(r, 3).Value = done;
            ws.Cell(r, 4).Value = end.HasValue ? end.Value.ToString("yyyy-MM-dd") : "-";
            ws.Cell(r, 5).Value = target is { } tt ? tt.ToString("yyyy-MM-dd") : "-";
            ws.Cell(r, 6).Value = status;
            ws.Cell(r, 6).Style.Fill.BackgroundColor = statusColor;
            ws.Cell(r, 6).Style.Font.FontColor = XLColor.White;
            r++;
        }

        var groups = ProgramReporter.Build(sc, sch).Groups;
        if (groups.Count > 0)
        {
            r += 2;
            Header(ws, r, "A-Level phase", "Campaigns", "End date", "Target", "Status");
            r++;
            foreach (var g in groups)
            {
                var (status, color) = g.Status switch
                {
                    MilestoneStatus.Met => ("Met", "#22C55E"),
                    MilestoneStatus.Missed => ("Late", "#F59E0B"),
                    MilestoneStatus.NoTarget => ("Done", "#3B82F6"),
                    _ => ("Incomplete", "#EF4444"),
                };
                ws.Cell(r, 1).Value = g.GroupName;
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 2).Value = g.ChildCount;
                ws.Cell(r, 3).Value = g.End is { } e ? e.ToString("yyyy-MM-dd") : "-";
                ws.Cell(r, 4).Value = g.Target is { } t ? t.ToString("yyyy-MM-dd") : "-";
                ws.Cell(r, 5).Value = status;
                ws.Cell(r, 5).Style.Fill.BackgroundColor = XLColor.FromHtml(color);
                ws.Cell(r, 5).Style.Font.FontColor = XLColor.White;
                r++;
            }
        }

        r += 2;
        Header(ws, r, "Person", "Role", "Flight hrs", "Duty hrs", "Sorties", "Rehearsals");
        r++;
        var load = ComputeLoad(sc, sch, campById);
        foreach (var p in People(sc))
        {
            var l = load.GetValueOrDefault(p.Id);
            ws.Cell(r, 1).Value = p.Name;
            ws.Cell(r, 2).Value = p is Pilot ? "Pilot" : "Engineer";
            ws.Cell(r, 3).Value = l.Flight;
            ws.Cell(r, 4).Value = l.Duty;
            ws.Cell(r, 5).Value = l.Sorties;
            ws.Cell(r, 6).Value = l.Rehearsals;
            r++;
        }

        ws.Columns().AdjustToContents();
    }

    private static Dictionary<string, (double Flight, double Duty, int Sorties, int Rehearsals)> ComputeLoad(
        Scenario sc, Schedule sch, Dictionary<string, Campaign> campById)
    {
        var load = new Dictionary<string, (double, double, int, int)>();
        void Add(string id, double f, double d, int so, int reh)
        {
            var c = load.GetValueOrDefault(id);
            load[id] = (c.Item1 + f, c.Item2 + d, c.Item3 + so, c.Item4 + reh);
        }

        foreach (var s in sch.Sorties)
        {
            var camp = campById[s.CampaignId];
            foreach (var id in Crew(s.PilotId, s.AdditionalPilotIds.Concat(s.SupportPilotIds).Concat(s.EngineerIds)))
                Add(id, camp.ExpectedSortieFlightHours, camp.ExpectedSortieDutyHours, 1, 0);
        }
        foreach (var rh in sch.Rehearsals)
        {
            var camp = campById[rh.CampaignId];
            foreach (var id in rh.CrewIds)
                Add(id, 0, camp.ExpectedRehearsalDutyHours, 0, 1);
        }
        foreach (var ss in sch.SimulatorSorties)
        {
            var camp = campById[ss.CampaignId];
            var crew = ss.AdditionalPilotIds.Concat(ss.EngineerIds);
            if (ss.PilotId.Length > 0) crew = crew.Prepend(ss.PilotId);
            foreach (var id in crew)
                Add(id, 0, camp.ExpectedSortieDutyHours, 1, 0);
        }
        return load;
    }

    private static void WriteSorties(
        XLWorkbook wb, string prefix, Scenario sc, Schedule sch, Dictionary<string, XLColor> campColor,
        Dictionary<string, Campaign> campById, Dictionary<string, string> nameById,
        Dictionary<string, string> acNameById)
    {
        var ws = wb.Worksheets.Add(prefix + "Sorties");
        Header(ws, 1, "Date", "Block", "Campaign", "SortieNo", "Aircraft", "Pilot", "Engineers", "Support ships", "External support", "TP flown", "Rehearsal");
        var extName = sc.ExternalSupports.ToDictionary(a => a.Id, a => a.Name);

        int r = 2;
        foreach (var so in sch.Sorties.OrderBy(s => s.Date).ThenBy(s => s.CampaignId))
        {
            var camp = campById.GetValueOrDefault(so.CampaignId);
            ws.Cell(r, 1).Value = so.Date.ToString("yyyy-MM-dd");
            ws.Cell(r, 2).Value = BlockName(so.Block, sc.Config.BlocksPerDay);
            ws.Cell(r, 3).Value = camp?.Name ?? so.CampaignId;
            ws.Cell(r, 3).Style.Fill.BackgroundColor = campColor[so.CampaignId];
            ws.Cell(r, 3).Style.Font.FontColor = XLColor.White;
            ws.Cell(r, 4).Value = camp is null ? so.SortieNo.ToString() : $"{so.SortieNo}/{camp.RequiredSorties()}";
            ws.Cell(r, 5).Value = acNameById.GetValueOrDefault(so.AircraftId, so.AircraftId);
            ws.Cell(r, 6).Value = string.Join(", ", so.AdditionalPilotIds.Prepend(so.PilotId)
                .Select(id => nameById.GetValueOrDefault(id, id)));
            ws.Cell(r, 7).Value = string.Join(", ", so.EngineerIds.Select(id => nameById.GetValueOrDefault(id, id)));
            ws.Cell(r, 8).Value = so.SupportAircraftIds.Count == 0 ? "-"
                : string.Join(", ", so.SupportAircraftIds.Select((a, i) =>
                    i < so.SupportPilotIds.Count
                        ? $"{a} ({nameById.GetValueOrDefault(so.SupportPilotIds[i], so.SupportPilotIds[i])})"
                        : a));
            ws.Cell(r, 9).Value = so.ExternalSupportIds.Count == 0 ? "-"
                : string.Join(", ", so.ExternalSupportIds.Select(id => extName.GetValueOrDefault(id, id)));
            ws.Cell(r, 10).Value = so.TestPointsFlown;
            ws.Cell(r, 11).Value = so.RehearsalDate?.ToString("yyyy-MM-dd") ?? "-";
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteSimulatorSorties(
        XLWorkbook wb, string prefix, Scenario sc, Schedule sch, Dictionary<string, XLColor> campColor,
        Dictionary<string, Campaign> campById, Dictionary<string, string> nameById)
    {
        var ws = wb.Worksheets.Add(prefix + "Simulator sorties");
        Header(ws, 1, "Date", "Block", "Campaign", "SortieNo", "Simulator", "Pilot", "Engineers", "TP flown");
        var simName = sc.Simulators.ToDictionary(s => s.Id, s => s.Name);

        int r = 2;
        foreach (var ss in sch.SimulatorSorties.OrderBy(s => s.Date).ThenBy(s => s.CampaignId))
        {
            var camp = campById.GetValueOrDefault(ss.CampaignId);
            ws.Cell(r, 1).Value = ss.Date.ToString("yyyy-MM-dd");
            ws.Cell(r, 2).Value = BlockName(ss.Block, sc.Config.BlocksPerDay);
            ws.Cell(r, 3).Value = camp?.Name ?? ss.CampaignId;
            ws.Cell(r, 3).Style.Fill.BackgroundColor = campColor[ss.CampaignId];
            ws.Cell(r, 3).Style.Font.FontColor = XLColor.White;
            ws.Cell(r, 4).Value = camp is null ? ss.SortieNo.ToString() : $"{ss.SortieNo}/{camp.RequiredSorties()}";
            ws.Cell(r, 5).Value = simName.GetValueOrDefault(ss.SimulatorId, ss.SimulatorId);
            ws.Cell(r, 6).Value = ss.PilotId.Length == 0 ? "-"
                : string.Join(", ", ss.AdditionalPilotIds.Prepend(ss.PilotId).Select(id => nameById.GetValueOrDefault(id, id)));
            ws.Cell(r, 7).Value = string.Join(", ", ss.EngineerIds.Select(id => nameById.GetValueOrDefault(id, id)));
            ws.Cell(r, 8).Value = ss.Aborted ? 0 : ss.TestPointsFlown;
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static string BlockName(int block, int blocksPerDay) =>
        blocksPerDay == 3
            ? block switch { 1 => "morning", 2 => "midday", 3 => "afternoon", _ => $"block {block}" }
            : $"block {block}";

    private static void WriteAircraftCalendar(
        XLWorkbook wb, string prefix, Scenario sc, Schedule sch,
        Dictionary<string, XLColor> campColor, Dictionary<string, string> shortById)
    {
        var ws = wb.Worksheets.Add(prefix + "Aircraft-Calendar");
        var days = Days(sc);
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);

        WriteCalendarHeader(ws, days, "Aircraft");
        var rowOf = sc.Aircraft.Select((a, i) => (a.Id, Row: i + 2)).ToDictionary(t => t.Id, t => t.Row);
        for (int i = 0; i < sc.Aircraft.Count; i++)
            ws.Cell(i + 2, 1).Value = sc.Aircraft[i].Name;

        foreach (var so in sch.Sorties)
        {
            if (!rowOf.TryGetValue(so.AircraftId, out int row)) continue;
            int col = (so.Date.DayNumber - sc.HorizonStart.DayNumber) + 2;
            var cell = ws.Cell(row, col);
            cell.Value = $"{campName.GetValueOrDefault(so.CampaignId, so.CampaignId)} / {shortById.GetValueOrDefault(so.PilotId, so.PilotId)}";
            cell.Style.Fill.BackgroundColor = campColor[so.CampaignId];
            cell.Style.Font.FontColor = XLColor.White;
        }

        for (int i = 0; i < sc.Aircraft.Count; i++)
            for (int j = 0; j < days.Count; j++)
                if (sc.Aircraft[i].IsInMaintenance(days[j]) && ws.Cell(i + 2, j + 2).IsEmpty())
                    SetCalCell(ws, i + 2, j + 2, "MAINT", UnavailableGray, XLColor.White);

        FinishCalendar(ws, days);
    }

    private static void WriteCrewCalendar(
        XLWorkbook wb, string prefix, Scenario sc, Schedule sch, Dictionary<string, XLColor> campColor)
    {
        var ws = wb.Worksheets.Add(prefix + "Crew-Calendar");
        var days = Days(sc);
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);

        WriteCalendarHeader(ws, days, "Crew");
        var people = People(sc).ToList();
        var rowOf = people.Select((p, i) => (p.Id, Row: i + 2)).ToDictionary(t => t.Id, t => t.Row);
        for (int i = 0; i < people.Count; i++)
            ws.Cell(i + 2, 1).Value = people[i].Name;

        foreach (var rh in sch.Rehearsals)
        {
            int col = (rh.Date.DayNumber - sc.HorizonStart.DayNumber) + 2;
            foreach (var id in rh.CrewIds)
                if (rowOf.TryGetValue(id, out int row))
                    SetCalCell(ws, row, col, $"R:{campName.GetValueOrDefault(rh.CampaignId, rh.CampaignId)}",
                        Tint(campColor[rh.CampaignId]), XLColor.Black);
        }
        foreach (var so in sch.Sorties)
        {
            int col = (so.Date.DayNumber - sc.HorizonStart.DayNumber) + 2;
            foreach (var id in Crew(so.PilotId, so.AdditionalPilotIds.Concat(so.SupportPilotIds).Concat(so.EngineerIds)))
                if (rowOf.TryGetValue(id, out int row))
                    SetCalCell(ws, row, col, $"S:{campName.GetValueOrDefault(so.CampaignId, so.CampaignId)}",
                        campColor[so.CampaignId], XLColor.White);
        }

        for (int i = 0; i < people.Count; i++)
            for (int j = 0; j < days.Count; j++)
                if (people[i].IsOnLeave(days[j]) && ws.Cell(i + 2, j + 2).IsEmpty())
                    SetCalCell(ws, i + 2, j + 2, "LEAVE", UnavailableGray, XLColor.White);

        FinishCalendar(ws, days);
    }

    private static void WriteCalendarHeader(IXLWorksheet ws, List<DateOnly> days, string corner)
    {
        ws.Cell(1, 1).Value = corner;
        for (int j = 0; j < days.Count; j++)
            ws.Cell(1, j + 2).Value = days[j].ToString("MM-dd");
        ws.Row(1).Style.Font.Bold = true;
    }

    private static void FinishCalendar(IXLWorksheet ws, List<DateOnly> days)
    {
        ws.Column(1).AdjustToContents();
        ws.SheetView.FreezeColumns(1);
        ws.SheetView.FreezeRows(1);
        for (int j = 0; j < days.Count; j++)
            ws.Column(j + 2).Width = 12;
    }

    private static void SetCalCell(IXLWorksheet ws, int row, int col, string text, XLColor bg, XLColor fg)
    {
        var cell = ws.Cell(row, col);
        cell.Value = text;
        cell.Style.Fill.BackgroundColor = bg;
        cell.Style.Font.FontColor = fg;
    }

    private static void Header(IXLWorksheet ws, int row, params string[] titles)
    {
        for (int i = 0; i < titles.Length; i++)
        {
            var cell = ws.Cell(row, i + 1);
            cell.Value = titles[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1A2029");
            cell.Style.Font.FontColor = XLColor.White;
        }
    }

    private static XLColor Tint(XLColor c) =>
        XLColor.FromArgb(70, c.Color.R, c.Color.G, c.Color.B);

    private static IEnumerable<string> Crew(string pilotId, IEnumerable<string> engineers)
    {
        yield return pilotId;
        foreach (var e in engineers) yield return e;
    }
}
