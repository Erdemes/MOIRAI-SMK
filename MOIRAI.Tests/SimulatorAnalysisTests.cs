using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class SimulatorAnalysisTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario SimScenario(int devices, int maxBlocksPerDay, double totalTp, int targetDays)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar(),
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.7, 12).ToArray(),
                Persistence = 0.5,
                Anchor = Start,
            },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(120),
        };
        s.Engineers.AddRange(new[] { "E1", "E2" }.Select(id => new Engineer { Id = id, Name = id }));
        for (int d = 1; d <= devices; d++)
            s.Simulators.Add(new Simulator { Id = $"SIM-{d}", Name = $"SIM-{d}", MaxBlocksPerDay = maxBlocksPerDay });
        s.Campaigns.Add(new Campaign
        {
            Id = "DEV", Name = "DEV",
            Venue = CampaignVenue.Simulator,
            TotalTestPoints = totalTp, TestPointsPerSortie = 4,
            AssignedEngineerIds = { "E1", "E2" }, EngineersPerSortie = 1, PilotsPerSortie = 0,
            StartDate = Start, TargetEndDate = Start.AddDays(targetDays),
        });
        return s;
    }

    [Fact]
    public void Mc_effort_metrics_count_simulator_sessions()
    {
        var s = SimScenario(devices: 1, maxBlocksPerDay: 0, totalTp: 12, targetDays: 60);
        var result = MonteCarlo.Analyze(s, runs: 20, seed: 7);
        Assert.True(result.SortiesMean >= 3.0,
            $"sim sessions must count toward effort; got SortiesMean={result.SortiesMean}");
    }

    [Fact]
    public void Criticality_tracks_devices_and_the_sole_device_is_load_bearing()
    {
        var s = SimScenario(devices: 1, maxBlocksPerDay: 0, totalTp: 12, targetDays: 60);
        var result = CriticalityAnalysis.Analyze(s);
        var sim = result.Resources.FirstOrDefault(r => r.Kind == "Simulator");
        Assert.NotNull(sim);
        Assert.True(sim!.BreaksPlan);
    }

    [Fact]
    public void Criticality_with_a_spare_device_does_not_break_when_one_is_removed()
    {
        var s = SimScenario(devices: 2, maxBlocksPerDay: 0, totalTp: 12, targetDays: 60);
        var result = CriticalityAnalysis.Analyze(s);
        Assert.All(result.Resources.Where(r => r.Kind == "Simulator"),
            r => Assert.False(r.BreaksPlan));
    }

    [Fact]
    public void Recommender_proposes_adding_a_simulator_for_a_device_bound_program()
    {
        var s = SimScenario(devices: 1, maxBlocksPerDay: 1, totalTp: 120, targetDays: 21);
        var baseline = new Scheduler().Run(s);
        Assert.False(baseline.MeetsDeadline("DEV", s.Campaigns[0].TargetEndDate),
            "the device-bound campaign should miss its target before help");

        var result = ResourceSuggester.Recommend(
            s, ResourceSuggester.RecommenderLevers.AddSimulator, runs: 12,
            targetConfidence: 0.90, maxSteps: 2, seed: 7);
        Assert.Contains(result.Steps, step =>
            step.Addition.Contains("simulator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Optimizer_adds_a_simulator_when_the_lever_is_enabled()
    {
        var s = SimScenario(devices: 1, maxBlocksPerDay: 1, totalTp: 120, targetDays: 21);
        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.AddSimulators,
            MaxAddedSimulators = 2,
            Iterations = 80, SearchRuns = 10, FinalRuns = 10, Seed = 7,
        };
        var result = Optimizer.Optimize(s, opts);
        Assert.True(result.Optimized.Simulators.Count > s.Simulators.Count,
            "the optimizer should add at least one simulator to a device-bound program");
    }
}
