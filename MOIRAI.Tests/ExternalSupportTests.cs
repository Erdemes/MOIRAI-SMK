using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

public class ExternalSupportTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario Make(int horizonDays = 30)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= 3; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "Tail-2" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, string id, int sorties, params string[] extIds)
    {
        var c = new Campaign
        {
            Id = id, Name = id, TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        c.RequiredExternalSupportIds.AddRange(extIds);
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Sorties_fly_only_on_the_assets_allocated_days()
    {
        var s = Make();
        var allocated = new ExplicitFlyableDays();
        for (int d = 5; d <= 8; d++) allocated.Days.Add(Start.AddDays(d));
        s.ExternalSupports.Add(new ExternalSupport
        {
            Id = "chase", Name = "External chase", AvailabilityModel = allocated,
        });
        Camp(s, "c1", sorties: 3, "chase");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so =>
        {
            Assert.InRange(so.Date.DayNumber - Start.DayNumber, 5, 8);
            Assert.Equal(new[] { "chase" }, so.ExternalSupportIds);
        });
        Assert.True(sch.Bottlenecks["c1"].External > 0);
        Assert.Contains(DayLogCodes.External, sch.DayLog["c1"]);
    }

    [Fact]
    public void One_asset_serves_one_sortie_per_block()
    {
        var s = Make();
        s.ExternalSupports.Add(new ExternalSupport { Id = "chase", Name = "Chase" });
        var a = Camp(s, "A", sorties: 4, "chase");
        a.CompatibleAircraftIds.Clear(); a.CompatibleAircraftIds.Add("TC-001");
        var b = Camp(s, "B", sorties: 4, "chase");
        b.CompatibleAircraftIds.Clear(); b.CompatibleAircraftIds.Add("TC-002");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["A"]);
        Assert.True(sch.CampaignCompleted["B"]);
        var slots = sch.Sorties.Select(x => (x.Date, x.Block)).ToList();
        Assert.Equal(slots.Count, slots.Distinct().Count());
    }

    [Fact]
    public void And_semantics_requires_every_listed_asset()
    {
        var s = Make();
        s.ExternalSupports.Add(new ExternalSupport { Id = "chase", Name = "Chase" });
        var tankerDays = new ExplicitFlyableDays();
        for (int d = 10; d <= 29; d++) tankerDays.Days.Add(Start.AddDays(d));
        s.ExternalSupports.Add(new ExternalSupport
        {
            Id = "tanker", Name = "Tanker", AvailabilityModel = tankerDays,
        });
        Camp(s, "c1", sorties: 2, "chase", "tanker");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so => Assert.True(so.Date >= Start.AddDays(10)));
        Assert.All(sch.Sorties, so =>
            Assert.Equal(new[] { "chase", "tanker" }, so.ExternalSupportIds.OrderBy(x => x)));
    }

    [Fact]
    public void External_support_leaves_no_footprint_on_our_books()
    {
        var baseline = Make();
        Camp(baseline, "c1", sorties: 5).CompatibleAircraftIds.RemoveAll(id => id != "TC-001");
        var withExt = Make();
        withExt.ExternalSupports.Add(new ExternalSupport { Id = "chase", Name = "Chase" });
        Camp(withExt, "c1", sorties: 5, "chase").CompatibleAircraftIds.RemoveAll(id => id != "TC-001");

        var a = new Scheduler().Run(baseline);
        var b = new Scheduler().Run(withExt);

        Assert.Equal(a.Sorties.Count, b.Sorties.Count);
        for (int i = 0; i < a.Sorties.Count; i++)
        {
            Assert.Equal(a.Sorties[i].Date, b.Sorties[i].Date);
            Assert.Equal(a.Sorties[i].Block, b.Sorties[i].Block);
            Assert.Equal(a.Sorties[i].AircraftId, b.Sorties[i].AircraftId);
            Assert.Equal(a.Sorties[i].PilotId, b.Sorties[i].PilotId);
            Assert.Equal(a.Sorties[i].EngineerIds, b.Sorties[i].EngineerIds);
        }
    }

    [Fact]
    public void Daily_cap_limits_supported_sorties_per_day()
    {
        var s = Make();
        s.ExternalSupports.Add(new ExternalSupport { Id = "tk", Name = "Tanker", MaxSortiesPerDay = 1 });
        Camp(s, "c1", sorties: 4, "tk");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        foreach (var g in sch.Sorties.GroupBy(x => x.Date))
            Assert.Single(g);
    }

    [Fact]
    public void Unknown_asset_id_blocks_the_campaign_visibly()
    {
        var s = Make();
        Camp(s, "c1", sorties: 2, "deleted-asset");

        var sch = new Scheduler().Run(s);

        Assert.Empty(sch.Sorties);
        Assert.False(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Bottlenecks["c1"].External > 0);
        Assert.Equal("external support", sch.Bottlenecks["c1"].Dominant()!.Value.Reason);
    }

    [Fact]
    public void External_availability_is_its_own_decomposition_source()
    {
        var s = Make(horizonDays: 60);
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0, Start);
        s.ExternalSupports.Add(new ExternalSupport
        {
            Id = "tk", Name = "Tanker",
            AvailabilityModel = ProbabilisticFlyableDays.Uniform(0.5, 0.4, Start),
        });
        Camp(s, "c1", sorties: 8, "tk");

        var weatherOnly = MOIRAI.Analysis.MonteCarlo.Analyze(s, runs: 40, seed: 5,
            sources: MOIRAI.Analysis.MonteCarlo.UncertaintySources.Weather);
        var extOnly = MOIRAI.Analysis.MonteCarlo.Analyze(s, runs: 40, seed: 5,
            sources: MOIRAI.Analysis.MonteCarlo.UncertaintySources.ExternalSupport);

        Assert.Equal(weatherOnly.FinishP10, weatherOnly.FinishP90);
        Assert.True(extOnly.FinishP90 > extOnly.FinishP10);

        var again = MOIRAI.Analysis.MonteCarlo.Analyze(s, runs: 40, seed: 5,
            sources: MOIRAI.Analysis.MonteCarlo.UncertaintySources.ExternalSupport);
        Assert.Equal(extOnly.PDelivery, again.PDelivery);
        Assert.Equal(extOnly.FinishP50, again.FinishP50);
    }

    [Fact]
    public void Validator_flags_unknown_and_never_allocated_assets()
    {
        var s = Make(horizonDays: 30);
        Camp(s, "c1", sorties: 2, "deleted-asset");
        var never = new ExplicitFlyableDays();
        s.ExternalSupports.Add(new ExternalSupport { Id = "tk", Name = "Tanker", AvailabilityModel = never });
        Camp(s, "c2", sorties: 2, "tk");

        var issues = MOIRAI.Reporting.ScenarioValidator.Validate(s);

        Assert.Contains(issues, i => i.Category == "External support"
            && i.Severity == MOIRAI.Reporting.IssueSeverity.Error && i.CampaignName == "c1");
        Assert.Contains(issues, i => i.Category == "External support"
            && i.Severity == MOIRAI.Reporting.IssueSeverity.Warning && i.CampaignName == "c2");
    }

    [Fact]
    public void Advanced_engine_honours_the_same_availability()
    {
        var s = Make();
        var allocated = new ExplicitFlyableDays();
        for (int d = 5; d <= 12; d++) allocated.Days.Add(Start.AddDays(d));
        s.ExternalSupports.Add(new ExternalSupport
        {
            Id = "chase", Name = "Chase", AvailabilityModel = allocated,
        });
        Camp(s, "c1", sorties: 3, "chase");

        var sch = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true,
        }).Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.All(sch.Sorties, so => Assert.InRange(so.Date.DayNumber - Start.DayNumber, 5, 12));
    }
}
