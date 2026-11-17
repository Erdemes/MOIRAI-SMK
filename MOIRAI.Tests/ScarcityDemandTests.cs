using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class ScarcityDemandTests
{
    private static readonly DateOnly Start = new(2026, 1, 5);

    private static readonly RevisingOptions Scarcity = new() { EnableScarcityCrew = true };

    private static Scenario Scenario(int? fStartDay, int fPoints = 3)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 0, MaxSortiesPerPersonPerDay = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(80),
        };
        s.Aircraft.Add(new Aircraft { Id = "D", Name = "D" });
        s.Aircraft.Add(new Aircraft { Id = "F", Name = "F" });
        s.Pilots.Add(new Pilot { Id = "S", Name = "S", TypeRatings = { "D" } });
        s.Pilots.Add(new Pilot { Id = "M1", Name = "M1", TypeRatings = { "D", "F" } });
        s.Pilots.Add(new Pilot { Id = "M2", Name = "M2", TypeRatings = { "D", "F" } });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 12, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(40), RehearsalRequired = false,
            CompatibleAircraftIds = { "D" }, AssignedPilotIds = { "S", "M1", "M2" },
        });
        if (fStartDay is { } f)
            s.Campaigns.Add(new Campaign
            {
                Id = "F", Name = "F", TotalTestPoints = fPoints, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
                StartDate = Start.AddDays(f), TargetEndDate = Start.AddDays(70), RehearsalRequired = false,
                CompatibleAircraftIds = { "F" }, AssignedPilotIds = { "M1", "M2" },
            });
        return s;
    }

    private static int Flown(Schedule s, string pilot) =>
        s.Sorties.Count(x => x.CampaignId == "A" && x.PilotId == pilot);

    [Fact]
    public void Without_other_type_demand_the_crew_is_load_balanced()
    {
        var sch = new CoreForwardScheduler(Scarcity).Run(Scenario(fStartDay: null), LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(1)));
        Assert.True(Flown(sch, "M1") > 0);
        Assert.True(Flown(sch, "M2") > 0);
        Assert.InRange(Flown(sch, "S"), 3, 6);
    }

    [Fact]
    public void Concurrent_other_type_demand_holds_the_multi_rated_crew_back()
    {
        var sch = new CoreForwardScheduler(Scarcity).Run(Scenario(fStartDay: 0, fPoints: 40), LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(1)));
        Assert.True(Flown(sch, "S") >= 10);
    }

    [Fact]
    public void Far_future_demand_does_not_hold_crew_back_until_it_comes_into_the_window()
    {
        var sch = new CoreForwardScheduler(Scarcity).Run(Scenario(fStartDay: 60), LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(1)));
        Assert.True(Flown(sch, "M1") > 0);
        Assert.True(Flown(sch, "M2") > 0);
        Assert.InRange(Flown(sch, "S"), 3, 6);
    }

    [Fact]
    public void Tail_choice_lets_the_least_loaded_pilot_fly()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 0 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(40),
        };
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Pilots.Add(new Pilot { Id = "M", Name = "M", TypeRatings = { "T1", "T2" } });
        s.Pilots.Add(new Pilot { Id = "S", Name = "S", TypeRatings = { "T1" } });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 12, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(30), RehearsalRequired = false,
            CompatibleAircraftIds = { "T1", "T2" }, AssignedPilotIds = { "M", "S" },
        });

        var greedy = new Scheduler().Run(s);
        Assert.True(Flown(greedy, "M") >= 7);
        var smart = new CoreForwardScheduler(Scarcity).Run(s, LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(1)));
        Assert.True(Flown(smart, "M") < Flown(greedy, "M"), $"smart M={Flown(smart, "M")} S={Flown(smart, "S")}, greedy M={Flown(greedy, "M")}");
        Assert.InRange(Flown(smart, "M") - Flown(smart, "S"), -2, 2);
    }

    [Fact]
    public void Failed_rehearsal_tries_another_crew_before_losing_the_day()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 0 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(30),
        };
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "SIM" });
        var p1 = new Pilot { Id = "P1", Name = "P1" };
        p1.LeaveWindows.Add(new DateRange { Start = Start, End = Start.AddDays(2) });
        s.Pilots.Add(p1);
        s.Pilots.Add(new Pilot { Id = "P2", Name = "P2" });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 3, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Start.AddDays(3), TargetEndDate = Start.AddDays(20),
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "P1", "P2" },
        });

        DateOnly First(Schedule sch) => sch.Sorties.Where(x => x.CampaignId == "A").Min(x => x.Date);
        Assert.True(First(new Scheduler().Run(s)) > Start.AddDays(3));
        var smart = new CoreForwardScheduler(new RevisingOptions { EnableLocalRepair = true })
            .Run(s, LosslessYieldModel.Instance, new StochasticFailureModel(new Random(1)));
        Assert.Equal(Start.AddDays(3), First(smart));
        Assert.Equal("P2", smart.Sorties.Where(x => x.CampaignId == "A").MinBy(x => x.Date)!.PilotId);
    }

    [Fact]
    public void Monte_carlo_runs_greedy_when_the_plan_floored_to_greedy()
    {
        var sc = Scenario(fStartDay: 60);
        sc.FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0, Start);
        var plan = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableScarcityCrew = true,
            EnableBottleneckSteer = true, EnableRestStagger = true,
        }).Run(sc, (IProgress<double>?)null, CancellationToken.None);
        var replay = MonteCarlo.ReplayRun(sc, 0).Schedule;
        foreach (var p in new[] { "S", "M1", "M2" })
            Assert.Equal(Flown(plan, p), Flown(replay, p));
    }
}
