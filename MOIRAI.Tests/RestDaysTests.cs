using System.Text.Json;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class RestDaysTests
{
    private static readonly DateOnly Start = new(2026, 1, 5);

    private static Scenario Chain(int restDays, bool rehearsals = false)
    {
        var sc = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(60),
        };
        sc.Pilots.Add(new Pilot { Id = "P1", Name = "P1" });
        sc.Engineers.Add(new Engineer { Id = "E1", Name = "E1" });
        sc.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        if (rehearsals) sc.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });

        var a = new Campaign
        {
            Id = "A", Name = "A",
            TotalTestPoints = 4, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(10),
            RehearsalRequired = rehearsals,
            RestDaysAfterCompletion = restDays,
        };
        a.CompatibleAircraftIds.Add("T1");
        a.AssignedPilotIds.Add("P1");
        a.AssignedEngineerIds.Add("E1");
        var b = new Campaign
        {
            Id = "B", Name = "B",
            TotalTestPoints = 3, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(40),
            RehearsalRequired = rehearsals,
        };
        b.CompatibleAircraftIds.Add("T1");
        b.AssignedPilotIds.Add("P1");
        b.AssignedEngineerIds.Add("E1");
        b.PrerequisiteCampaignIds.Add("A");
        sc.Campaigns.Add(a);
        sc.Campaigns.Add(b);
        return sc;
    }

    [Fact]
    public void Rest_days_delay_the_successor_and_zero_is_the_old_behavior()
    {
        var baseline = new Scheduler().Run(Chain(restDays: 0));
        Assert.Equal(new DateOnly(2026, 1, 8), baseline.CampaignEndDates["A"]);
        Assert.Equal(new DateOnly(2026, 1, 9), FirstSortie(baseline, "B"));

        var rested = new Scheduler().Run(Chain(restDays: 5));
        Assert.Equal(new DateOnly(2026, 1, 8), rested.CampaignEndDates["A"]);
        Assert.Equal(new DateOnly(2026, 1, 14), FirstSortie(rested, "B"));
        Assert.True(rested.CampaignCompleted["B"]);
    }

    [Fact]
    public void Advanced_engine_with_levers_off_is_byte_identical_with_rest_days()
    {
        foreach (int rest in new[] { 0, 3, 7 })
        {
            var sc = Chain(rest);
            var g = new Scheduler().Run(sc);
            var off = new RevisingScheduler(RevisingOptions.Off).Run(sc);
            Assert.True(Json(g) == Json(off), $"rest={rest}: advanced-OFF diverged from greedy");
        }
    }

    [Fact]
    public void Rest_blocks_rehearsals_too()
    {
        var sch = new Scheduler().Run(Chain(restDays: 5, rehearsals: true));
        Assert.Equal(new DateOnly(2026, 1, 9), sch.CampaignEndDates["A"]);
        var restStart = new DateOnly(2026, 1, 10);
        var restEnd = new DateOnly(2026, 1, 14);
        Assert.DoesNotContain(sch.Rehearsals, r => r.Date >= restStart && r.Date <= restEnd);
        Assert.DoesNotContain(sch.Sorties, s => s.Date >= restStart && s.Date <= restEnd);
        Assert.Equal(new DateOnly(2026, 1, 16), FirstSortie(sch, "B"));
    }

    [Fact]
    public void An_unrelated_campaign_keeps_flying_through_the_rest_window()
    {
        var sc = Chain(restDays: 5);
        sc.Pilots.Add(new Pilot { Id = "P2", Name = "P2" });
        sc.Engineers.Add(new Engineer { Id = "E2", Name = "E2" });
        sc.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        var c = new Campaign
        {
            Id = "C", Name = "C",
            TotalTestPoints = 20, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(40),
            RehearsalRequired = false,
        };
        c.CompatibleAircraftIds.Add("T2");
        c.AssignedPilotIds.Add("P2");
        c.AssignedEngineerIds.Add("E2");
        sc.Campaigns.Add(c);

        var sch = new Scheduler().Run(sc);
        Assert.Contains(sch.Sorties, s => s.CampaignId == "C"
            && s.Date >= new DateOnly(2026, 1, 9) && s.Date <= new DateOnly(2026, 1, 13));
    }

    [Fact]
    public void Actual_completion_anchors_the_rest_window_across_the_freeze()
    {
        var sc = Chain(restDays: 5);
        for (int k = 0; k < 4; k++)
            sc.ActualSorties.Add(new Sortie
            {
                CampaignId = "A", Date = Start.AddDays(k), Block = 1, SortieNo = k + 1,
                AircraftId = "T1", PilotId = "P1", EngineerIds = new List<string> { "E1" },
                TestPointsFlown = 1,
            });
        sc.FreezeDate = new DateOnly(2026, 1, 9);

        foreach (var (name, sch) in new (string, Schedule)[]
                 {
                     ("greedy", new Scheduler().Run(sc)),
                     ("advanced-off", new RevisingScheduler(RevisingOptions.Off).Run(sc)),
                 })
        {
            Assert.True(sch.CampaignCompleted["A"], $"{name}: A should be completed by actuals");
            Assert.Equal(new DateOnly(2026, 1, 8), sch.CampaignEndDates["A"]);
            var firstNew = sch.Sorties.Where(s => s.CampaignId == "B" && !s.IsActual)
                .Select(s => s.Date).Min();
            Assert.True(firstNew == new DateOnly(2026, 1, 14),
                $"{name}: B replans at {firstNew}, expected 2026-01-14");
        }
    }

    [Fact]
    public void Smart_engine_honours_rest_days_and_is_never_worse_than_greedy()
    {
        var sc = Chain(restDays: 5);
        var smart = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true,
        }).Run(sc);
        Assert.DoesNotContain(smart.Sorties,
            s => s.Date >= new DateOnly(2026, 1, 9) && s.Date <= new DateOnly(2026, 1, 13));
        Assert.True(smart.CampaignCompleted["B"]);
        Assert.True(smart.CampaignEndDates["B"] <= new Scheduler().Run(sc).CampaignEndDates["B"],
            "smart finished B later than greedy");
    }

    [Fact]
    public void Repair_never_relocates_a_sortie_into_a_rest_window_that_opens_later()
    {
        var sc = new Scenario
        {
            Config = new ScenarioConfig
            {
                PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3,
                MaxFlightHoursPerDay = 3,
            },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(60),
        };
        sc.Pilots.Add(new Pilot { Id = "P1", Name = "P1" });
        sc.Engineers.Add(new Engineer { Id = "E1", Name = "E1" });
        foreach (var t in new[] { "T-H", "T-L", "T-M" }) sc.Aircraft.Add(new Aircraft { Id = t, Name = t });
        sc.Aircraft[2].MaintenanceWindows.Add(new DateRange { Start = Start.AddDays(6), End = Start.AddDays(10) });

        Campaign Mk(string id, double tp, string tail, int startDay, int targetDay, int rest = 0)
        {
            var c = new Campaign
            {
                Id = id, Name = id,
                TotalTestPoints = tp, TestPointsPerSortie = 1,
                StartDate = Start.AddDays(startDay), TargetEndDate = Start.AddDays(targetDay),
                ExpectedSortieFlightHours = 3, ExpectedSortieDutyHours = 4,
                RehearsalRequired = false,
                RestDaysAfterCompletion = rest,
            };
            c.CompatibleAircraftIds.Add(tail);
            c.AssignedPilotIds.Add("P1");
            c.AssignedEngineerIds.Add("E1");
            return c;
        }
        sc.Campaigns.Add(Mk("H", 1, "T-H", 6, 8));
        sc.Campaigns.Add(Mk("L", 4, "T-L", 6, 20, rest: 5));
        sc.Campaigns.Add(Mk("M", 20, "T-M", 0, 50));

        var smart = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(sc);
        var end = smart.CampaignEndDates["L"];
        Assert.NotNull(end);
        var lo = end!.Value.AddDays(1);
        var hi = end.Value.AddDays(5);
        var offenders = smart.Sorties
            .Where(s => s.Date >= lo && s.Date <= hi
                        && (s.PilotId == "P1" || s.EngineerIds.Contains("E1") || s.AircraftId == "T-L"))
            .Select(s => $"{s.CampaignId}@{s.Date:yyyy-MM-dd}")
            .ToList();
        Assert.True(offenders.Count == 0,
            $"work inside L's rest window {lo:yyyy-MM-dd}..{hi:yyyy-MM-dd}: {string.Join(", ", offenders)}");
    }

    private static DateOnly FirstSortie(Schedule sch, string campId) =>
        sch.Sorties.Where(s => s.CampaignId == campId).Select(s => s.Date).Min();

    private static string Json(Schedule sch)
    {
        foreach (var list in sch.AutoMaintenance.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.UnscheduledRepairs.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.SimulatorRepairs.Values) foreach (var w in list) w.Id = "";
        return JsonSerializer.Serialize(sch, ScenarioJson.Options);
    }
}
