using System.Text.Json;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class RestStaggerTests
{
    private static RevisingOptions StaggerOnly() => new() { EnableRestStagger = true };

    [Fact]
    public void Stagger_desynchronizes_rule19_and_finishes_earlier()
    {
        var sc = SevenDayWeek(pilots: 3, engineers: 3);
        var g = new Scheduler().Run(sc);
        var st = new RevisingScheduler(StaggerOnly()).Run(sc);

        Assert.True(g.CampaignCompleted["C1"], "greedy should complete the campaign");
        Assert.True(st.CampaignCompleted["C1"], "stagger should complete the campaign");

        Assert.True(GapDays(g) > 0, "greedy should hit the collective rule-19 stand-down day");

        Assert.Equal(0, GapDays(st));
        Assert.True(st.CampaignEndDates["C1"]!.Value < g.CampaignEndDates["C1"]!.Value,
            $"stagger should finish earlier: stagger={st.CampaignEndDates["C1"]} greedy={g.CampaignEndDates["C1"]}");

        AssertRule19(sc, st);
    }

    [Fact]
    public void With_a_weekend_break_ahead_the_lever_is_a_noop()
    {
        var sc = SevenDayWeek(pilots: 3, engineers: 3);
        sc.Calendar.WeekendDays = new() { DayOfWeek.Saturday, DayOfWeek.Sunday };
        var g = new Scheduler().Run(sc);
        var st = new RevisingScheduler(StaggerOnly()).Run(sc);
        Assert.Equal(Json(g), Json(st));
    }

    [Fact]
    public void A_deadline_sprint_that_finishes_before_the_wall_is_never_disturbed()
    {
        var sc = SevenDayWeek(pilots: 3, engineers: 3);
        sc.Campaigns[0].TotalTestPoints = 18;
        var g = new Scheduler().Run(sc);
        var st = new RevisingScheduler(StaggerOnly()).Run(sc);
        Assert.Equal(Json(g), Json(st));
    }

    [Fact]
    public void A_lone_crew_is_never_voluntarily_rested()
    {
        var sc = SevenDayWeek(pilots: 1, engineers: 1);
        var g = new Scheduler().Run(sc);
        var st = new RevisingScheduler(StaggerOnly()).Run(sc);
        Assert.Equal(Json(g), Json(st));
    }

    private static Scenario SevenDayWeek(int pilots, int engineers)
    {
        var start = new DateOnly(2026, 1, 5);
        var sc = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(150),
        };
        for (int i = 0; i < pilots; i++) sc.Pilots.Add(new Pilot { Id = $"P{i}", Name = $"P{i}" });
        for (int i = 0; i < engineers; i++) sc.Engineers.Add(new Engineer { Id = $"E{i}", Name = $"E{i}" });
        sc.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        var c = new Campaign
        {
            Id = "C1", Name = "C1",
            TotalTestPoints = 90, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(45),
            RehearsalRequired = false,
        };
        c.CompatibleAircraftIds.Add("T1");
        foreach (var p in sc.Pilots) c.AssignedPilotIds.Add(p.Id);
        foreach (var e in sc.Engineers) c.AssignedEngineerIds.Add(e.Id);
        sc.Campaigns.Add(c);
        return sc;
    }

    private static int GapDays(Schedule s)
    {
        var days = s.Sorties.Select(x => x.Date).Distinct().OrderBy(d => d).ToList();
        if (days.Count < 2) return 0;
        return days.Last().DayNumber - days.First().DayNumber + 1 - days.Count;
    }

    private static void AssertRule19(Scenario sc, Schedule s)
    {
        var flying = new Dictionary<string, SortedSet<int>>();
        foreach (var so in s.Sorties)
            foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId)
                         .Concat(so.SupportPilotIds).Concat(so.EngineerIds))
            {
                if (!flying.TryGetValue(pid, out var set)) flying[pid] = set = new SortedSet<int>();
                set.Add(so.Date.DayNumber);
            }
        foreach (var (pid, set) in flying)
        {
            int max = 0, run = 0, prev = int.MinValue;
            foreach (int d in set)
            {
                run = d == prev + 1 ? run + 1 : 1;
                max = Math.Max(max, run);
                prev = d;
            }
            Assert.True(max <= sc.Config.MaxConsecutiveFlyingDays,
                $"{pid} flies {max} consecutive days (cap {sc.Config.MaxConsecutiveFlyingDays})");
        }
    }

    private static string Json(Schedule sch)
    {
        foreach (var list in sch.AutoMaintenance.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.UnscheduledRepairs.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.SimulatorRepairs.Values) foreach (var w in list) w.Id = "";
        return JsonSerializer.Serialize(sch, ScenarioJson.Options);
    }
}
