using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MOIRAI.Reporting;

public static class PdfReporter
{
    private const string Ink = "#1A1F27", Muted = "#5A636F", Rule = "#C7CDD6",
                         HeadBg = "#1A2029", Good = "#1B8A3A", Warn = "#B7791F", Bad = "#C03A33",
                         ZebraBg = "#F3F5F8";

    static PdfReporter() => QuestPDF.Settings.License = LicenseType.Community;

    public static void Export(
        Scenario sc,
        Schedule sch,
        string path,
        MonteCarlo.Result? mc = null,
        IReadOnlyList<(string Label, MonteCarlo.Result Result)>? decomposition = null,
        MonteCarlo.SensitivityResult? sensitivity = null)
    {
        var report = ProgramReporter.Build(sc, sch);

        var body = new List<(string Title, Action<ColumnDescriptor> Body)>
        {
            ("Program overview", c => Verdict(c, report)),
            ("Campaign outcomes", c => CampaignOutcomes(c, sc, sch)),
            ("Aircraft", c => Fleet(c, sc)),
            ("Personnel", c => Personnel(c, sc)),
            ("Campaign definitions", c => CampaignDefinitions(c, sc)),
            ("Workload (per person)", c => Workload(c, sc, sch)),
            ("Aircraft utilization", c => AircraftUtilization(c, sc, sch)),
        };
        if (sc.Locations.Count > 0) body.Add(("Locations & deployments", c => Locations(c, sc, sch)));
        if (mc is not null) body.Add(("Monte-Carlo deadline risk", c => McRisk(c, sc, mc)));
        if (decomposition is { Count: > 0 }) body.Add(("Risk decomposition", c => Decomposition(c, decomposition)));
        if (sensitivity is not null) body.Add(("Sensitivity analysis", c => Sensitivity(c, sensitivity)));

        var charts = new List<(string Title, Action<ColumnDescriptor> Body)>
        {
            ("Gantt chart & burn-down", c => Charts(c, sc, sch)),
        };
        var appendix = new List<(string Title, Action<ColumnDescriptor> Body)>
        {
            ("Schedule (sorties)", c => ScheduleTable(c, sc, sch)),
        };
        if (sch.SimulatorSorties.Count > 0)
            appendix.Add(("Schedule (simulator sessions)", c => SimScheduleTable(c, sc, sch)));
        appendix.Add(("Settings & model", c => Settings(c, sc)));

        int n = 0;
        var numbered = body.Concat(charts).Concat(appendix)
            .Select(s => (Title: $"{++n}. {s.Title}", s.Body)).ToList();
        var (bodyN, chartsN, appendixN) =
            (numbered.Take(body.Count).ToList(),
             numbered.Skip(body.Count).Take(charts.Count).ToList(),
             numbered.Skip(body.Count + charts.Count).ToList());

        Document.Create(doc =>
        {
            doc.Page(page => Compose(page, sc, bodyN, landscape: false));
            doc.Page(page => Compose(page, sc, chartsN, landscape: true));
            doc.Page(page => Compose(page, sc, appendixN, landscape: false));
        }).GeneratePdf(path);
    }

    private static void Compose(PageDescriptor page, Scenario sc,
        List<(string Title, Action<ColumnDescriptor> Body)> sections, bool landscape)
    {
        page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
        page.Margin(1.4f, Unit.Centimetre);
        page.DefaultTextStyle(t => t.FontFamily("Segoe UI").FontSize(8.5f).FontColor(Ink));

        page.Header().Element(h => Header(h, sc));
        page.Footer().Element(Footer);

        page.Content().PaddingVertical(8).Column(col =>
        {
            col.Spacing(12);
            foreach (var (title, body) in sections)
            {
                Section(col, title);
                body(col);
            }
        });
    }

    private static void Header(IContainer c, Scenario sc) =>
        c.BorderBottom(1).BorderColor(Rule).PaddingBottom(6).Column(col =>
        {
            col.Item().Text("MOIRAI · Flight-Test Program Report").FontSize(15).Bold();
            col.Item().Text(t =>
            {
                t.Span("Scenario: ").FontColor(Muted);
                t.Span(sc.Name).SemiBold();
                t.Span($"     Horizon: {D(sc.HorizonStart)} … {D(sc.HorizonEnd)}").FontColor(Muted);
            });
        });

    private static void Footer(IContainer c) =>
        c.BorderTop(1).BorderColor(Rule).PaddingTop(4).Row(r =>
        {
            r.RelativeItem().Text("MOIRAI engine report").FontSize(7).FontColor(Muted);
            r.RelativeItem().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(7).FontColor(Muted));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });

    private static void Verdict(ColumnDescriptor col, ProgramReport r)
    {
        string color = r.Delivered ? Good : Bad;
        string headline = r.Delivered
            ? "DELIVERED · all campaigns complete within the program date"
            : "AT RISK · master deadline overrun or incomplete campaigns";
        col.Item().Text(headline).FontSize(11).SemiBold().FontColor(color);

        KeyValues(col,
            ("Master deadline", r.MasterDeadline is { } m ? D(m) : "- (none)"),
            ("Program finish", r.ProgramFinish is { } f ? D(f) : "-"),
            ("Schedule margin", r.SlackDays is { } s
                ? (s >= 0 ? $"{s} d remaining to the master deadline"
                          : $"{-s} d overrun ({r.OverdueSorties} sorties past the master deadline)")
                : "-"),
            ("Campaigns met", $"{r.Campaigns.Count(c => c.Status == MilestoneStatus.Met)}/{r.Campaigns.Count}"),
            ("Total sorties / rehearsals", $"{r.TotalSorties} / {r.TotalRehearsals}"),
            ("Flight / duty hours", $"{r.TotalFlightHours:0.#} / {r.TotalDutyHours:0.#}"),
            ("Aircraft / crew used", $"{r.AircraftUsed} / {r.CrewUsed}"));
    }

    private static void CampaignOutcomes(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        Table(col, new[] { ("Campaign", 3f), ("Req", 1f), ("Done", 1f), ("End date", 2f), ("Target", 2f), ("Status", 1.6f), ("Dominant bottleneck", 3f) },
            sc.Campaigns.Select(c =>
            {
                int req = c.RequiredSorties();
                int done = sch.Sorties.Count(s => s.CampaignId == c.Id);
                bool completed = sch.CampaignCompleted.GetValueOrDefault(c.Id);
                sch.CampaignEndDates.TryGetValue(c.Id, out var end);
                var target = CampaignDeadlines.EffectiveMilestone(sc, c);
                string status = !completed ? "Incomplete"
                    : target is not { } t ? "Done"
                    : end.HasValue && end.Value > t ? "Late" : "Met";
                string bott = sch.Bottlenecks.TryGetValue(c.Id, out var tally) && tally.Dominant() is { } dom
                    ? $"{dom.Reason} ({dom.Days} d)" : "-";
                return new[]
                {
                    c.Name, req.ToString(), done.ToString(),
                    end.HasValue ? D(end.Value) : "-",
                    target is { } tt ? D(tt) : "-",
                    status, bott,
                };
            }));

        var groups = ProgramReporter.Build(sc, sch).Groups;
        if (groups.Count > 0)
        {
            col.Item().PaddingTop(8).Text("A-Level phases").SemiBold().FontColor(Muted);
            Table(col, new[] { ("Phase", 3f), ("Campaigns", 1.4f), ("End date", 2f), ("Target", 2f), ("Status", 1.6f) },
                groups.Select(g => new[]
                {
                    g.GroupName, g.ChildCount.ToString(),
                    g.End is { } e ? D(e) : "-",
                    g.Target is { } t ? D(t) : "-",
                    g.Status switch
                    {
                        MilestoneStatus.Met => "Met",
                        MilestoneStatus.Missed => "Late",
                        MilestoneStatus.NoTarget => "Done",
                        _ => "Incomplete",
                    },
                }));
        }
    }

    private static void Settings(ColumnDescriptor col, Scenario sc)
    {
        col.Item().Text("Weather & calendar").SemiBold().FontColor(Muted);
        KeyValues(col,
            ("Flyable model", Weather(sc)),
            ("Weekend days", string.Join(", ", sc.Calendar.WeekendDays.OrderBy(d => (int)d).Select(d => d.ToString()))),
            ("Holidays", sc.Calendar.Holidays.Count.ToString()),
            ("Extra workdays (authorized)", sc.Calendar.ExtraWorkdays.Count == 0
                ? "-"
                : string.Join(", ", sc.Calendar.ExtraWorkdays.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")))));

        col.Item().PaddingTop(4).Text("FTOM limits").SemiBold().FontColor(Muted);
        var cfg = sc.Config;
        KeyValues(col,
            ("Blocks / day", cfg.BlocksPerDay.ToString()),
            ("Pilots / engineers per sortie", $"{cfg.PilotsPerSortie} / {cfg.EngineersPerSortie}"),
            ("Max sorties / person / day", cfg.MaxSortiesPerPersonPerDay.ToString()),
            ("Max rehearsals / person / day", cfg.MaxRehearsalsPerPersonPerDay.ToString()),
            ("Max rehearsal gap (working days)", cfg.MaxRehearsalGapDays.ToString()),
            ("Load-balance window (days)", cfg.LoadBalanceWindowDays.ToString()),
            ("Max consecutive flying days", cfg.MaxConsecutiveFlyingDays.ToString()),
            ("Min rest (h)", F(cfg.MinRestHours)),
            ("Flight cap day / 7 / 28 / year", $"{F(cfg.MaxFlightHoursPerDay)} / {F(cfg.MaxFlightHoursPer7Days)} / {F(cfg.MaxFlightHoursPer28Days)} / {F(cfg.MaxFlightHoursPerYear)}"),
            ("Duty cap day / 7 / 14 / 28", $"{F(cfg.MaxDutyHoursPerDay)} / {F(cfg.MaxDutyHoursPer7Days)} / {F(cfg.MaxDutyHoursPer14Days)} / {F(cfg.MaxDutyHoursPer28Days)}"),
            ("Max flight-duty / day", F(cfg.MaxFlightDutyHoursPerDay)));

        col.Item().PaddingTop(4).Text("Test efficiency").SemiBold().FontColor(Muted);
        KeyValues(col,
            ("Campaigns with efficiency on", $"{sc.Campaigns.Count(c => c.TestEfficiencyEnabled)}/{sc.Campaigns.Count}"),
            ("Simulate yield in nominal", B(cfg.SimulateYieldInNominal)),
            ("Scenario abort override", cfg.OverrideSortieAbort ? F(cfg.SortieAbortOverride) : "off"));
    }

    private static void Fleet(ColumnDescriptor col, Scenario sc)
    {
        Table(col, new[] { ("Tail", 1.4f), ("Name", 2f), ("Maintenance windows", 3f), ("Periodic rules", 2.4f) },
            sc.Aircraft.Select(a => new[]
            {
                a.Id, a.Name,
                a.MaintenanceWindows.Count == 0 ? "-"
                    : string.Join("; ", a.MaintenanceWindows.Select(w => $"{D(w.Start)}-{D(w.End)}")),
                a.PeriodicMaintenances.Count == 0 ? "-"
                    : string.Join("; ", a.PeriodicMaintenances.Select(p => $"{F(p.IntervalFlightHours)}h→{p.DurationDays}d")),
            }));
    }

    private static void Personnel(ColumnDescriptor col, Scenario sc)
    {
        var people = sc.Pilots.Cast<Resource>().Select(p => (R: p, Role: "Pilot"))
            .Concat(sc.Engineers.Select(e => (R: (Resource)e, Role: "Engineer")));
        Table(col, new[] { ("Name", 2.4f), ("Role", 1.4f), ("Short", 1f), ("Leave windows", 3.2f) },
            people.Select(x => new[]
            {
                x.R.Name, x.Role, x.R.DisplayShort,
                x.R.LeaveWindows.Count == 0 ? "-"
                    : string.Join("; ", x.R.LeaveWindows.Select(w => $"{D(w.Start)}-{D(w.End)}")),
            }));
    }

    private static void CampaignDefinitions(ColumnDescriptor col, Scenario sc)
    {
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);

        var windowLabel = sc.Aircraft
            .SelectMany(a => a.MaintenanceWindows.Select(w =>
                (w.Id, Label: $"{a.Id} {D(w.Start)}→{D(w.End)}" +
                              (string.IsNullOrWhiteSpace(w.Note) ? "" : $" ({w.Note})"))))
            .ToDictionary(t => t.Id, t => t.Label);

        var pages = sc.Campaigns.Chunk(CampaignsPerPage).ToList();
        for (int p = 0; p < pages.Count; p++)
        {
            col.Item().ShowEntire().Column(grid =>
            {
                grid.Spacing(11);
                foreach (var line in pages[p].Chunk(CampaignCols))
                    grid.Item().Row(row =>
                    {
                        row.Spacing(11);
                        foreach (var c in line)
                            row.RelativeItem().Element(x => CampaignCard(x, c, sc, campName, windowLabel));
                        for (int i = line.Length; i < CampaignCols; i++) row.RelativeItem();
                    });
            });
            if (p < pages.Count - 1) col.Item().PageBreak();
        }
    }

    private const int CampaignCols = 3, CampaignRows = 2;
    private const int CampaignsPerPage = CampaignCols * CampaignRows;

    private static void CampaignCard(IContainer box, Campaign c, Scenario sc,
        Dictionary<string, string> campName, Dictionary<string, string> windowLabel)
    {
        var rows = new (string K, string V)[]
        {
            ("Test points", $"{F(c.TotalTestPoints)} / {F(c.TestPointsPerSortie)} per sortie"),
            ("Sorties", $"{c.RequiredSorties()}"),
            ("Window", $"{D(c.StartDate)} → {(c.TargetEndDate == DateOnly.MaxValue ? "—" : D(c.TargetEndDate))}"),
            ("Aircraft", c.CompatibleAircraftIds.Count == 0 ? "-" : string.Join(", ", c.CompatibleAircraftIds)),
            ("Crew P / E", $"{c.AssignedPilotIds.Count} / {c.AssignedEngineerIds.Count}"),
            ("Prerequisites", c.PrerequisiteCampaignIds.Count == 0 ? "-"
                : string.Join(", ", c.PrerequisiteCampaignIds.Select(id => campName.GetValueOrDefault(id, id)))),
            ("Prereq. maint.", c.PrerequisiteMaintenanceIds.Count == 0 ? "-"
                : string.Join("; ", c.PrerequisiteMaintenanceIds.Select(id => windowLabel.GetValueOrDefault(id, id)))),
            ("Location", c.LocationId.Length == 0 ? "Main base"
                : sc.Locations.FirstOrDefault(l => l.Id == c.LocationId)?.Name ?? c.LocationId),
            ("Blocks", $"{c.BlocksPerSortie} · {(c.AllowedBlocks.Count == 0 ? "all" : string.Join(",", c.AllowedBlocks))}"),
            ("Single tail", B(c.PreferSingleAircraft)),
            ("Hours F/D/R", $"{F(c.ExpectedSortieFlightHours)} / {F(c.ExpectedSortieDutyHours)} / {F(c.ExpectedRehearsalDutyHours)}"),
            ("Efficiency", c.TestEfficiencyEnabled
                ? $"retest {F(c.RetestProbabilityStart)}→{F(c.RetestProbabilityFloor)}, abort {F(c.AbortProbability)} (×{c.ExpectedInflation():0.##})"
                : "off"),
        };

        box.Border(1).BorderColor(Rule).Padding(8).Column(col =>
        {
            col.Item().PaddingBottom(4).BorderBottom(1).BorderColor(Rule)
                .Text(c.Name).FontSize(9).SemiBold();
            col.Item().PaddingTop(4).Table(t =>
            {
                t.ColumnsDefinition(d => { d.ConstantColumn(62); d.RelativeColumn(); });
                foreach (var (k, v) in rows)
                {
                    t.Cell().PaddingVertical(2.6f).Text(k).FontSize(7.4f).FontColor(Muted);
                    t.Cell().PaddingVertical(2.6f).Text(v).FontSize(7.4f);
                }
            });
        });
    }

    private static void Workload(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        var campById = sc.Campaigns.ToDictionary(c => c.Id);
        var load = new Dictionary<string, (double F, double D, int S, int R)>();
        void Add(string id, double f, double d, int s, int r)
        {
            var c = load.GetValueOrDefault(id);
            load[id] = (c.F + f, c.D + d, c.S + s, c.R + r);
        }
        foreach (var s in sch.Sorties)
        {
            var c = campById[s.CampaignId];
            Add(s.PilotId, c.ExpectedSortieFlightHours, c.ExpectedSortieDutyHours, 1, 0);
            foreach (var ap in s.AdditionalPilotIds.Concat(s.SupportPilotIds)) Add(ap, c.ExpectedSortieFlightHours, c.ExpectedSortieDutyHours, 1, 0);
            foreach (var e in s.EngineerIds) Add(e, c.ExpectedSortieFlightHours, c.ExpectedSortieDutyHours, 1, 0);
        }
        foreach (var rh in sch.Rehearsals)
        {
            var c = campById[rh.CampaignId];
            foreach (var id in rh.CrewIds) Add(id, 0, c.ExpectedRehearsalDutyHours, 0, 1);
        }
        foreach (var ss in sch.SimulatorSorties)
        {
            var c = campById[ss.CampaignId];
            if (ss.PilotId.Length > 0) Add(ss.PilotId, 0, c.ExpectedSortieDutyHours, 1, 0);
            foreach (var ap in ss.AdditionalPilotIds) Add(ap, 0, c.ExpectedSortieDutyHours, 1, 0);
            foreach (var e in ss.EngineerIds) Add(e, 0, c.ExpectedSortieDutyHours, 1, 0);
        }

        Table(col, new[] { ("Person", 2.6f), ("Role", 1.4f), ("Sorties", 1.2f), ("Flight h", 1.2f), ("Duty h", 1.2f), ("Rehearsals", 1.4f) },
            sc.Pilots.Cast<Resource>().Concat(sc.Engineers)
                .Where(p => load.ContainsKey(p.Id))
                .Select(p =>
                {
                    var l = load[p.Id];
                    return new[] { p.Name, p is Pilot ? "Pilot" : "Engineer", l.S.ToString(), $"{l.F:0.#}", $"{l.D:0.#}", l.R.ToString() };
                }));
    }

    private static void AircraftUtilization(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);
        Table(col, new[] { ("Aircraft", 2.2f), ("Sorties", 1.2f), ("Days", 1f), ("Campaign breakdown", 5f) },
            sc.Aircraft.Select(a =>
            {
                var ts = sch.Sorties.Where(s => s.AircraftId == a.Id).ToList();
                string breakdown = ts.Count == 0 ? "unused"
                    : string.Join(" · ", ts.GroupBy(s => s.CampaignId)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{campName.GetValueOrDefault(g.Key, g.Key)} {g.Count()}"));
                return new[]
                {
                    string.IsNullOrWhiteSpace(a.Name) || a.Name == a.Id ? a.Id : $"{a.Id} ({a.Name})",
                    ts.Count.ToString(), ts.Select(s => s.Date).Distinct().Count().ToString(), breakdown,
                };
            }));
    }

    private static void Locations(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        Table(col, new[] { ("Location", 1.8f), ("Blocks cap", 1.2f), ("Simulator", 1.1f), ("Ferry (d / h)", 1.4f), ("Setup / teardown", 1.6f), ("Weather", 2.2f), ("Range availability", 2.2f) },
            sc.Locations.Select(l => new[]
            {
                l.Name,
                l.BlocksPerDayCap?.ToString() ?? "full",
                B(sc.Simulators.Any(s => s.LocationId == l.Id)),
                $"{l.FerryDays} / {F(l.FerryFlightHours)}",
                $"{l.SetupDays} / {l.TeardownDays}",
                Availability(l.FlyableModel, "scenario's model"),
                Availability(l.RangeModel, "always available"),
            }));

        if (sch.Deployments.Count > 0)
        {
            col.Item().PaddingTop(4).Text("Derived deployments").SemiBold().FontColor(Muted);
            Table(col, new[] { ("Location", 2f), ("Departure", 1.6f), ("Flying from", 1.6f), ("Return ends", 1.6f), ("Aircraft", 2f), ("Crew", 1f) },
                sch.Deployments.Select(d => new[]
                {
                    d.LocationName, D(d.Departure), D(d.FlyFrom),
                    d.ReturnEnd is { } r ? D(r) : "- (not in horizon)",
                    string.Join(", ", d.AircraftIds), d.PersonIds.Count.ToString(),
                }));
        }
    }

    private static string Availability(FlyableDayModel? model, string whenNull) => model switch
    {
        null => whenNull,
        ProbabilisticFlyableDays p =>
            $"probabilistic (mean {F(p.MonthlyRates is { Length: 12 } ? p.MonthlyRates.Average() : 0)}, phi {F(p.Persistence)})",
        RateBasedFlyableDays r => $"rate pattern ({F(r.Rate)})",
        ExplicitFlyableDays e => $"{e.Days.Count} explicit day(s)",
        _ => model.GetType().Name,
    };

    private static void McRisk(ColumnDescriptor col, Scenario sc, MonteCarlo.Result mc)
    {
        col.Item().Text($"{mc.Runs} runs · P(program) = {mc.PDelivery:0%} · sorties P50 {mc.SortiesP50} / P90 {mc.SortiesP90}" +
                        (mc.AbortedSortiesMean > 0 ? $" · avg {mc.AbortedSortiesMean:0.#} aborted" : "")).FontColor(Muted);

        var targets = sc.Campaigns.ToDictionary(c => c.Id, c => c.TargetEndDate);
        var rows = new List<string[]>
        {
            new[]
            {
                sc.MasterDeadline is { } ? "★ PROGRAM" : "★ PROGRAM (all complete)",
                sc.MasterDeadline is { } m ? D(m) : "-",
                mc.PDelivery.ToString("0%"), Day(mc.FinishP10), Day(mc.FinishP50), Day(mc.FinishP90),
                mc.PProgramIncomplete.ToString("0%"), $"{mc.SortiesP10}/{mc.SortiesP50}/{mc.SortiesP90}", mc.AbortedSortiesMean.ToString("0.#"),
            },
        };
        rows.AddRange(mc.Campaigns.Select(c =>
        {
            var t = targets.GetValueOrDefault(c.CampaignId, DateOnly.MaxValue);
            return new[]
            {
                c.CampaignName, t == DateOnly.MaxValue ? "-" : D(t),
                c.PMeet.ToString("0%"), Day(c.P10), Day(c.P50), Day(c.P90),
                c.PIncomplete.ToString("0%"), $"{c.SortiesP10}/{c.SortiesP50}/{c.SortiesP90}", c.AbortedMean.ToString("0.#"),
            };
        }));
        Table(col, new[] { ("Campaign", 2.4f), ("Target", 1.8f), ("P(meet)", 1.2f), ("P10", 1.6f), ("P50", 1.6f), ("P90", 1.6f), ("Incomp.", 1.2f), ("Sorties 10/50/90", 2f), ("Aborts", 1f) }, rows);
    }

    private static void Decomposition(ColumnDescriptor col, IReadOnlyList<(string Label, MonteCarlo.Result Result)> dec)
    {
        Table(col, new[] { ("Source", 2.4f), ("P(program)", 1.6f), ("Finish P50", 2f), ("Finish P90", 2f), ("Sorties P50/P90", 2f) },
            dec.Select(d => new[]
            {
                d.Label, d.Result.PDelivery.ToString("0%"), Day(d.Result.FinishP50), Day(d.Result.FinishP90),
                $"{d.Result.SortiesP50} / {d.Result.SortiesP90}",
            }));
    }

    private static void Sensitivity(ColumnDescriptor col, MonteCarlo.SensitivityResult s)
    {
        col.Item().Text($"Baseline P(program) = {s.BaselinePDelivery:0%}. Tornado (most influential first):").FontColor(Muted);
        Table(col, new[] { ("Parameter", 2.6f), ("Low setting", 1.8f), ("Low P", 1.2f), ("High setting", 1.8f), ("High P", 1.2f), ("Swing", 1.2f) },
            s.Factors.Select(f => new[]
            {
                f.Name, f.LowLabel, f.LowPDelivery.ToString("0%"), f.HighLabel, f.HighPDelivery.ToString("0%"), f.Swing.ToString("0%"),
            }));
        if (s.GlobalImportance.Count > 0)
        {
            col.Item().PaddingTop(4)
                .Text($"Global importance · share of the P(program) variation each parameter drives " +
                      $"({s.JointInteractionShare:0%} from parameters acting together)").SemiBold().FontColor(Muted);
            Table(col, new[] { ("Parameter", 2.6f), ("On its own", 1.6f), ("With interactions", 1.8f), ("Interaction only", 1.8f) },
                s.GlobalImportance.Select(g => new[]
                {
                    g.Name, g.DirectShare.ToString("0%"), g.TotalShare.ToString("0%"), g.InteractionShare.ToString("0%"),
                }));
        }

        col.Item().PaddingTop(4).Text("Parameter sweeps · P(program) across each parameter's range")
            .SemiBold().FontColor(Muted);
        foreach (var sweep in s.Sweeps)
        {
            col.Item().PaddingTop(2).Text(sweep.Name).SemiBold();
            Table(col, new[] { ("Setting", 1f), ("P(program)", 1.6f), ("Finish P50", 2f) },
                sweep.Points.Select(p => new[] { p.Label, p.PDelivery.ToString("0%"), Day(p.FinishP50) }));
        }
    }

    private static void ScheduleTable(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        var nameById = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).ToDictionary(r => r.Id, r => r.DisplayShort);
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);
        Table(col, new[] { ("Date", 1.8f), ("Blk", 0.8f), ("Campaign", 2.4f), ("#", 0.8f), ("Tail", 1.2f), ("Pilot", 1.2f), ("Engineers", 2f), ("TP", 0.9f), ("Rehearsal", 1.8f) },
            sch.Sorties.OrderBy(s => s.Date).ThenBy(s => s.Block).Select(s => new[]
            {
                D(s.Date), s.Block.ToString(), campName.GetValueOrDefault(s.CampaignId, s.CampaignId), s.SortieNo.ToString(),
                s.AircraftId,
                string.Join(", ", s.AdditionalPilotIds.Prepend(s.PilotId).Select(p => nameById.GetValueOrDefault(p, p))),
                string.Join(", ", s.EngineerIds.Select(e => nameById.GetValueOrDefault(e, e))),
                s.Aborted ? "0 (abort)" : F(s.TestPointsFlown), Day(s.RehearsalDate),
            }));
    }

    private static void SimScheduleTable(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        var nameById = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).ToDictionary(r => r.Id, r => r.DisplayShort);
        var campName = sc.Campaigns.ToDictionary(c => c.Id, c => c.Name);
        var simName = sc.Simulators.ToDictionary(s => s.Id, s => s.Name);
        Table(col, new[] { ("Date", 1.8f), ("Blk", 0.8f), ("Campaign", 2.4f), ("#", 0.8f), ("Simulator", 1.6f), ("Pilot", 1.2f), ("Engineers", 2.4f), ("TP", 0.9f) },
            sch.SimulatorSorties.OrderBy(s => s.Date).ThenBy(s => s.Block).Select(s => new[]
            {
                D(s.Date), s.Block.ToString(), campName.GetValueOrDefault(s.CampaignId, s.CampaignId), s.SortieNo.ToString(),
                simName.GetValueOrDefault(s.SimulatorId, s.SimulatorId),
                s.PilotId.Length == 0 ? "-"
                    : string.Join(", ", s.AdditionalPilotIds.Prepend(s.PilotId).Select(p => nameById.GetValueOrDefault(p, p))),
                string.Join(", ", s.EngineerIds.Select(e => nameById.GetValueOrDefault(e, e))),
                s.Aborted ? "0 (abort)" : F(s.TestPointsFlown),
            }));
    }

    private static void Charts(ColumnDescriptor col, Scenario sc, Schedule sch)
    {
        col.Item().Height(ChartH).Svg(ReportCharts.AircraftGantt(sc, sch, ChartW, ChartH));
        col.Item().PageBreak();
        col.Item().Height(ChartH).Svg(ReportCharts.PersonnelGantt(sc, sch, ChartW, ChartH));
        col.Item().PageBreak();
        col.Item().Height(ChartH).Svg(ReportCharts.Burndown(sc, sch, ChartW, ChartH));
    }

    private const float ChartW = 762.5f, ChartH = 395f;

    private static void Section(ColumnDescriptor col, string title) =>
        col.Item().PaddingTop(2).BorderBottom(1).BorderColor(Rule).PaddingBottom(2)
            .Text(title).FontSize(12).SemiBold();

    private static void KeyValues(ColumnDescriptor col, params (string Key, string Value)[] rows) =>
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(c => { c.ConstantColumn(190); c.RelativeColumn(); });
            foreach (var (k, v) in rows)
            {
                table.Cell().PaddingVertical(1).Text(k).FontColor(Muted);
                table.Cell().PaddingVertical(1).Text(v);
            }
        });

    private static void Table(ColumnDescriptor col, (string Header, float Weight)[] columns, IEnumerable<string[]> rows) =>
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(c => { foreach (var (_, w) in columns) c.RelativeColumn(w); });
            foreach (var (h, _) in columns)
                table.Cell().Background(HeadBg).Padding(3).Text(h).FontColor("#FFFFFF").FontSize(8).SemiBold();

            bool zebra = false;
            foreach (var row in rows)
            {
                string bg = zebra ? ZebraBg : "#FFFFFF";
                zebra = !zebra;
                foreach (var cell in row)
                    table.Cell().Background(bg).Padding(3).Text(cell).FontSize(8);
            }
        });

    private static string Weather(Scenario sc) => sc.FlyableModel switch
    {
        RateBasedFlyableDays r => $"Rate pattern · rate {F(r.Rate)}, length {r.PatternLength}, anchor {D(r.Anchor)}",
        ProbabilisticFlyableDays p => $"Probabilistic (seasonal) · phi {F(p.Persistence)}, monthly rates " +
            (p.MonthlyRates.Length == 12 ? $"{F(p.MonthlyRates.Min())}…{F(p.MonthlyRates.Max())} (mean {F(p.MonthlyRates.Average())})" : "-") +
            $", length {p.PatternLength}, anchor {D(p.Anchor)}",
        ExplicitFlyableDays e => $"Explicit · {e.Days.Count} flyable days",
        _ => sc.FlyableModel.GetType().Name,
    };

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    private static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "-";
    private static string F(double d) => double.IsInfinity(d) ? "unlimited" : d.ToString("0.###");
    private static string B(bool b) => b ? "yes" : "no";
}
