using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class SimulatorCapabilityTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Base(int blocksPerDay, int horizon = 30)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = blocksPerDay },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizon - 1),
        };
        return s;
    }

    private static void TwoCampaigns(Scenario s, int sorties = 3)
    {
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Engineers.Add(new Engineer { Id = "e2", Name = "E2" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "ac2", Name = "T2" });
        Add(s, "A", "p1", "e1", "ac1", sorties);
        Add(s, "B", "p2", "e2", "ac2", sorties);

        static void Add(Scenario s, string id, string p, string e, string tail, int n)
        {
            var c = new Campaign
            {
                Id = id, Name = id, TotalTestPoints = n, TestPointsPerSortie = 1,
                StartDate = Start, TargetEndDate = Start.AddDays(60),
            };
            c.CompatibleAircraftIds.Add(tail);
            c.AssignedPilotIds.Add(p);
            c.AssignedEngineerIds.Add(e);
            s.Campaigns.Add(c);
        }
    }

    private static int MaxRehearsalsPerDay(Schedule sch) =>
        sch.Rehearsals.GroupBy(r => r.Date).Select(g => g.Count()).DefaultIfEmpty(0).Max();

    [Fact]
    public void Device_daily_block_cap_limits_rehearsals_per_day()
    {
        var s = Base(blocksPerDay: 3);
        TwoCampaigns(s);
        s.Simulators.Add(new Simulator { Id = "sim1", Name = "S1", MaxBlocksPerDay = 1 });

        var capped = new Scheduler().Run(s);
        Assert.Equal(1, MaxRehearsalsPerDay(capped));

        s.Simulators[0].MaxBlocksPerDay = 0;
        var uncapped = new Scheduler().Run(s);
        Assert.Equal(2, MaxRehearsalsPerDay(uncapped));
        Assert.True(capped.ProgramFinish > uncapped.ProgramFinish);
    }

    [Fact]
    public void Campaign_restricted_to_a_down_simulator_cannot_rehearse()
    {
        var s = Base(blocksPerDay: 1);
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "T1" });
        var c = new Campaign
        {
            Id = "c1", Name = "c1", TotalTestPoints = 2, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        };
        c.CompatibleAircraftIds.Add("ac1");
        c.AssignedPilotIds.Add("p1");
        c.AssignedEngineerIds.Add("e1");
        s.Campaigns.Add(c);

        var good = new Simulator { Id = "good", Name = "Good" };
        var bad = new Simulator { Id = "bad", Name = "Bad" };
        bad.MaintenanceWindows.Add(new DateRange { Start = Start, End = s.HorizonEnd });
        s.Simulators.Add(good);
        s.Simulators.Add(bad);

        c.CompatibleSimulatorIds.Add("bad");
        var blocked = new Scheduler().Run(s);
        Assert.False(blocked.CampaignCompleted["c1"]);
        Assert.True(blocked.Bottlenecks["c1"].Rehearsal > 0);

        c.CompatibleSimulatorIds.Clear();
        c.CompatibleSimulatorIds.Add("good");
        var ok = new Scheduler().Run(s);
        Assert.True(ok.CampaignCompleted["c1"]);
    }

    [Fact]
    public void A_remote_only_simulator_does_not_serve_main_base_rehearsals()
    {
        var s = Base(blocksPerDay: 1);
        TwoCampaigns(s);
        s.Locations.Add(new Location { Id = "loc1", Name = "Remote" });
        s.Simulators.Add(new Simulator { Id = "rsim", Name = "Remote sim", LocationId = "loc1" });

        var blocked = new Scheduler().Run(s);
        Assert.All(blocked.CampaignCompleted.Values, Assert.False);
        Assert.All(blocked.Bottlenecks.Values, b => Assert.True(b.Rehearsal > 0));

        s.Simulators[0].LocationId = string.Empty;
        var serial = new Scheduler().Run(s);
        Assert.Equal(1, MaxRehearsalsPerDay(serial));
        Assert.All(serial.CampaignCompleted.Values, Assert.True);
    }

    [Fact]
    public void Sim_failures_are_recorded_and_ground_the_device()
    {
        var s = Base(blocksPerDay: 1, horizon: 40);
        TwoCampaigns(s, sorties: 3);
        s.Simulators.Add(new Simulator
        {
            Id = "sim1", Name = "S1",
            FailuresPer100Hours = 100_000, RepairDaysMin = 2, RepairDaysMax = 2,
        });

        var det = new Scheduler().Run(s);
        Assert.Empty(det.SimulatorRepairs);

        var withFailures = new Scheduler().Run(s, LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(7)));
        Assert.NotEmpty(withFailures.SimulatorRepairs["sim1"]);
        Assert.True(withFailures.ProgramFinish >= det.ProgramFinish);
    }

    [Fact]
    public void Zero_rate_simulator_keeps_MC_byte_identical()
    {
        var s = Base(blocksPerDay: 1);
        TwoCampaigns(s);
        s.Simulators.Add(new Simulator { Id = "sim1", Name = "S1" });

        var a = new Scheduler().Run(s, LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(1)));
        var b = new Scheduler().Run(s);

        Assert.Empty(a.SimulatorRepairs);
        Assert.Equal(
            b.Rehearsals.Select(r => (r.Date, r.Block, r.CampaignId)),
            a.Rehearsals.Select(r => (r.Date, r.Block, r.CampaignId)));
    }
}
