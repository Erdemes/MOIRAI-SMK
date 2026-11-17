using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class FormationTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario Make(int sorties, int supportPerSortie, int targetDay = 30)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(39),
        };
        for (int i = 1; i <= 3; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= 2; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Test article" });
        s.Aircraft.Add(new Aircraft { Id = "CH-1", Name = "Chase 1" });
        s.Aircraft.Add(new Aircraft { Id = "CH-2", Name = "Chase 2" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        s.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "FT-FORM", TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(targetDay),
            CompatibleAircraftIds = { "TC-001" },
            SupportAircraftIds = { "CH-1", "CH-2" },
            SupportAircraftPerSortie = supportPerSortie,
            AssignedPilotIds = { "p1", "p2", "p3" },
            AssignedEngineerIds = { "e1", "e2" },
        });
        return s;
    }

    [Fact]
    public void Formation_sortie_gets_support_ships_and_pilots_in_the_same_block()
    {
        var s = Make(sorties: 4, supportPerSortie: 2);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so =>
        {
            Assert.Equal(2, so.SupportAircraftIds.Count);
            Assert.Equal(2, so.SupportPilotIds.Count);
            Assert.DoesNotContain(so.AircraftId, so.SupportAircraftIds);
            Assert.DoesNotContain(so.PilotId, so.SupportPilotIds);
            Assert.Equal(so.SupportPilotIds.Count, so.SupportPilotIds.Distinct().Count());
        });
    }

    [Fact]
    public void Missing_support_ship_blocks_the_sortie_with_an_aircraft_bottleneck()
    {
        var s = Make(sorties: 3, supportPerSortie: 2);
        s.Aircraft.RemoveAll(a => a.Id == "CH-2");
        s.Campaigns[0].SupportAircraftIds.Remove("CH-2");

        var sch = new Scheduler().Run(s);

        Assert.False(sch.CampaignCompleted["c1"]);
        Assert.Empty(sch.Sorties);
        Assert.Equal("aircraft", sch.Bottlenecks["c1"].Dominant()!.Value.Reason);
    }

    [Fact]
    public void Support_pilots_consume_their_daily_sortie_allowance()
    {
        var s = Make(sorties: 6, supportPerSortie: 1);
        s.Config.MaxSortiesPerPersonPerDay = 1;

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        var perDay = sch.Sorties.GroupBy(x => x.Date).Select(g => g.Count());
        Assert.All(perDay, n => Assert.True(n <= 1, "two-pilot formations under a 1/day cap fit once per day"));
    }

    [Fact]
    public void Support_fields_round_trip_through_a_json_clone()
    {
        var s = Make(sorties: 2, supportPerSortie: 1);

        var clone = ScenarioJson.Clone(s);

        var c = clone.Campaigns.Single();
        Assert.Equal(1, c.SupportAircraftPerSortie);
        Assert.Equal(new[] { "CH-1", "CH-2" }, c.SupportAircraftIds);
    }
}
