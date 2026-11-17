using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class LocationTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Location Remote(
        int ferryDays = 1, int setupDays = 2, int? blocksCap = null,
        double ferryHours = 2) => new()
    {
        Id = "rng", Name = "Test Range",
        FerryDays = ferryDays, SetupDays = setupDays, TeardownDays = 1,
        FerryFlightHours = ferryHours, FerryDutyHours = 6,
        BlocksPerDayCap = blocksCap,
    };

    private static Scenario Make(Location? remote, int sorties, int targetDay,
        bool remoteCampaign = true, int horizonDays = 60, bool remoteSim = true)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "Tail-2" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        if (remote is not null)
        {
            s.Locations.Add(remote);
            if (remoteSim)
                s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = remote.Id });
        }
        s.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "FT-RANGE", TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(targetDay),
            LocationId = remoteCampaign && remote is not null ? remote.Id : string.Empty,
            CompatibleAircraftIds = { "TC-001" },
            AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1", "e2" },
        });
        return s;
    }

    [Fact]
    public void Range_availability_is_its_own_decomposition_source()
    {
        var remote = Remote(ferryDays: 1, setupDays: 1);
        remote.RangeModel = ProbabilisticFlyableDays.Uniform(0.6, 0.4, Start);
        var s = Make(remote, sorties: 8, targetDay: 40, horizonDays: 80);
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0, Start);

        var weatherOnly = MonteCarlo.Analyze(s, runs: 40, seed: 5,
            sources: MonteCarlo.UncertaintySources.Weather);
        var rangeOnly = MonteCarlo.Analyze(s, runs: 40, seed: 5,
            sources: MonteCarlo.UncertaintySources.Range);

        Assert.Equal(weatherOnly.FinishP10, weatherOnly.FinishP90);

        Assert.True(rangeOnly.FinishP90 > rangeOnly.FinishP10);
        Assert.True(rangeOnly.FinishP90 >= weatherOnly.FinishP90);
    }

    [Fact]
    public void Remote_campaign_flies_only_after_ferry_and_setup()
    {
        var s = Make(Remote(ferryDays: 1, setupDays: 2), sorties: 4, targetDay: 30);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Sorties.Min(x => x.Date) >= Start.AddDays(3));

        var dep = Assert.Single(sch.Deployments);
        Assert.Equal(Start, dep.Departure);
        Assert.Equal(Start.AddDays(3), dep.FlyFrom);
        Assert.NotNull(dep.ReturnEnd);
        Assert.Equal(2, sch.RelocationFlights.Count);
        Assert.Single(sch.RelocationFlights, f => f.Outbound);
    }

    [Fact]
    public void Deployed_crew_cannot_fly_main_base_campaigns_while_away()
    {
        var s = Make(Remote(), sorties: 6, targetDay: 40);
        s.Campaigns.Add(new Campaign
        {
            Id = "c2", Name = "FT-HOME", TotalTestPoints = 20, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(50),
            CompatibleAircraftIds = { "TC-002" },
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e3" },
        });

        var sch = new Scheduler().Run(s);
        var dep = Assert.Single(sch.Deployments);
        var away = (Min: dep.Departure, Max: dep.ReturnEnd ?? s.HorizonEnd);

        var homeSortiesWhileAway = sch.Sorties
            .Where(x => x.CampaignId == "c2" && x.Date >= away.Min && x.Date <= away.Max)
            .ToList();
        Assert.NotEmpty(homeSortiesWhileAway);
        Assert.All(homeSortiesWhileAway, x => Assert.Equal("p2", x.PilotId));
    }

    [Fact]
    public void Post_deployment_rest_keeps_the_crew_off_after_the_return()
    {
        var remote = Remote();
        remote.PostDeploymentRestDays = 5;
        var s = Make(remote, sorties: 4, targetDay: 40, horizonDays: 90);
        s.Campaigns.Add(new Campaign
        {
            Id = "c2", Name = "FT-HOME", TotalTestPoints = 40, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(80),
            CompatibleAircraftIds = { "TC-002" },
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e3" },
        });

        var sch = new Scheduler().Run(s);
        var dep = Assert.Single(sch.Deployments);
        Assert.NotNull(dep.ReturnEnd);
        Assert.Equal(dep.ReturnEnd!.Value.AddDays(5), dep.RestEnd);

        var restWindow = sch.Sorties
            .Where(x => x.Date > dep.ReturnEnd.Value && x.Date <= dep.RestEnd!.Value)
            .ToList();
        Assert.NotEmpty(restWindow);
        Assert.All(restWindow, x =>
        {
            Assert.NotEqual("p1", x.PilotId);
            Assert.DoesNotContain("e1", x.EngineerIds);
            Assert.DoesNotContain("e2", x.EngineerIds);
        });
        Assert.DoesNotContain(sch.Rehearsals, r =>
            r.Date > dep.ReturnEnd.Value && r.Date <= dep.RestEnd!.Value
            && (r.PilotId == "p1" || r.EngineerIds.Contains("e1") || r.EngineerIds.Contains("e2")));

        Assert.Contains(sch.Sorties, x => x.PilotId == "p1" && x.Date > dep.RestEnd!.Value);
    }

    [Fact]
    public void Ferry_hours_feed_the_periodic_maintenance_counters()
    {
        var s = Make(Remote(ferryHours: 3, ferryDays: 1, setupDays: 0), sorties: 3, targetDay: 30);
        s.Campaigns[0].ExpectedSortieFlightHours = 1;
        s.Aircraft[0].PeriodicMaintenances.Add(new PeriodicMaintenanceRule
        {
            IntervalFlightHours = 5, DurationDays = 2,
        });

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.AutoMaintenance.ContainsKey("TC-001"),
            "ferry hours must count toward the periodic-maintenance interval");
    }

    [Fact]
    public void Remote_block_cap_limits_daily_sorties()
    {
        var s = Make(Remote(blocksCap: 1, ferryDays: 1, setupDays: 0), sorties: 6, targetDay: 40);
        s.Campaigns[0].AssignedPilotIds.Add("p2");
        s.Campaigns[0].AssignedEngineerIds.Add("e3");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        var perDay = sch.Sorties.GroupBy(x => x.Date).Select(g => g.Count());
        Assert.All(perDay, n => Assert.True(n <= 1));
        Assert.All(sch.Sorties, x => Assert.Equal(1, x.Block));
    }

    [Fact]
    public void Without_a_simulator_every_rehearsal_lands_before_departure()
    {
        var s = Make(Remote(ferryDays: 1, setupDays: 1), sorties: 4, targetDay: 30, remoteSim: false);

        var sch = new Scheduler().Run(s);
        var dep = Assert.Single(sch.Deployments);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.NotEmpty(sch.Rehearsals);
        Assert.All(sch.Rehearsals, r => Assert.True(r.Date < dep.Departure,
            $"rehearsal {r.Date} must precede the departure {dep.Departure} (no simulator at the range)"));

        var ordered = sch.Rehearsals.OrderBy(r => r.SortieNo).ToList();
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].Date >= ordered[i - 1].Date,
                $"rehearsal for sortie #{ordered[i].SortieNo} ({ordered[i].Date}) must not precede " +
                $"the one for sortie #{ordered[i - 1].SortieNo} ({ordered[i - 1].Date})");
    }

    [Fact]
    public void Without_a_simulator_rehearsals_pack_close_to_departure()
    {
        var s = Make(Remote(ferryDays: 1, setupDays: 1), sorties: 4, targetDay: 50, remoteSim: false);
        s.Campaigns[0].StartDate = Start.AddDays(25);

        var sch = new Scheduler().Run(s);
        var dep = Assert.Single(sch.Deployments);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.NotEmpty(sch.Rehearsals);
        Assert.All(sch.Rehearsals, r => Assert.True(r.Date < dep.Departure));

        var earliest = sch.Rehearsals.Min(r => r.Date);
        int daysEarly = dep.Departure.DayNumber - earliest.DayNumber;
        Assert.True(daysEarly <= 5,
            $"earliest rehearsal {earliest} is {daysEarly} days before departure {dep.Departure} — " +
            "pre-deployment rehearsals should pack close to the deployment");
    }

    [Fact]
    public void Without_a_simulator_shared_crew_rehearsals_still_pack_close_to_departure()
    {
        var remote = Remote(ferryDays: 1, setupDays: 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start, HorizonEnd = Start.AddDays(79),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        for (int i = 1; i <= 4; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "Tail-2" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        s.Locations.Add(remote);
        foreach (var (id, tail, e1, e2) in new[] { ("ca", "TC-001", "e1", "e2"), ("cb", "TC-002", "e3", "e4") })
            s.Campaigns.Add(new Campaign
            {
                Id = id, Name = id, TotalTestPoints = 4, TestPointsPerSortie = 1,
                StartDate = Start.AddDays(25), TargetEndDate = Start.AddDays(70),
                LocationId = remote.Id, CompatibleAircraftIds = { tail },
                AssignedPilotIds = { "p1" }, AssignedEngineerIds = { e1, e2 },
            });

        var sch = new Scheduler().Run(s);
        var dep = Assert.Single(sch.Deployments);

        Assert.True(sch.CampaignCompleted["ca"] && sch.CampaignCompleted["cb"]);
        Assert.NotEmpty(sch.Rehearsals);
        Assert.All(sch.Rehearsals, r => Assert.True(r.Date < dep.Departure));

        var earliest = sch.Rehearsals.Min(r => r.Date);
        int daysEarly = dep.Departure.DayNumber - earliest.DayNumber;
        Assert.True(daysEarly <= 10,
            $"earliest rehearsal is {daysEarly} days before departure {dep.Departure} — " +
            "shared-crew pre-deployment rehearsals must stay packed close to departure");
    }

    [Fact]
    public void Remote_weather_gates_remote_sorties_independently_of_main_weather()
    {
        var rangeDays = new HashSet<DateOnly>();
        for (var d = Start.AddDays(10); d <= Start.AddDays(59); d = d.AddDays(1)) rangeDays.Add(d);
        var remote = Remote(ferryDays: 1, setupDays: 1);
        remote.FlyableModel = new ExplicitFlyableDays { Days = rangeDays };

        var s = Make(remote, sorties: 4, targetDay: 40);
        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Sorties.Min(x => x.Date) >= Start.AddDays(10));
    }

    [Fact]
    public void Range_availability_gates_remote_sorties_and_tallies_its_own_bottleneck()
    {
        var bookedDays = new HashSet<DateOnly>();
        for (var d = Start.AddDays(12); d <= Start.AddDays(59); d = d.AddDays(1)) bookedDays.Add(d);
        var remote = Remote(ferryDays: 1, setupDays: 1);
        remote.RangeModel = new ExplicitFlyableDays { Days = bookedDays };

        var s = Make(remote, sorties: 4, targetDay: 40);
        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Sorties.Min(x => x.Date) >= Start.AddDays(12));
        var tally = sch.Bottlenecks["c1"];
        Assert.True(tally.Range > 0, "range-blocked days must tally the Range bucket");
        Assert.Equal("range availability", tally.Dominant()!.Value.Reason);
    }

    [Fact]
    public void Probabilistic_range_risk_lowers_confidence_and_stays_reproducible()
    {
        Scenario Build(double rangeRate)
        {
            var remote = Remote(ferryDays: 1, setupDays: 1);
            remote.RangeModel = ProbabilisticFlyableDays.Uniform(rangeRate, 0.5, Start);
            var s = Make(remote, sorties: 8, targetDay: 25);
            s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.95, 0.2, Start);
            s.MasterDeadline = Start.AddDays(25);
            return s;
        }

        var risky = MonteCarlo.Analyze(Build(rangeRate: 0.4), runs: 60, seed: 5);
        var safe = MonteCarlo.Analyze(Build(rangeRate: 0.95), runs: 60, seed: 5);
        var riskyAgain = MonteCarlo.Analyze(Build(rangeRate: 0.4), runs: 60, seed: 5);

        Assert.True(safe.PDelivery > risky.PDelivery + 0.1,
            $"scarce range bookings must cost confidence: safe={safe.PDelivery:0.00}, risky={risky.PDelivery:0.00}");
        Assert.Equal(risky.PDelivery, riskyAgain.PDelivery, 10);
    }

    [Fact]
    public void Locations_and_campaign_location_round_trip_through_a_json_clone()
    {
        var remote = Remote(blocksCap: 2);
        remote.FlyableModel = ProbabilisticFlyableDays.Uniform(0.6, 0.4, Start);
        remote.RangeModel = ProbabilisticFlyableDays.Uniform(0.8, 0.3, Start);
        var s = Make(remote, sorties: 2, targetDay: 20);

        var clone = ScenarioJson.Clone(s);

        var loc = Assert.Single(clone.Locations);
        Assert.Equal("rng", loc.Id);
        Assert.Equal(2, loc.BlocksPerDayCap);
        Assert.Equal(1, loc.FerryDays);
        var pm = Assert.IsType<ProbabilisticFlyableDays>(loc.FlyableModel);
        Assert.Equal(0.4, pm.Persistence);
        var rm = Assert.IsType<ProbabilisticFlyableDays>(loc.RangeModel);
        Assert.Equal(0.3, rm.Persistence);
        Assert.Equal("rng", clone.Campaigns.Single().LocationId);
    }

    [Fact]
    public void Monte_carlo_runs_with_a_probabilistic_location_model_and_is_reproducible()
    {
        var remote = Remote(ferryDays: 1, setupDays: 1);
        remote.FlyableModel = ProbabilisticFlyableDays.Uniform(0.7, 0.4, Start);
        var s = Make(remote, sorties: 5, targetDay: 30);
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.9, 0.2, Start);
        s.MasterDeadline = Start.AddDays(30);

        var a = MonteCarlo.Analyze(s, runs: 40, seed: 11);
        var b = MonteCarlo.Analyze(s, runs: 40, seed: 11);

        Assert.Equal(a.PDelivery, b.PDelivery, 10);
        Assert.InRange(a.PDelivery, 0.0, 1.0);

        if (a.Campaigns[0].P50Run >= 0)
        {
            var (_, sch) = MonteCarlo.ReplayRun(s, a.Campaigns[0].P50Run, seed: 11);
            Assert.Equal(a.Campaigns[0].P50, sch.CampaignEndDates["c1"]);
        }
    }
}
