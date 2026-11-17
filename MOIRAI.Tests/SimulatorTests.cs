using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class SimulatorTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario TwoParallelCampaigns(int simulators)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(29),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Engineers.Add(new Engineer { Id = "e2", Name = "E2" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "ac2", Name = "T2" });
        for (int i = 1; i <= simulators; i++)
            s.Simulators.Add(new Simulator { Id = $"sim{i}", Name = $"SIM{i}" });

        AddCamp(s, "A", "p1", "e1", "ac1");
        AddCamp(s, "B", "p2", "e2", "ac2");
        return s;

        static void AddCamp(Scenario s, string id, string pilot, string eng, string tail)
        {
            var c = new Campaign
            {
                Id = id, Name = id,
                TotalTestPoints = 3, TestPointsPerSortie = 1,
                StartDate = Start, TargetEndDate = Start.AddDays(60),
            };
            c.CompatibleAircraftIds.Add(tail);
            c.AssignedPilotIds.Add(pilot);
            c.AssignedEngineerIds.Add(eng);
            s.Campaigns.Add(c);
        }
    }

    private static int MaxRehearsalsPerDayBlock(Schedule sch) =>
        sch.Rehearsals.GroupBy(r => (r.Date, r.Block)).Select(g => g.Count()).DefaultIfEmpty(0).Max();

    [Fact]
    public void No_simulator_at_the_base_blocks_rehearsals()
    {
        var s = TwoParallelCampaigns(simulators: 0);
        var sch = new Scheduler().Run(s);

        Assert.All(s.Campaigns, c => Assert.False(sch.CampaignCompleted[c.Id]));
        Assert.All(sch.Bottlenecks.Values, b => Assert.True(b.Rehearsal > 0));
    }

    [Fact]
    public void One_simulator_serializes_competing_rehearsals()
    {
        var one = new Scheduler().Run(TwoParallelCampaigns(simulators: 1));
        var two = new Scheduler().Run(TwoParallelCampaigns(simulators: 2));

        Assert.Equal(1, MaxRehearsalsPerDayBlock(one));
        Assert.All(one.CampaignCompleted.Keys, id => Assert.True(one.CampaignCompleted[id]));

        Assert.True(one.ProgramFinish > two.ProgramFinish,
            $"1-sim finish {one.ProgramFinish} should be later than 2-sim {two.ProgramFinish}");
    }

    [Fact]
    public void Two_simulators_restore_parallel_capacity()
    {
        var two = new Scheduler().Run(TwoParallelCampaigns(simulators: 2));
        var ample = new Scheduler().Run(TwoParallelCampaigns(simulators: 4));

        Assert.Equal(2, MaxRehearsalsPerDayBlock(two));
        Assert.Equal(ample.ProgramFinish, two.ProgramFinish);
    }

    [Fact]
    public void Rehearsal_count_per_block_never_exceeds_available_simulators()
    {
        var s = TwoParallelCampaigns(simulators: 1);
        var sch = new Scheduler().Run(s);

        foreach (var g in sch.Rehearsals.GroupBy(r => (r.Date, r.Block)))
        {
            int avail = s.Simulators.Count(sim => !sim.IsInMaintenance(g.Key.Date));
            Assert.True(g.Count() <= avail,
                $"{g.Count()} rehearsals in block {g.Key.Block} on {g.Key.Date} > {avail} simulators");
        }
    }

    [Fact]
    public void Simulator_maintenance_blocks_rehearsals_on_those_days()
    {
        var s = TwoParallelCampaigns(simulators: 1);
        s.Simulators[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(13) });

        var sch = new Scheduler().Run(s);

        Assert.DoesNotContain(sch.Rehearsals, r => r.Date <= Start.AddDays(13));
        Assert.DoesNotContain(sch.Sorties, so => so.Date <= Start.AddDays(13));
        Assert.All(s.Campaigns, c => Assert.True(sch.CampaignCompleted[c.Id]));
    }

    [Fact]
    public void Single_campaign_unaffected_by_one_simulator()
    {
        Scenario Build(int sims)
        {
            var s = new Scenario
            {
                Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
                Calendar = new FlightCalendar { WeekendDays = new() },
                FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
                HorizonStart = Start,
                HorizonEnd = Start.AddDays(20),
            };
            s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
            s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
            s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "T1" });
            for (int i = 1; i <= sims; i++) s.Simulators.Add(new Simulator { Id = $"s{i}", Name = $"S{i}" });
            var c = new Campaign
            {
                Id = "c1", Name = "c1", TotalTestPoints = 4, TestPointsPerSortie = 1,
                StartDate = Start, TargetEndDate = Start.AddDays(60),
            };
            c.CompatibleAircraftIds.Add("ac1");
            c.AssignedPilotIds.Add("p1");
            c.AssignedEngineerIds.Add("e1");
            s.Campaigns.Add(c);
            return s;
        }

        var one = new Scheduler().Run(Build(1));
        var two = new Scheduler().Run(Build(2));

        Assert.Equal(
            one.Sorties.Select(x => (x.Date, x.Block)),
            two.Sorties.Select(x => (x.Date, x.Block)));
    }
}
