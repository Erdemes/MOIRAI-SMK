using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class FailureTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Scenario(int horizonDays, int aircraft = 1, double failureRate = 0)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= 2; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= 2; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        for (int i = 1; i <= aircraft; i++)
            s.Aircraft.Add(new Aircraft
            {
                Id = $"ac{i}",
                Name = $"Tail-{i}",
                FailuresPer100FlightHours = failureRate,
                RepairDaysMin = 2,
                RepairDaysMax = 2,
            });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, int requiredSorties, double flightHours = 1)
    {
        var c = new Campaign
        {
            Id = "c1",
            Name = "c1",
            TotalTestPoints = requiredSorties,
            TestPointsPerSortie = 1,
            StartDate = Start,
            TargetEndDate = Start.AddDays(60),
            ExpectedSortieFlightHours = flightHours,
            BlocksPerSortie = 3,
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Deterministic_run_never_fails_even_with_a_rate_set()
    {
        var s = Scenario(horizonDays: 20, failureRate: 50);
        Camp(s, requiredSorties: 5);

        var sch = new Scheduler().Run(s);

        Assert.Empty(sch.UnscheduledRepairs);
        Assert.Equal(5, sch.Sorties.Count);
    }

    [Fact]
    public void Certain_failure_grounds_the_tail_from_the_next_day()
    {
        var s = Scenario(horizonDays: 30, failureRate: 100_000);
        Camp(s, requiredSorties: 3);

        var sch = new Scheduler().Run(s, LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(42)));

        var repairs = sch.UnscheduledRepairs["ac1"];
        Assert.Equal(3, repairs.Count);
        var first = sch.Sorties.Min(x => x.Date);
        Assert.Equal(first.AddDays(1), repairs.Min(w => w.Start));

        var dates = sch.Sorties.Select(x => x.Date).OrderBy(d => d).ToList();
        for (int i = 1; i < dates.Count; i++)
            Assert.True(dates[i].DayNumber - dates[i - 1].DayNumber >= 3,
                $"sortie on {dates[i]} ignores the repair after {dates[i - 1]}");
    }

    [Fact]
    public void Zero_rate_consumes_no_randomness()
    {
        var s1 = Scenario(horizonDays: 20);
        Camp(s1, requiredSorties: 5);
        var s2 = Scenario(horizonDays: 20);
        Camp(s2, requiredSorties: 5);

        var a = new Scheduler().Run(s1, LosslessYieldModel.Instance,
            new StochasticFailureModel(new Random(7)));
        var b = new Scheduler().Run(s2);

        Assert.Empty(a.UnscheduledRepairs);
        Assert.Equal(
            b.Sorties.Select(x => (x.Date, x.Block, x.AircraftId, x.PilotId)),
            a.Sorties.Select(x => (x.Date, x.Block, x.AircraftId, x.PilotId)));
    }

    [Fact]
    public void Failures_lower_the_MC_delivery_probability()
    {
        var s = Scenario(horizonDays: 45, failureRate: 8);
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.8, 0.4, Start);
        var c = Camp(s, requiredSorties: 20);
        s.MasterDeadline = Start.AddDays(34);
        c.TargetEndDate = s.MasterDeadline.Value;

        var without = MonteCarlo.Analyze(s, runs: 200, seed: 99,
            sources: MonteCarlo.UncertaintySources.Weather);
        var with = MonteCarlo.Analyze(s, runs: 200, seed: 99,
            sources: MonteCarlo.UncertaintySources.Weather | MonteCarlo.UncertaintySources.Failures);

        Assert.True(with.PDelivery < without.PDelivery,
            $"failures should hurt: with={with.PDelivery:P0}, without={without.PDelivery:P0}");
    }

    [Fact]
    public void MC_with_failures_is_reproducible()
    {
        var s = Scenario(horizonDays: 45, failureRate: 8);
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.8, 0.4, Start);
        Camp(s, requiredSorties: 15);

        var a = MonteCarlo.Analyze(s, runs: 100, seed: 5);
        var b = MonteCarlo.Analyze(s, runs: 100, seed: 5);

        Assert.Equal(a.PDelivery, b.PDelivery);
        Assert.Equal(a.FinishP50, b.FinishP50);
    }
}
