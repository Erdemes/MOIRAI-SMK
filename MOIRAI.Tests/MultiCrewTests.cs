using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class MultiCrewTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Scn(int pilots, int minPilots)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(29),
        };
        for (int i = 1; i <= pilots; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "Multi", MinPilots = minPilots });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, int sorties = 3)
    {
        var c = new Campaign
        {
            Id = "c1", Name = "c1", TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        };
        c.CompatibleAircraftIds.Add("ac1");
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.Add("e1");
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Multi_crew_sortie_carries_two_pilots()
    {
        var s = Scn(pilots: 2, minPilots: 2);
        Camp(s, sorties: 2);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so =>
        {
            Assert.Single(so.AdditionalPilotIds);
            Assert.NotEqual(so.PilotId, so.AdditionalPilotIds[0]);
        });
    }

    [Fact]
    public void One_pilot_cannot_fly_a_two_crew_aircraft()
    {
        var s = Scn(pilots: 1, minPilots: 2);
        Camp(s, sorties: 2);

        var sch = new Scheduler().Run(s);

        Assert.False(sch.CampaignCompleted["c1"]);
        Assert.Empty(sch.Sorties);
        Assert.True(sch.Bottlenecks["c1"].Pilot > 0);
    }

    [Fact]
    public void Two_pilots_fly_the_two_crew_aircraft_to_completion()
    {
        var s = Scn(pilots: 2, minPilots: 2);
        Camp(s, sorties: 4);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.Equal(4, sch.Sorties.Count);
    }

    [Fact]
    public void Min_pilots_zero_is_unchanged()
    {
        var withZero = new Scheduler().Run(BuildSingle(0));
        var withOne = new Scheduler().Run(BuildSingle(1));

        Assert.Equal(withOne.Sorties.Count, withZero.Sorties.Count);
        Assert.Equal(
            withOne.Sorties.Select(x => (x.Date, x.AircraftId, x.PilotId)),
            withZero.Sorties.Select(x => (x.Date, x.AircraftId, x.PilotId)));

        static Scenario BuildSingle(int minPilots)
        {
            var s = Scn(pilots: 1, minPilots: minPilots);
            Camp(s, sorties: 3);
            return s;
        }
    }

    [Fact]
    public void Validator_flags_too_few_pilots_for_a_multi_crew_campaign()
    {
        var s = Scn(pilots: 1, minPilots: 2);
        Camp(s);

        var issues = ScenarioValidator.Validate(s);
        Assert.Contains(issues, i => i.Category == "Crew" && i.Severity == IssueSeverity.Error
                                     && i.Message.Contains("2 pilot"));
    }

    [Fact]
    public void Campaign_test_crew_occupies_extra_pilots_on_a_single_seat_jet()
    {
        var s = Scn(pilots: 2, minPilots: 1);
        s.Engineers.Add(new Engineer { Id = "e2", Name = "E2" });
        var c = Camp(s, sorties: 2);
        c.AssignedEngineerIds.Add("e2");
        c.PilotsPerSortie = 2;
        c.EngineersPerSortie = 2;

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so =>
        {
            Assert.Single(so.AdditionalPilotIds);
            Assert.Equal(2, so.EngineerIds.Count);
        });
    }

    [Fact]
    public void Per_campaign_crew_zero_inherits_the_scenario_default()
    {
        var s = Scn(pilots: 1, minPilots: 1);
        var c = Camp(s, sorties: 3);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so => Assert.Empty(so.AdditionalPilotIds));
    }

    [Fact]
    public void Validator_flags_too_few_pilots_for_a_campaign_test_need()
    {
        var s = Scn(pilots: 1, minPilots: 1);
        var c = Camp(s);
        c.PilotsPerSortie = 2;

        var issues = ScenarioValidator.Validate(s);
        Assert.Contains(issues, i => i.Category == "Crew" && i.Severity == IssueSeverity.Error
                                     && i.Message.Contains("2 pilot"));
    }
}
