using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class CriticalityTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Build()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(60),
        };
        s.Pilots.Add(new Pilot { Id = "P1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "P2", Name = "P2" });
        s.Engineers.Add(new Engineer { Id = "E1", Name = "E1" });
        s.Engineers.Add(new Engineer { Id = "E2", Name = "E2" });
        s.Aircraft.Add(new Aircraft { Id = "TA", Name = "TA" });
        s.Aircraft.Add(new Aircraft { Id = "TB", Name = "TB" });
        s.Aircraft.Add(new Aircraft { Id = "TC", Name = "TC" });

        Campaign Camp(string id, int sorties, string tail, string pilot, string eng, params string[] prereqs)
        {
            var c = new Campaign
            {
                Id = id, Name = id,
                TotalTestPoints = sorties, TestPointsPerSortie = 1,
                StartDate = Start, TargetEndDate = Start.AddDays(55),
                BlocksPerSortie = 3,
                RehearsalRequired = false,
            };
            c.PrerequisiteCampaignIds.AddRange(prereqs);
            c.CompatibleAircraftIds.Add(tail);
            c.AssignedPilotIds.Add(pilot);
            c.AssignedEngineerIds.Add(eng);
            s.Campaigns.Add(c);
            return c;
        }

        Camp("A", 3, "TA", "P1", "E1");
        Camp("B", 3, "TB", "P1", "E1", "A");
        Camp("C", 2, "TC", "P2", "E2");
        return s;
    }

    [Fact]
    public void Prerequisite_chain_is_critical_independent_campaign_has_float()
    {
        var s = Build();
        var r = CriticalityAnalysis.Analyze(s, maxFloatDays: 60);

        Assert.True(r.BaselineComplete);
        Assert.NotNull(r.BaselineFinish);
        Assert.Equal(3, r.Campaigns.Count);

        var a = r.Campaigns.First(c => c.CampaignId == "A");
        var b = r.Campaigns.First(c => c.CampaignId == "B");
        var c = r.Campaigns.First(x => x.CampaignId == "C");

        Assert.True(a.IsCritical, "A (prerequisite of the long pole) should be critical");
        Assert.True(b.IsCritical, "B (the long pole) should be critical");
        Assert.Equal(0, a.FloatDays);

        Assert.False(c.IsCritical);
        Assert.True(c.FloatDays is > 0, $"C should have float, got {c.FloatDays}");
    }

    [Fact]
    public void Removing_a_campaigns_sole_resource_breaks_the_plan()
    {
        var s = Build();
        var r = CriticalityAnalysis.Analyze(s, maxFloatDays: 30);

        Assert.True(r.Resources.First(x => x.ResourceId == "P2").BreaksPlan);
        Assert.True(r.Resources.First(x => x.ResourceId == "TC").BreaksPlan);
        Assert.True(r.Resources.First(x => x.ResourceId == "P1").IsCritical);
    }

    [Fact]
    public void Runs_on_the_advanced_engine_when_supplied()
    {
        var s = Build();
        var opts = new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableScarcityCrew = true,
            EnableBottleneckSteer = true, EnableGlobalSearch = true, SearchParallelism = 1,
        };
        var r = CriticalityAnalysis.Analyze(s, sc => new RevisingScheduler(opts).Run(sc), maxFloatDays: 60);

        Assert.True(r.BaselineComplete);
        Assert.NotNull(r.BaselineFinish);
        Assert.True(r.Campaigns.First(c => c.CampaignId == "A").IsCritical);
        Assert.False(r.Campaigns.First(c => c.CampaignId == "C").IsCritical);
    }

    [Fact]
    public void Analyze_does_not_mutate_the_input_scenario()
    {
        var s = Build();
        var startsBefore = s.Campaigns.ToDictionary(c => c.Id, c => c.StartDate);
        int pilotsBefore = s.Pilots.Count, acBefore = s.Aircraft.Count;

        CriticalityAnalysis.Analyze(s, maxFloatDays: 20);

        foreach (var c in s.Campaigns) Assert.Equal(startsBefore[c.Id], c.StartDate);
        Assert.Equal(pilotsBefore, s.Pilots.Count);
        Assert.Equal(acBefore, s.Aircraft.Count);
    }
}
