using System.Text.Json;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class DecisionLogTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Build()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() { DayOfWeek.Saturday, DayOfWeek.Sunday } },
            FlyableModel = new RateBasedFlyableDays { Rate = 0.8, PatternLength = 5, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(40),
        };
        for (int i = 1; i <= 2; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Aircraft.Add(new Aircraft
        {
            Id = "TC-002",
            Name = "Tail-2",
            MaintenanceWindows = { new DateRange { Start = Start.AddDays(7), End = Start.AddDays(20) } },
        });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });

        Campaign Camp(string id, int sorties, DateOnly start, params string[] prereqs)
        {
            var c = new Campaign
            {
                Id = id,
                Name = id,
                TotalTestPoints = sorties,
                TestPointsPerSortie = 1,
                StartDate = start,
                TargetEndDate = Start.AddDays(35),
            };
            c.PrerequisiteCampaignIds.AddRange(prereqs);
            c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
            c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
            c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
            s.Campaigns.Add(c);
            return c;
        }

        Camp("PERF", 5, Start);
        Camp("ENV", 4, Start, "PERF");
        return s;
    }

    private static string Json(Schedule sch)
    {
        foreach (var list in sch.AutoMaintenance.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.UnscheduledRepairs.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.SimulatorRepairs.Values) foreach (var w in list) w.Id = "";
        return JsonSerializer.Serialize(sch, ScenarioJson.Options);
    }

    [Fact]
    public void Capture_does_not_change_the_greedy_schedule()
    {
        var s = Build();
        var plain = new Scheduler().Run(s);
        var captured = new Scheduler { CaptureDecisions = true }.Run(s);

        Assert.Equal(Json(plain), Json(captured));
        Assert.Empty(plain.DecisionLog);
        Assert.NotEmpty(captured.DecisionLog);
    }

    [Fact]
    public void Greedy_log_is_chronological_and_records_placements_waits_and_outcomes()
    {
        var s = Build();
        var sch = new Scheduler { CaptureDecisions = true }.Run(s);
        var log = sch.DecisionLog;

        for (int i = 1; i < log.Count; i++)
            Assert.True(log[i].DayIndex >= log[i - 1].DayIndex, "decision log is not day-ordered");

        Assert.Contains(log, e => e.Kind == DecisionKind.Placed && e.CampaignId == "PERF");
        Assert.Contains(log, e => e.Kind == DecisionKind.Waiting && e.CampaignId == "ENV"
                                  && e.Message.Contains("PERF"));
        Assert.Contains(log, e => e.Kind == DecisionKind.Outcome && e.CampaignId == "PERF");
        Assert.Contains(log, e => e.Kind == DecisionKind.Outcome && e.CampaignId == "ENV");
    }

    [Fact]
    public void Capture_does_not_change_the_advanced_schedule_with_levers_off()
    {
        var s = Build();
        var plain = new RevisingScheduler(RevisingOptions.Off).Run(s);
        var captured = new RevisingScheduler(RevisingOptions.Off) { CaptureDecisions = true }.Run(s);

        Assert.Equal(Json(plain), Json(captured));
        Assert.Empty(plain.DecisionLog);
        Assert.NotEmpty(captured.DecisionLog);
    }

    [Fact]
    public void Capture_does_not_change_the_advanced_schedule_with_search_on()
    {
        var s = Build();
        var opts = new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableScarcityCrew = true,
            EnableBottleneckSteer = true, EnableGlobalSearch = true, GlobalSearchIterations = 400,
        };
        var plain = new RevisingScheduler(opts).Run(s);
        var captured = new RevisingScheduler(opts) { CaptureDecisions = true }.Run(s);

        Assert.Equal(Json(plain), Json(captured));
        Assert.NotEmpty(captured.DecisionLog);
        Assert.Contains(captured.DecisionLog, e => e.Kind == DecisionKind.Search);
    }
}
