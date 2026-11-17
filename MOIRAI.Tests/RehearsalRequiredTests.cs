using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class RehearsalRequiredTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Build(bool rehearsalRequired)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(29),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "AC" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        var c = new Campaign
        {
            Id = "c1", Name = "c1", TotalTestPoints = 4, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
            RehearsalRequired = rehearsalRequired,
        };
        c.CompatibleAircraftIds.Add("ac1");
        c.AssignedPilotIds.Add("p1");
        c.AssignedEngineerIds.Add("e1");
        s.Campaigns.Add(c);
        return s;
    }

    [Fact]
    public void Default_requires_a_rehearsal_per_sortie()
    {
        var sch = new Scheduler().Run(Build(rehearsalRequired: true));

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.Equal(4, sch.Sorties.Count);
        Assert.Equal(4, sch.Rehearsals.Count);
        Assert.All(sch.Sorties, so => Assert.NotNull(so.RehearsalDate));
    }

    [Fact]
    public void Opt_out_flies_sorties_only_with_no_rehearsals()
    {
        var sch = new Scheduler().Run(Build(rehearsalRequired: false));

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.Equal(4, sch.Sorties.Count);
        Assert.Empty(sch.Rehearsals);
        Assert.All(sch.Sorties, so => Assert.Null(so.RehearsalDate));
    }

    [Fact]
    public void Opt_out_never_reports_a_rehearsal_bottleneck()
    {
        var sch = new Scheduler().Run(Build(rehearsalRequired: false));
        Assert.Equal(0, sch.Bottlenecks["c1"].Rehearsal);
    }
}
