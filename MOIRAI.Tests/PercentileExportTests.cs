using ClosedXML.Excel;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class PercentileExportTests
{
    private static readonly DateOnly Start = new(2026, 3, 2);

    private static Scenario Probabilistic()
    {
        var sc = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() { DayOfWeek.Saturday, DayOfWeek.Sunday } },
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.7, 12).ToArray(),
                Persistence = 0.3, PatternLength = 7, Anchor = Start,
            },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(80),
        };
        sc.Pilots.Add(new Pilot { Id = "P1", Name = "P1" });
        sc.Pilots.Add(new Pilot { Id = "P2", Name = "P2" });
        sc.Engineers.Add(new Engineer { Id = "E1", Name = "E1" });
        sc.Engineers.Add(new Engineer { Id = "E2", Name = "E2" });
        sc.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        var c = new Campaign
        {
            Id = "C1", Name = "C1",
            TotalTestPoints = 20, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(50),
            RehearsalRequired = false,
        };
        c.CompatibleAircraftIds.Add("T1");
        foreach (var p in sc.Pilots) c.AssignedPilotIds.Add(p.Id);
        foreach (var e in sc.Engineers) c.AssignedEngineerIds.Add(e.Id);
        sc.Campaigns.Add(c);
        return sc;
    }

    [Fact]
    public void ReplayFinishPercentiles_reconstructs_the_exact_percentile_runs()
    {
        var sc = Probabilistic();
        var mc = MonteCarlo.Analyze(sc, runs: 40, seed: 123);
        var pct = MonteCarlo.ReplayFinishPercentiles(sc, mc, seed: 123);

        Assert.Equal(3, pct.Count);
        var byLabel = pct.ToDictionary(t => t.Label);
        Assert.Equal(mc.FinishP10, byLabel["P10"].Schedule.ProgramFinish);
        Assert.Equal(mc.FinishP50, byLabel["P50"].Schedule.ProgramFinish);
        Assert.Equal(mc.FinishP90, byLabel["P90"].Schedule.ProgramFinish);
        foreach (var (_, _, sch) in pct) Assert.NotEmpty(sch.Sorties);

        var again = MonteCarlo.ReplayFinishPercentiles(sc, mc, seed: 123);
        Assert.Equal(pct.Select(t => t.Schedule.Sorties.Count), again.Select(t => t.Schedule.Sorties.Count));
    }

    [Fact]
    public void Analysis_export_embeds_a_schedule_sheet_set_per_percentile()
    {
        var sc = Probabilistic();
        var mc = MonteCarlo.Analyze(sc, runs: 40, seed: 123);
        var pct = MonteCarlo.ReplayFinishPercentiles(sc, mc, seed: 123);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-pct-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.ExportAnalysis(sc, mc, null, null, path, pct);

            using var wb = new XLWorkbook(path);
            var names = wb.Worksheets.Select(w => w.Name).ToList();
            Assert.Contains("MC-Risk", names);
            foreach (var label in new[] { "P10", "P50", "P90" })
                foreach (var sheet in new[] { "Summary", "Sorties", "Aircraft-Utilization", "Aircraft-Calendar", "Crew-Calendar" })
                    Assert.Contains($"{label} {sheet}", names);

            var p50 = pct.Single(t => t.Label == "P50").Schedule;
            var ws = wb.Worksheet("P50 Sorties");
            int rows = ws.LastRowUsed()!.RowNumber() - 1;
            Assert.Equal(p50.Sorties.Count, rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Analysis_export_without_percentiles_is_unchanged()
    {
        var sc = Probabilistic();
        var mc = MonteCarlo.Analyze(sc, runs: 20, seed: 123);

        string path = Path.Combine(Path.GetTempPath(), $"moirai-noplain-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.ExportAnalysis(sc, mc, null, null, path);
            using var wb = new XLWorkbook(path);
            Assert.DoesNotContain(wb.Worksheets, w => w.Name.StartsWith("P10 ")
                || w.Name.StartsWith("P50 ") || w.Name.StartsWith("P90 "));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
