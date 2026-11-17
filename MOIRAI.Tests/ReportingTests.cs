using ClosedXML.Excel;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class ReportingTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static FlightCalendar NoWeekend() => new() { WeekendDays = new() };
    private static FlyableDayModel AllFlyable() => new RateBasedFlyableDays { Rate = 1, PatternLength = 1 };

    private static Scenario SimpleScenario(int horizonDays, Campaign camp) => new()
    {
        Pilots = { new Pilot { Id = "p1", Name = "Pilot1" } },
        Engineers = { new Engineer { Id = "e1", Name = "Eng1" } },
        Aircraft = { new Aircraft { Id = "ac1", Name = "Tail-1" } },
        Simulators = { new Simulator { Id = "SIM", Name = "Main sim" } },
        Campaigns = { camp },
        Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
        Calendar = NoWeekend(),
        FlyableModel = AllFlyable(),
        HorizonStart = Start,
        HorizonEnd = Start.AddDays(horizonDays - 1),
    };

    private static Campaign Camp(string name, int requiredSorties, DateOnly? start = null, DateOnly? target = null) => new()
    {
        Id = "c1",
        Name = name,
        TotalTestPoints = requiredSorties,
        TestPointsPerSortie = 1,
        CompatibleAircraftIds = { "ac1" },
        AssignedPilotIds = { "p1" },
        AssignedEngineerIds = { "e1" },
        StartDate = start ?? Start,
        TargetEndDate = target ?? Start.AddDays(60),
    };

    [Fact]
    public void Report_names_aircraft_as_dominant_bottleneck_for_a_maintenance_blocked_late_campaign()
    {
        var camp = Camp("FT-FLUT", requiredSorties: 2, target: Start.AddDays(3));
        var s = SimpleScenario(horizonDays: 12, camp);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(4) });

        var schedule = new Scheduler().Run(s);
        var report = BottleneckReport.Build(schedule, s);

        Assert.Contains("FT-FLUT [Late]", report);
        Assert.Contains("dominant bottleneck = aircraft (5 days)", report);
    }

    [Fact]
    public void Report_shows_Met_and_no_blocking_days_for_an_unblocked_campaign()
    {
        var camp = Camp("FT-PERF", requiredSorties: 2, start: Start.AddDays(2));
        var s = SimpleScenario(horizonDays: 12, camp);

        var schedule = new Scheduler().Run(s);
        var report = BottleneckReport.Build(schedule, s);

        Assert.True(schedule.CampaignCompleted["c1"]);
        Assert.Contains("FT-PERF [Met]", report);
        Assert.Contains("no blocking days", report);
        Assert.DoesNotContain("dominant bottleneck", report);
    }

    [Fact]
    public void Excel_export_with_maintenance_and_leave_windows_writes_a_workbook()
    {
        var camp = Camp("FT-ENV", requiredSorties: 2, start: Start.AddDays(2));
        var s = SimpleScenario(horizonDays: 12, camp);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(1) });
        s.Engineers[0].LeaveWindows.Add(new DateRange { Start = Start.AddDays(10), End = Start.AddDays(11) });

        var schedule = new Scheduler().Run(s);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.Export(s, schedule, path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Excel_summary_includes_an_A_level_phase_rollup_row()
    {
        var s = SimpleScenario(horizonDays: 12, new Campaign
        {
            Id = "c1", Name = "Child", GroupId = "G",
            TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "ac1" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            StartDate = Start, TargetEndDate = DateOnly.MaxValue,
        });
        s.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Envelope Expansion", TargetEndDate = Start.AddDays(5) });

        var schedule = new Scheduler().Run(s);
        string path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.Export(s, schedule, path);
            using var wb = new XLWorkbook(path);
            var summary = wb.Worksheet("Summary");
            Assert.Contains(summary.CellsUsed(), c => c.GetString() == "Envelope Expansion");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Excel_export_has_an_aircraft_utilization_sheet_with_per_tail_totals()
    {
        var camp = Camp("FT-ENV", requiredSorties: 3, start: Start.AddDays(2));
        var s = SimpleScenario(horizonDays: 16, camp);
        var schedule = new Scheduler().Run(s);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.Export(s, schedule, path);
            using var wb = new XLWorkbook(path);
            Assert.True(wb.Worksheets.Contains("Aircraft-Utilization"));
            var ws = wb.Worksheet("Aircraft-Utilization");
            Assert.Equal("Aircraft", ws.Cell(1, 1).GetString());
            Assert.Equal(schedule.Sorties.Count, (int)ws.Cell(2, 3).GetDouble());
            Assert.Equal(schedule.Sorties.Select(x => x.Date).Distinct().Count(), (int)ws.Cell(2, 4).GetDouble());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Analysis_export_writes_only_the_sections_that_were_run()
    {
        var camp = Camp("FT-ENV", requiredSorties: 4, start: Start.AddDays(2), target: Start.AddDays(12));
        var s = SimpleScenario(horizonDays: 24, camp);
        s.FlyableModel = new ProbabilisticFlyableDays
        {
            MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
            Persistence = 0.4, PatternLength = 7, Anchor = Start,
        };
        s.MasterDeadline = Start.AddDays(12);

        var mc = MonteCarlo.Analyze(s, runs: 30, seed: 3);
        var sens = MonteCarlo.Sensitivity(s, runs: 15, seed: 3, sobolSamples: 16);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.ExportAnalysis(s, mc, decomposition: null, sensitivity: sens, path);
            using var wb = new XLWorkbook(path);
            Assert.True(wb.Worksheets.Contains("MC-Risk"));
            Assert.True(wb.Worksheets.Contains("Sensitivity"));
            Assert.DoesNotContain(wb.Worksheets, w => w.Name == "Risk-Decomposition");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Pdf_report_writes_a_nontrivial_file()
    {
        var camp = Camp("FT-ENV", requiredSorties: 3, start: Start.AddDays(2));
        var s = SimpleScenario(horizonDays: 16, camp);
        var schedule = new Scheduler().Run(s);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfReporter.Export(s, schedule, path);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 2000, "expected a real multi-section PDF");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Pdf_report_includes_the_analysis_sections_when_supplied()
    {
        var camp = Camp("FT-ENV", requiredSorties: 4, start: Start.AddDays(2), target: Start.AddDays(12));
        var s = SimpleScenario(horizonDays: 24, camp);
        s.FlyableModel = new ProbabilisticFlyableDays
        {
            MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
            Persistence = 0.4, PatternLength = 7, Anchor = Start,
        };
        s.MasterDeadline = Start.AddDays(12);
        var schedule = new Scheduler().Run(s);
        var mc = MonteCarlo.Analyze(s, runs: 25, seed: 3);
        var sens = MonteCarlo.Sensitivity(s, runs: 12, seed: 3, sobolSamples: 16);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfReporter.Export(s, schedule, path, mc, decomposition: null, sensitivity: sens);
            Assert.True(new FileInfo(path).Length > 2000);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
