using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class RehearsalReplanTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Scenario()
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
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "Tail-1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });

        var c = new Campaign
        {
            Id = "c1",
            Name = "FT-REH",
            TotalTestPoints = 1,
            TestPointsPerSortie = 1,
            StartDate = Start,
            TargetEndDate = Start.AddDays(20),
            RehearsalRequired = true,
        };
        c.CompatibleAircraftIds.Add("ac1");
        c.AssignedPilotIds.Add("p1");
        c.AssignedEngineerIds.Add("e1");
        s.Campaigns.Add(c);
        return s;
    }

    [Fact]
    public void Held_rehearsal_for_a_slipped_sortie_satisfies_the_replan_without_a_new_one()
    {
        var s = Scenario();
        s.FreezeDate = Start.AddDays(2);
        s.ActualRehearsals.Add(new Rehearsal
        {
            CampaignId = "c1",
            Date = Start.AddDays(1),
            Block = 1,
            SortieNo = 1,
            SortieDate = Start.AddDays(3),
            PilotId = "p1",
            EngineerIds = new List<string> { "e1" },
            IsActual = true,
        });

        var sch = new Scheduler().Run(s);

        var sortie = Assert.Single(sch.Sorties);
        Assert.False(sortie.IsActual);
        Assert.True(sortie.Date >= Start.AddDays(2));
        Assert.True(sch.CampaignCompleted["c1"]);

        var reh = Assert.Single(sch.Rehearsals);
        Assert.True(reh.IsActual);
        Assert.Equal(Start.AddDays(1), reh.Date);
        Assert.DoesNotContain(sch.Rehearsals, r => !r.IsActual);
    }

    [Fact]
    public void Without_a_banked_rehearsal_the_replanned_sortie_books_its_own()
    {
        var s = Scenario();
        s.FreezeDate = Start.AddDays(2);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        var reh = Assert.Single(sch.Rehearsals);
        Assert.False(reh.IsActual);
    }
}
