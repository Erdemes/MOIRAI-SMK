using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class TypeRatingTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Two(int pilots = 2, int engineers = 2)
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
        for (int i = 1; i <= engineers; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "acA", Name = "Type-A" });
        s.Aircraft.Add(new Aircraft { Id = "acB", Name = "Type-B" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, int sorties, params string[] tails)
    {
        var c = new Campaign
        {
            Id = "c1", Name = "c1", TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        };
        c.CompatibleAircraftIds.AddRange(tails.Length > 0 ? tails : new[] { "acA", "acB" });
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Pilot_rated_on_one_type_keeps_sorties_on_that_type()
    {
        var s = Two(pilots: 1, engineers: 1);
        Camp(s, sorties: 5, "acA", "acB");
        s.Pilots[0].TypeRatings.Add("acA");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so => Assert.Equal("acA", so.AircraftId));
    }

    [Fact]
    public void Campaign_whose_pilot_is_not_rated_for_its_only_tail_cannot_fly()
    {
        var s = Two(pilots: 1, engineers: 1);
        Camp(s, sorties: 3, "acA");
        s.Pilots[0].TypeRatings.Add("acB");

        var sch = new Scheduler().Run(s);

        Assert.False(sch.CampaignCompleted["c1"]);
        Assert.Empty(sch.Sorties);
    }

    [Fact]
    public void Engineer_rating_also_constrains_the_tail()
    {
        var s = Two(pilots: 1, engineers: 1);
        Camp(s, sorties: 4, "acA", "acB");
        s.Engineers[0].TypeRatings.Add("acB");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so => Assert.Equal("acB", so.AircraftId));
    }

    [Fact]
    public void Disjoint_ratings_let_each_type_be_flown_by_its_rated_pilot()
    {
        var s = Two(pilots: 2, engineers: 2);
        Camp(s, sorties: 6, "acA", "acB");
        s.Pilots[0].TypeRatings.Add("acA");
        s.Pilots[1].TypeRatings.Add("acB");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.Contains(sch.Sorties, so => so.AircraftId == "acA");
        Assert.Contains(sch.Sorties, so => so.AircraftId == "acB");
        foreach (var so in sch.Sorties)
        {
            var pilot = s.Pilots.First(p => p.Id == so.PilotId);
            Assert.Contains(so.AircraftId, pilot.TypeRatings);
        }
    }

    [Fact]
    public void Empty_ratings_are_unrestricted()
    {
        var s = Two(pilots: 1, engineers: 1);
        Camp(s, sorties: 6, "acA", "acB");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.Contains(sch.Sorties, so => so.AircraftId == "acA");
        Assert.Contains(sch.Sorties, so => so.AircraftId == "acB");
    }
}
