using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

public class EngineAuditTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario Probabilistic(bool periodic = false)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = ProbabilisticFlyableDays.Uniform(0.6, 0.5, Start),
            HorizonStart = Start, HorizonEnd = Start.AddDays(45),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        var t1 = new Aircraft { Id = "TC-01", Name = "TC-01" };
        if (periodic) t1.PeriodicMaintenances.Add(new PeriodicMaintenanceRule { IntervalFlightHours = 5, DurationDays = 2 });
        s.Aircraft.Add(t1);
        s.Aircraft.Add(new Aircraft { Id = "TC-02", Name = "TC-02" });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 20, TestPointsPerSortie = 2,
            StartDate = Start, TargetEndDate = Start.AddDays(30), CompatibleAircraftIds = { "TC-01", "TC-02" },
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e1", "e2" }, EngineersPerSortie = 1,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5, RehearsalRequired = false,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 12, TestPointsPerSortie = 2,
            StartDate = Start, TargetEndDate = Start.AddDays(40), CompatibleAircraftIds = { "TC-01", "TC-02" },
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e2", "e3" }, EngineersPerSortie = 1,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5, RehearsalRequired = false,
        });
        return s;
    }

    [Fact]
    public void Monte_carlo_with_zero_runs_throws_a_clear_error_not_a_crash()
    {
        var s = Probabilistic();
        Assert.Throws<ArgumentOutOfRangeException>(() => MonteCarlo.Analyze(s, runs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MonteCarlo.Evaluate(s, System.Array.Empty<ExplicitFlyableDays>()));
    }

    [Fact]
    public void Monte_carlo_with_one_run_works()
    {
        var s = Probabilistic();
        var r = MonteCarlo.Analyze(s, runs: 1, seed: 3);
        Assert.Equal(1, r.Runs);
        Assert.Equal(s.Campaigns.Count, r.Campaigns.Count);
    }

    [Fact]
    public void Simulator_double_unbook_does_not_admit_an_extra_rehearsal()
    {
        var days = new List<DateOnly> { Start };
        var pool = new SimPool(
            new List<Simulator> { new() { Id = "S1", Name = "S1", MaxBlocksPerDay = 1 } }, days, blocksPerDay: 3);
        var dev = new SimIndex(0);
        var any = new HashSet<SimIndex>();

        pool.Book(dev, 0, 0);
        pool.Unbook(dev, 0, 0);
        pool.Unbook(dev, 0, 0);
        pool.Book(dev, 0, 0);

        Assert.Null(pool.FindFree(baseId: "", block: 1, day: 0, anySim: true, compatible: any));
    }

    [Fact]
    public void DayPlan_rejects_a_zero_block_span()
    {
        var day = new DayPlan(aircraftCount: 2, blocksPerDay: 3);
        Assert.Null(day.FindSlot(new[] { 0, 1 }, new[] { 1, 2, 3 }, blocksPerSortie: 0));
        Assert.NotNull(day.FindSlot(new[] { 0, 1 }, new[] { 1, 2, 3 }, blocksPerSortie: 1));
    }

    [Fact]
    public void Greedy_monte_carlo_is_reproducible()
    {
        var s = Probabilistic();
        Assert.Equal(MonteCarlo.Analyze(s, 80, 11).PDelivery, MonteCarlo.Analyze(s, 80, 11).PDelivery);
    }

    [Fact]
    public void Advanced_monte_carlo_with_periodic_maintenance_is_reproducible_and_valid()
    {
        var s = Probabilistic(periodic: true);
        s.Config.McUseAdvancedEngine = true;

        var r1 = MonteCarlo.Analyze(s, 80, 11);
        var r2 = MonteCarlo.Analyze(s, 80, 11);

        Assert.Equal(r1.PDelivery, r2.PDelivery);
        Assert.Equal(r1.Campaigns[0].PMeet, r2.Campaigns[0].PMeet);
        Assert.InRange(r1.PDelivery, 0.0, 1.0);
        Assert.All(r1.Campaigns, c => Assert.InRange(c.PMeet, 0.0, 1.0));
    }

    private static Scenario UnreachableRemote(bool unknownLocation)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start, HorizonEnd = Start.AddDays(9),
        };
        s.Locations.Add(new Location { Id = "rng", Name = "Range", FerryDays = 20, SetupDays = 5, TeardownDays = 1 });
        s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = "rng" });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "TC-001" });
        s.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "Range work", TotalTestPoints = 3, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(9),
            LocationId = unknownLocation ? "no-such-location" : "rng",
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            EngineersPerSortie = 1, RehearsalRequired = false,
        });
        return s;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remote_campaign_with_no_deployment_is_not_flown_at_the_main_base(bool unknownLocation)
    {
        var s = UnreachableRemote(unknownLocation);

        var greedy = new Scheduler().Run(s);
        Assert.DoesNotContain(greedy.Sorties, x => x.CampaignId == "c1");
        Assert.False(greedy.CampaignCompleted.GetValueOrDefault("c1"));

        var advanced = new RevisingScheduler(new RevisingOptions()).Run(s);
        Assert.DoesNotContain(advanced.Sorties, x => x.CampaignId == "c1");
        Assert.False(advanced.CampaignCompleted.GetValueOrDefault("c1"));
    }

    [Fact]
    public void Advanced_mc_replay_reproduces_the_reported_program_finish()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = ProbabilisticFlyableDays.Uniform(0.8, 0.4, Start),
            HorizonStart = Start, HorizonEnd = Start.AddDays(40),
        };
        s.Config.McUseAdvancedEngine = true;
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Campaigns.Add(new Campaign
        {
            Id = "LONG", Name = "Long", TotalTestPoints = 16, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(30), CompatibleAircraftIds = { "T1" },
            AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" }, EngineersPerSortie = 1,
            ExpectedSortieFlightHours = 1, ExpectedSortieDutyHours = 4, RehearsalRequired = false,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "SHORT", Name = "Short", TotalTestPoints = 4, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(35), CompatibleAircraftIds = { "T1" },
            AssignedPilotIds = { "p2" }, AssignedEngineerIds = { "e2" }, EngineersPerSortie = 1,
            ExpectedSortieFlightHours = 1, ExpectedSortieDutyHours = 4, RehearsalRequired = false,
        });

        var r = MonteCarlo.Analyze(s, runs: 60, seed: 9);
        if (r.FinishP50Run >= 0)
        {
            var (_, sch) = MonteCarlo.ReplayRun(s, r.FinishP50Run, seed: 9);
            Assert.Equal(r.FinishP50, sch.ProgramFinish);
        }
        if (r.FinishP90Run >= 0)
        {
            var (_, sch) = MonteCarlo.ReplayRun(s, r.FinishP90Run, seed: 9);
            Assert.Equal(r.FinishP90, sch.ProgramFinish);
        }
    }

    [Fact]
    public void Deployed_simulator_base_does_not_book_main_base_pre_deployment_rehearsals()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3, MaxRehearsalGapDays = 2 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(40),
        };
        s.Locations.Add(new Location { Id = "rng", Name = "Range", FerryDays = 2, SetupDays = 2, TeardownDays = 1 });
        s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = "rng", MaxBlocksPerDay = 1 });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 4; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        foreach (var (id, p, e1, e2) in new[] { ("A", "p1", "e1", "e2"), ("B", "p2", "e3", "e4") })
            s.Campaigns.Add(new Campaign
            {
                Id = id, Name = id, TotalTestPoints = 8, TestPointsPerSortie = 1, StartDate = start,
                TargetEndDate = start.AddDays(35), LocationId = "rng",
                CompatibleAircraftIds = { "T1", "T2" }, AssignedPilotIds = { p }, AssignedEngineerIds = { e1, e2 },
                EngineersPerSortie = 1, RehearsalRequired = true,
            });

        var greedy = new Scheduler().Run(s);
        var dep = greedy.Deployments.SingleOrDefault(d => d.LocationId == "rng");
        if (dep is not null)
            Assert.DoesNotContain(greedy.Rehearsals, r => r.Date < dep.FlyFrom);
    }

    [Fact]
    public void Repair_never_makes_a_crew_member_exceed_max_consecutive_flying_days()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig
            {
                PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3, MaxConsecutiveFlyingDays = 3,
            },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(40),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 4; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 20, TestPointsPerSortie = 1, StartDate = start,
            TargetEndDate = start.AddDays(25), CompatibleAircraftIds = { "T1", "T2" },
            AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1", "e2" }, EngineersPerSortie = 1,
            ExpectedSortieFlightHours = 1, ExpectedSortieDutyHours = 4, RehearsalRequired = false,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 20, TestPointsPerSortie = 1, StartDate = start,
            TargetEndDate = start.AddDays(20), CompatibleAircraftIds = { "T1", "T2" },
            AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e3", "e4" }, EngineersPerSortie = 1,
            ExpectedSortieFlightHours = 1, ExpectedSortieDutyHours = 4, RehearsalRequired = false,
        });

        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s);

        var perPerson = new Dictionary<string, SortedSet<int>>();
        foreach (var so in revised.Sorties)
            foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.EngineerIds).Concat(so.SupportPilotIds))
                (perPerson.TryGetValue(pid, out var set) ? set : perPerson[pid] = new SortedSet<int>())
                    .Add(so.Date.DayNumber);
        foreach (var (pid, days) in perPerson)
        {
            int run = 1, max = 1, prev = int.MinValue;
            foreach (var d in days) { run = d == prev + 1 ? run + 1 : 1; max = Math.Max(max, run); prev = d; }
            Assert.True(max <= s.Config.MaxConsecutiveFlyingDays,
                $"{pid} flies {max} consecutive days (cap {s.Config.MaxConsecutiveFlyingDays})");
        }
    }

    [Fact]
    public void Execution_tracking_with_repair_keeps_periodic_legality_and_the_paced_gap()
    {
        var s = Probabilistic(periodic: true);
        s.FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start };
        s.Campaigns[0].MinWorkdaysBetweenSorties = 2;

        var baseline = new Scheduler().Run(s);
        var freeze = Start.AddDays(14);
        foreach (var x in baseline.Sorties.Where(x => x.Date < freeze)) s.ActualSorties.Add(x);
        foreach (var x in baseline.Rehearsals.Where(x => x.Date < freeze)) s.ActualRehearsals.Add(x);
        s.FreezeDate = freeze;

        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s);

        foreach (var kv in revised.AutoMaintenance)
            foreach (var w in kv.Value)
                Assert.DoesNotContain(revised.Sorties.Where(x => x.AircraftId == kv.Key), so => w.Contains(so.Date));

        var aDates = revised.Sorties.Where(x => x.CampaignId == "A").Select(x => x.Date).Distinct().OrderBy(d => d).ToList();
        for (int j = 1; j < aDates.Count; j++)
            Assert.True(aDates[j].DayNumber - aDates[j - 1].DayNumber >= 3,
                $"campaign A flew {aDates[j - 1]:MM-dd} then {aDates[j]:MM-dd} — closer than the 2-workday review gap");
    }

    [Fact]
    public void Advanced_engine_never_flies_a_tail_during_its_periodic_maintenance()
    {
        var s = Probabilistic(periodic: true);
        s.FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = Start };
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s);

        foreach (var kv in revised.AutoMaintenance)
            foreach (var w in kv.Value)
                foreach (var so in revised.Sorties.Where(x => x.AircraftId == kv.Key))
                    Assert.False(w.Contains(so.Date),
                        $"{kv.Key} flew {so.Date:yyyy-MM-dd} inside its periodic window {w.Start:MM-dd}..{w.End:MM-dd}");
    }

    [Fact]
    public void Deterministic_Suggest_proposes_nothing_when_resources_cannot_change_met_or_lateness()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = Start },
            HorizonStart = Start, HorizonEnd = Start.AddDays(9),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-01", Name = "TC-01" });
        s.Campaigns.Add(new Campaign
        {
            Id = "huge", Name = "Huge", TotalTestPoints = 200, TestPointsPerSortie = 1, BlocksPerSortie = 3,
            StartDate = Start, TargetEndDate = Start.AddDays(7),
            CompatibleAircraftIds = { "TC-01" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            RehearsalRequired = false,
        });

        var result = ResourceSuggester.Suggest(s, maxAdditions: 3);

        Assert.False(result.AllMet);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void Sensitivity_omits_the_retest_factor_when_only_the_abort_override_is_active()
    {
        var s = Probabilistic();
        s.Config.OverrideSortieAbort = true;
        s.Config.SortieAbortOverride = 0.1;
        Assert.DoesNotContain(s.Campaigns, c => c.TestEfficiencyEnabled);

        var sens = MonteCarlo.Sensitivity(s, runs: 20, seed: 7, sobolSamples: 16);

        Assert.Contains(sens.Factors, f => f.Name.Contains("Abort"));
        Assert.DoesNotContain(sens.Factors, f => f.Name.Contains("Retest"));
        Assert.DoesNotContain(sens.Sweeps, sw => sw.Name.Contains("Retest"));
    }

    [Fact]
    public void Deployed_campaign_gated_by_a_phase_sharing_its_crew_does_not_deadlock()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(60),
        };
        s.Pilots.Add(new Pilot { Id = "P1", Name = "P1", ShortName = "P1" });
        s.Engineers.Add(new Engineer { Id = "E1", Name = "E1", ShortName = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Locations.Add(new Location { Id = "L", Name = "Range", FerryDays = 1, SetupDays = 1, TeardownDays = 1, FerryFlightHours = 1, FerryDutyHours = 4 });
        s.Simulators.Add(new Simulator { Id = "SIM-L", Name = "Range sim", LocationId = "L" });
        s.CampaignGroups.Add(new CampaignGroup { Id = "PH-A", Name = "Phase A", TargetEndDate = start.AddDays(30) });
        s.CampaignGroups.Add(new CampaignGroup { Id = "PH-B", Name = "Phase B", PrerequisiteGroupIds = { "PH-A" }, TargetEndDate = start.AddDays(55) });
        s.Campaigns.Add(new Campaign
        {
            Id = "A1", Name = "Upstream (main)", GroupId = "PH-A",
            TotalTestPoints = 6, TestPointsPerSortie = 1, StartDate = start, TargetEndDate = start.AddDays(30),
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "E1" },
            EngineersPerSortie = 1, RehearsalRequired = false, ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B1", Name = "Deployed (shares P1)", GroupId = "PH-B", LocationId = "L",
            TotalTestPoints = 4, TestPointsPerSortie = 1, StartDate = start, TargetEndDate = start.AddDays(55),
            CompatibleAircraftIds = { "T2" }, AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "E1" },
            EngineersPerSortie = 1, RehearsalRequired = false, ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5,
        });

        foreach (var (name, eng) in new (string, ISchedulerService)[]
        {
            ("greedy", new Scheduler()),
            ("advanced", new RevisingScheduler(new RevisingOptions
            {
                EnableLocalRepair = true, EnableTightnessOrder = true,
                EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
            })),
        })
        {
            var sch = eng.Run(s);
            Assert.True(sch.CampaignCompleted.GetValueOrDefault("A1"), $"[{name}] upstream A1 must complete (no deadlock)");
            Assert.True(sch.CampaignCompleted.GetValueOrDefault("B1"), $"[{name}] deployed B1 must complete after A1");
            var aEnd = sch.Sorties.Where(x => x.CampaignId == "A1").Max(x => x.Date);
            var bStart = sch.Sorties.Where(x => x.CampaignId == "B1").Min(x => x.Date);
            Assert.True(bStart > aEnd, $"[{name}] B1 (deployed) must fly only after A1 completes (A↔A)");
        }
    }
}
