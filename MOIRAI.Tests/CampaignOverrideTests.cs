using System.Text.Json;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class CampaignOverrideTests
{
    private static readonly DateOnly Monday = new(2026, 1, 5);

    private static Scenario TwoTeams(FlyableDayModel? scenarioModel = null)
    {
        var sc = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = scenarioModel ?? new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Monday },
            HorizonStart = Monday,
            HorizonEnd = Monday.AddDays(60),
        };
        foreach (var n in new[] { "1", "2" })
        {
            sc.Pilots.Add(new Pilot { Id = "P" + n, Name = "P" + n });
            sc.Engineers.Add(new Engineer { Id = "E" + n, Name = "E" + n });
            sc.Aircraft.Add(new Aircraft { Id = "T" + n, Name = "T" + n });
            sc.Campaigns.Add(new Campaign
            {
                Id = n == "1" ? "A" : "B", Name = n == "1" ? "A" : "B",
                TotalTestPoints = 10, TestPointsPerSortie = 1,
                StartDate = Monday, TargetEndDate = Monday.AddDays(20),
                RehearsalRequired = false,
                CompatibleAircraftIds = { "T" + n }, AssignedPilotIds = { "P" + n }, AssignedEngineerIds = { "E" + n },
            });
        }
        return sc;
    }

    private static List<DateOnly> Days(Schedule s, string id) =>
        s.Sorties.Where(x => x.CampaignId == id).Select(x => x.Date).Distinct().OrderBy(d => d).ToList();

    [Fact]
    public void Own_flyable_model_thins_only_that_campaigns_days()
    {
        var sc = TwoTeams();
        var a = sc.Campaigns[0];
        a.UseGlobalFlyable = false;
        a.FlyableModel = new RateBasedFlyableDays { Rate = 0.5, PatternLength = 2, Anchor = Monday };
        foreach (var sch in new[] { new Scheduler().Run(sc), new RevisingScheduler().Run(sc) })
        {
            Assert.All(Days(sch, "A"), d => Assert.True(a.FlyableModel.IsFlyable(d)));
            Assert.Contains(Days(sch, "B"), d => !a.FlyableModel.IsFlyable(d));
            Assert.True(sch.Bottlenecks["A"].NotUsable > 0);
            Assert.Equal(0, sch.Bottlenecks["B"].NotUsable);
        }
        a.UseGlobalFlyable = true;
        Assert.Equal(0, new Scheduler().Run(sc).Bottlenecks["A"].NotUsable);
    }

    [Fact]
    public void Own_probabilistic_model_is_sampled_by_monte_carlo()
    {
        var sc = TwoTeams(ProbabilisticFlyableDays.Uniform(1.0, 0, Monday));
        var a = sc.Campaigns[0];
        a.UseGlobalFlyable = false;
        a.FlyableModel = ProbabilisticFlyableDays.Uniform(0.5, 0.5, Monday);
        a.TargetEndDate = Monday.AddDays(18);
        var r = MonteCarlo.Analyze(sc, 200, seed: 7);
        double pa = r.Campaigns.Single(c => c.CampaignId == "A").PMeet;
        double pb = r.Campaigns.Single(c => c.CampaignId == "B").PMeet;
        Assert.Equal(1.0, pb);
        Assert.InRange(pa, 0.01, 0.99);

        var (_, replay) = MonteCarlo.ReplayRun(sc, 0, seed: 7);
        Assert.True(replay.CampaignCompleted["B"]);
    }

    [Fact]
    public void Own_limits_apply_to_that_campaign_only()
    {
        var sc = TwoTeams();
        sc.Config.MaxSortiesPerPersonPerDay = 3;
        var a = sc.Campaigns[0];
        a.UseGlobalLimits = false;
        a.Limits = CrewLimits.From(sc.Config);
        a.Limits.MaxSortiesPerPersonPerDay = 1;
        foreach (var sch in new[] { new Scheduler().Run(sc), new RevisingScheduler().Run(sc) })
        {
            var perDayA = sch.Sorties.Where(s => s.CampaignId == "A").GroupBy(s => s.Date).Max(g => g.Count());
            var perDayB = sch.Sorties.Where(s => s.CampaignId == "B").GroupBy(s => s.Date).Max(g => g.Count());
            Assert.Equal(1, perDayA);
            Assert.Equal(3, perDayB);
        }
        a.UseGlobalLimits = true;
        Assert.Equal(3, new Scheduler().Run(sc).Sorties.Where(s => s.CampaignId == "A")
            .GroupBy(s => s.Date).Max(g => g.Count()));
    }

    [Fact]
    public void Overrides_round_trip_through_json_and_default_to_global()
    {
        var c = new Campaign
        {
            Id = "A",
            UseGlobalFlyable = false,
            FlyableModel = ProbabilisticFlyableDays.Uniform(0.4, 0.3, Monday),
            UseGlobalLimits = false,
            Limits = new CrewLimits { MaxConsecutiveFlyingDays = 9 },
        };
        var back = JsonSerializer.Deserialize<Campaign>(JsonSerializer.Serialize(c))!;
        Assert.False(back.UseGlobalFlyable);
        Assert.IsType<ProbabilisticFlyableDays>(back.FlyableModel);
        Assert.Equal(9, back.Limits!.MaxConsecutiveFlyingDays);
        Assert.Equal(9, back.EffectiveConfig(new ScenarioConfig()).MaxConsecutiveFlyingDays);

        var old = JsonSerializer.Deserialize<Campaign>("{\"Id\":\"X\"}")!;
        Assert.True(old.UseGlobalFlyable);
        Assert.True(old.UseGlobalLimits);
        var cfg = new ScenarioConfig();
        Assert.Same(cfg, old.EffectiveConfig(cfg));
    }
}
