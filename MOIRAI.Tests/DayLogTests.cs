using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class DayLogTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Scenario(int horizonDays, FlyableDayModel? flyable = null)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = flyable ?? new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= 2; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= 2; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "Tail-1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, string id, int requiredSorties,
        DateOnly? start = null, params string[] prereqs)
    {
        var c = new Campaign
        {
            Id = id,
            Name = id,
            TotalTestPoints = requiredSorties,
            TestPointsPerSortie = 1,
            StartDate = start ?? Start,
            TargetEndDate = Start.AddDays(60),
            BlocksPerSortie = 3,
        };
        c.PrerequisiteCampaignIds.AddRange(prereqs);
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Log_spans_the_horizon_and_records_placed_and_idle_days()
    {
        var s = Scenario(horizonDays: 10);
        var c = Camp(s, "c1", requiredSorties: 3);

        var sch = new Scheduler().Run(s);
        string log = sch.DayLog[c.Id];

        Assert.Equal(10, log.Length);
        Assert.Equal("r...------", log);
    }

    [Fact]
    public void Log_agrees_with_the_tally_counters()
    {
        var s = Scenario(horizonDays: 30,
            flyable: new RateBasedFlyableDays { Rate = 0.7, PatternLength = 5, Anchor = Start });
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start.AddDays(4), End = Start.AddDays(8) });
        var a = Camp(s, "a", requiredSorties: 4);
        var b = Camp(s, "b", requiredSorties: 3, prereqs: "a");

        var sch = new Scheduler().Run(s);

        foreach (var camp in s.Campaigns)
        {
            var t = sch.Bottlenecks[camp.Id];
            string log = sch.DayLog[camp.Id];
            Assert.Equal(t.Placed, log.Count(ch => ch == DayLogCodes.Placed));
            Assert.Equal(t.NotUsable, log.Count(ch => ch == DayLogCodes.NotUsable));
            Assert.Equal(t.Prereq, log.Count(ch => ch == DayLogCodes.Prereq));
            Assert.Equal(t.Aircraft, log.Count(ch => ch == DayLogCodes.Aircraft));
            Assert.Equal(t.Pilot, log.Count(ch => ch == DayLogCodes.Pilot));
            Assert.Equal(t.Engineer, log.Count(ch => ch == DayLogCodes.Engineer));
            Assert.Equal(t.Rehearsal, log.Count(ch => ch == DayLogCodes.Rehearsal));
        }

        Assert.Contains(DayLogCodes.Prereq, sch.DayLog[b.Id]);
        Assert.Contains(DayLogCodes.Aircraft, sch.DayLog[a.Id]);
    }

    [Fact]
    public void Frozen_history_stays_idle_in_the_log()
    {
        var s = Scenario(horizonDays: 20);
        var c = Camp(s, "c1", requiredSorties: 2);
        s.FreezeDate = Start.AddDays(5);

        var sch = new Scheduler().Run(s);
        string log = sch.DayLog[c.Id];

        Assert.All(log[..5], ch => Assert.Equal(DayLogCodes.Idle, ch));
    }

    [Fact]
    public void PhaseSummary_reads_chronological_runs_and_skips_weekend_noise()
    {
        string log = "www" + "aa" + "ww" + "aaa" + "p" + "..." + "---";
        Assert.Equal("weather 3d → aircraft 5d → pilot 1d", DayLogCodes.PhaseSummary(log));

        Assert.Equal(string.Empty, DayLogCodes.PhaseSummary("...---"));

        Assert.Equal("weather 5d → aircraft 2d", DayLogCodes.PhaseSummary("wwwwwaa"));
    }

    [Fact]
    public void Bottleneck_report_includes_the_phase_line()
    {
        var s = Scenario(horizonDays: 30);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(9) });
        var c = Camp(s, "c1", requiredSorties: 2);

        var sch = new Scheduler().Run(s);
        string report = BottleneckReport.Build(sch, s);

        Assert.Contains("phases:", report);
        Assert.Contains("aircraft 10d", report);
    }
}
