using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class V2FeatureTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static FlightCalendar NoWeekend() => new() { WeekendDays = new() };
    private static FlyableDayModel AllFlyable() => new RateBasedFlyableDays { Rate = 1, PatternLength = 1 };

    private static Scenario Scenario(
        int horizonDays, int pilots, int engineers, int aircraft,
        ScenarioConfig? cfg = null, FlyableDayModel? flyable = null)
    {
        var s = new Scenario
        {
            Config = cfg ?? new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = NoWeekend(),
            FlyableModel = flyable ?? AllFlyable(),
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= pilots; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= engineers; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        for (int i = 1; i <= aircraft; i++) s.Aircraft.Add(new Aircraft { Id = $"ac{i}", Name = $"Tail-{i}" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(
        Scenario s, string id, int requiredSorties, DateOnly? start = null, DateOnly? target = null)
    {
        var c = new Campaign
        {
            Id = id,
            Name = id,
            TotalTestPoints = requiredSorties,
            TestPointsPerSortie = 1,
            StartDate = start ?? Start,
            TargetEndDate = target ?? Start.AddDays(60),
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Power_law_retest_curve_has_a_heavier_tail_than_exponential()
    {
        Campaign Make(RetestModel m) => new()
        {
            TotalTestPoints = 40, TestPointsPerSortie = 1, TestEfficiencyEnabled = true,
            RetestProbabilityStart = 0.4, RetestProbabilityFloor = 0.0,
            RetestLearningScale = 10, RetestCurve = m, RetestExponent = 1,
        };
        var exp = Make(RetestModel.Exponential);
        var pow = Make(RetestModel.PowerLaw);

        Assert.Equal(0.4, pow.RetestProbabilityAt(0), 6);
        Assert.Equal(0.2, pow.RetestProbabilityAt(10), 6);

        Assert.True(pow.ExpectedInflation() > exp.ExpectedInflation(),
            $"power-law inflation {pow.ExpectedInflation():0.###} should exceed exponential {exp.ExpectedInflation():0.###}");

        Assert.Equal(RetestModel.Exponential, new Campaign().RetestCurve);
    }

    [Fact]
    public void Weather_persistence_preserves_the_mean_but_raises_the_variance()
    {
        const int d = 30, m = 8000;
        const double rate = 0.5;

        (double Mean, double Var) Stats(double phi)
        {
            var model = new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(rate, 12).ToArray(),
                Persistence = phi, PatternLength = 7, Anchor = Start,
            };
            var rng = new Random(2026);
            double sum = 0, sumSq = 0;
            for (int i = 0; i < m; i++)
            {
                int cnt = model.Sample(Start, Start.AddDays(d - 1), rng).Days.Count;
                sum += cnt; sumSq += (double)cnt * cnt;
            }
            double mean = sum / m;
            return (mean, sumSq / m - mean * mean);
        }

        var lo = Stats(0.0);
        var hi = Stats(0.9);
        Assert.Equal(rate * d, lo.Mean, 1.0);
        Assert.Equal(rate * d, hi.Mean, 1.0);
        Assert.True(hi.Var > lo.Var * 2,
            $"expected variance to grow with φ: low={lo.Var:0.#}, high={hi.Var:0.#}");
    }

    [Fact]
    public void Persistence_helps_the_underdog_and_hurts_the_favorite()
    {
        double underLow = DeliveryAtPhi(0.0, rate: 0.5, horizon: 50, deadlineDay: 29, sorties: 20);
        double underHigh = DeliveryAtPhi(0.95, rate: 0.5, horizon: 50, deadlineDay: 29, sorties: 20);
        Assert.True(underHigh > underLow + 0.1,
            $"underdog should gain from φ: low={underLow:0.000}, high={underHigh:0.000}");

        double comfyLow = DeliveryAtPhi(0.0, rate: 0.6, horizon: 50, deadlineDay: 49, sorties: 10);
        double comfyHigh = DeliveryAtPhi(0.95, rate: 0.6, horizon: 50, deadlineDay: 49, sorties: 10);
        Assert.True(comfyHigh < comfyLow - 0.1,
            $"favorite should lose from φ: low={comfyLow:0.000}, high={comfyHigh:0.000}");
    }

    private static double DeliveryAtPhi(double phi, double rate, int horizon, int deadlineDay, int sorties)
    {
        var cfg = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 };
        var s = Scenario(horizon, pilots: 3, engineers: 3, aircraft: 1, cfg,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(rate, 12).ToArray(),
                Persistence = phi, PatternLength = 7, Anchor = Start,
            });
        s.MasterDeadline = Start.AddDays(deadlineDay);
        var c = Camp(s, "c1", requiredSorties: sorties, target: Start.AddDays(deadlineDay));
        c.BlocksPerSortie = 3;
        return MonteCarlo.Analyze(s, runs: 400, seed: 777).PDelivery;
    }

    [Fact]
    public void Frozen_order_injection_matches_internal_computation_under_the_advanced_engine()
    {
        var cfg = new ScenarioConfig
        {
            PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3, McUseAdvancedEngine = true,
        };
        var s = Scenario(30, pilots: 2, engineers: 2, aircraft: 1, cfg,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
                Persistence = 0.5, PatternLength = 7, Anchor = Start,
            });
        var a = Camp(s, "A", requiredSorties: 5, target: Start.AddDays(12));
        a.BlocksPerSortie = 3; a.RehearsalRequired = false;
        var b = Camp(s, "B", requiredSorties: 5, target: Start.AddDays(20));
        b.BlocksPerSortie = 3; b.RehearsalRequired = false;

        var order = MonteCarlo.FrozenOrder(s);
        Assert.NotNull(order);

        const int runs = 200, seed = 4242;
        var computed = MonteCarlo.Analyze(s, runs, seed);
        var injected = MonteCarlo.Analyze(s, runs, seed, null, default,
            MonteCarlo.UncertaintySources.All, order);

        Assert.Equal(computed.PDelivery, injected.PDelivery);
        Assert.Equal(computed.Campaigns.Count, injected.Campaigns.Count);
        for (int i = 0; i < computed.Campaigns.Count; i++)
            Assert.Equal(computed.Campaigns[i].PMeet, injected.Campaigns[i].PMeet);
    }

    [Fact]
    public void Order_spread_sensitivity_bands_only_when_requested()
    {
        var cfg = new ScenarioConfig
        {
            PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3, McUseAdvancedEngine = true,
        };
        var s = Scenario(30, pilots: 2, engineers: 2, aircraft: 1, cfg,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.55, 12).ToArray(),
                Persistence = 0.6, PatternLength = 7, Anchor = Start,
            });
        var a = Camp(s, "A", requiredSorties: 6, target: Start.AddDays(14));
        a.BlocksPerSortie = 3; a.RehearsalRequired = false;
        var b = Camp(s, "B", requiredSorties: 6, target: Start.AddDays(22));
        b.BlocksPerSortie = 3; b.RehearsalRequired = false;

        var single = MonteCarlo.Sensitivity(s, runs: 80, seed: 99, sobolSamples: 16, orderSpread: false);
        Assert.All(single.Factors, f =>
        {
            Assert.False(f.HasBand);
            Assert.Equal(f.LowPDelivery, f.LowPDeliveryLo);
            Assert.Equal(f.LowPDelivery, f.LowPDeliveryHi);
        });

        var spread = MonteCarlo.Sensitivity(s, runs: 80, seed: 99, sobolSamples: 16, orderSpread: true);
        Assert.All(spread.Factors, f =>
        {
            Assert.True(f.HasBand);
            Assert.InRange(f.LowPDelivery, f.LowPDeliveryLo - 1e-9, f.LowPDeliveryHi + 1e-9);
            Assert.InRange(f.HighPDelivery, f.HighPDeliveryLo - 1e-9, f.HighPDeliveryHi + 1e-9);
        });
        Assert.All(spread.Sweeps.SelectMany(sw => sw.Points), p =>
            Assert.InRange(p.PDelivery, p.PDeliveryLo - 1e-9, p.PDeliveryHi + 1e-9));
        Assert.NotEmpty(spread.GlobalImportance);
    }

    [Fact]
    public void One_aircraft_can_fly_up_to_BlocksPerDay_sorties_per_day()
    {
        var cfg = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 2, BlocksPerDay = 3 };
        var s = Scenario(horizonDays: 8, pilots: 3, engineers: 6, aircraft: 1, cfg);
        Camp(s, "c1", requiredSorties: 9);

        var sch = new Scheduler().Run(s);

        Assert.Equal(9, sch.Sorties.Count);
        var perDay = sch.Sorties.GroupBy(x => x.Date).ToList();
        Assert.All(perDay, g => Assert.InRange(g.Count(), 1, 3));

        var full = perDay.First(g => g.Count() == 3);
        Assert.Equal(new[] { 1, 2, 3 }, full.Select(x => x.Block).OrderBy(b => b));
        Assert.All(full, x => Assert.Equal("ac1", x.AircraftId));
    }

    [Fact]
    public void Flutter_BlocksPerSortie_equal_to_BlocksPerDay_ties_up_the_tail_all_day()
    {
        var cfg = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 2, BlocksPerDay = 3 };
        var s = Scenario(horizonDays: 10, pilots: 3, engineers: 6, aircraft: 1, cfg);
        Camp(s, "flut", requiredSorties: 4).BlocksPerSortie = 3;

        var sch = new Scheduler().Run(s);

        Assert.Equal(4, sch.Sorties.Count);
        Assert.All(sch.Sorties, x => Assert.Equal(1, x.Block));
        Assert.Equal(sch.Sorties.Count, sch.Sorties.Select(x => x.Date).Distinct().Count());
    }

    [Fact]
    public void AllowedBlocks_restricts_the_start_block()
    {
        var cfg = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 };
        var s = Scenario(horizonDays: 10, pilots: 2, engineers: 2, aircraft: 1, cfg);
        Camp(s, "c1", requiredSorties: 4).AllowedBlocks.Add(2);

        var sch = new Scheduler().Run(s);

        Assert.Equal(4, sch.Sorties.Count);
        Assert.All(sch.Sorties, x => Assert.Equal(2, x.Block));
        Assert.Equal(sch.Sorties.Count, sch.Sorties.Select(x => x.Date).Distinct().Count());
    }

    [Fact]
    public void No_person_day_block_is_double_booked_across_sorties_and_rehearsals()
    {
        var cfg = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 2, BlocksPerDay = 3 };
        var s = Scenario(horizonDays: 10, pilots: 3, engineers: 6, aircraft: 2, cfg);
        Camp(s, "c1", requiredSorties: 10);

        var sch = new Scheduler().Run(s);
        Assert.Equal(10, sch.Sorties.Count);

        var slots = new List<(string Person, DateOnly Day, int Block)>();
        foreach (var so in sch.Sorties)
        {
            slots.Add((so.PilotId, so.Date, so.Block));
            slots.AddRange(so.EngineerIds.Select(e => (e, so.Date, so.Block)));
        }
        foreach (var r in sch.Rehearsals)
        {
            slots.Add((r.PilotId, r.Date, r.Block));
            slots.AddRange(r.EngineerIds.Select(e => (e, r.Date, r.Block)));
        }

        Assert.Equal(slots.Count, slots.Distinct().Count());
    }

    [Fact]
    public void Maintenance_window_blocks_the_tail_and_is_tallied_as_aircraft_bottleneck()
    {
        var s = Scenario(horizonDays: 10, pilots: 1, engineers: 1, aircraft: 1);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(4) });
        Camp(s, "c1", requiredSorties: 2);

        var sch = new Scheduler().Run(s);

        Assert.Equal(2, sch.Sorties.Count);
        Assert.All(sch.Sorties, x => Assert.True(x.Date > Start.AddDays(4),
            $"sortie on {x.Date:yyyy-MM-dd} falls inside the maintenance window"));
        Assert.Equal(5, sch.Bottlenecks["c1"].Aircraft);
    }

    [Fact]
    public void Leave_window_blocks_both_sorties_and_rehearsals_for_that_person()
    {
        var s = Scenario(horizonDays: 12, pilots: 1, engineers: 1, aircraft: 1);
        var leave = new DateRange { Start = Start.AddDays(2), End = Start.AddDays(6) };
        s.Pilots[0].LeaveWindows.Add(leave);
        Camp(s, "c1", requiredSorties: 2);

        var sch = new Scheduler().Run(s);

        Assert.Equal(2, sch.Sorties.Count);
        Assert.All(sch.Sorties, x => Assert.False(leave.Contains(x.Date),
            $"sortie on {x.Date:yyyy-MM-dd} falls inside the pilot's leave"));
        Assert.All(sch.Rehearsals, r => Assert.False(leave.Contains(r.Date),
            $"rehearsal on {r.Date:yyyy-MM-dd} falls inside the pilot's leave"));
        Assert.True(sch.Bottlenecks["c1"].Pilot > 0);
    }

    [Fact]
    public void Open_campaign_on_unusable_days_tallies_NotUsable()
    {
        var flyable = new ExplicitFlyableDays();
        for (int i = 5; i <= 9; i++) flyable.Days.Add(Start.AddDays(i));
        var s = Scenario(horizonDays: 10, pilots: 1, engineers: 1, aircraft: 1, flyable: flyable);
        Camp(s, "c1", requiredSorties: 1);

        var sch = new Scheduler().Run(s);

        Assert.Equal(5, sch.Bottlenecks["c1"].NotUsable);
        Assert.True(sch.CampaignCompleted["c1"]);
    }

    [Fact]
    public void Waiting_on_a_prerequisite_tallies_Prereq()
    {
        var s = Scenario(horizonDays: 15, pilots: 1, engineers: 1, aircraft: 1);
        Camp(s, "A", requiredSorties: 3);
        Camp(s, "B", requiredSorties: 1).PrerequisiteCampaignIds.Add("A");

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["A"]);
        Assert.True(sch.CampaignCompleted["B"]);
        Assert.True(sch.Bottlenecks["B"].Prereq > 0, "B must have waited on A");
        Assert.Equal("prerequisite", sch.Bottlenecks["B"].Dominant()!.Value.Reason);
    }

    [Fact]
    public void Crew_work_is_spread_evenly_across_assigned_pilots()
    {
        var s = Scenario(horizonDays: 25, pilots: 2, engineers: 2, aircraft: 1);
        Camp(s, "c1", requiredSorties: 10);

        var sch = new Scheduler().Run(s);
        Assert.Equal(10, sch.Sorties.Count);

        var byPilot = sch.Sorties.GroupBy(x => x.PilotId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, byPilot.Count);
        Assert.True(Math.Abs(byPilot["p1"] - byPilot["p2"]) <= 2,
            $"unbalanced pilot load: {string.Join(", ", byPilot.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    [Fact]
    public void Probabilistic_nominal_pattern_follows_the_monthly_rates()
    {
        var m = new ProbabilisticFlyableDays
        {
            MonthlyRates = Enumerable.Repeat(0.5, 12).ToArray(),
            PatternLength = 10,
            Anchor = new DateOnly(2026, 1, 1),
        };
        m.MonthlyRates[0] = 0.0;
        m.MonthlyRates[6] = 1.0;

        for (var d = new DateOnly(2026, 1, 1); d.Month == 1; d = d.AddDays(1))
            Assert.False(m.IsFlyable(d));
        for (var d = new DateOnly(2026, 7, 1); d.Month == 7; d = d.AddDays(1))
            Assert.True(m.IsFlyable(d));
    }

    [Fact]
    public void Markov_sample_preserves_the_long_run_rate()
    {
        var anchor = new DateOnly(2026, 1, 1);
        var end = anchor.AddDays(4999);

        foreach (double phi in new[] { 0.0, 0.8 })
        {
            var m = ProbabilisticFlyableDays.Uniform(0.5, phi, anchor);
            var sample = m.Sample(anchor, end, new Random(42));
            double rate = sample.Days.Count / 5000.0;
            Assert.InRange(rate, 0.40, 0.60);
        }
    }

    [Fact]
    public void Persistence_lengthens_weather_spells_without_changing_the_rate()
    {
        var anchor = new DateOnly(2026, 1, 1);
        var end = anchor.AddDays(4999);

        double MeanRunLength(double phi)
        {
            var sample = ProbabilisticFlyableDays.Uniform(0.5, phi, anchor).Sample(anchor, end, new Random(7));
            var runs = new List<int>();
            int run = 0;
            for (var d = anchor; d <= end; d = d.AddDays(1))
            {
                if (sample.IsFlyable(d)) run++;
                else if (run > 0) { runs.Add(run); run = 0; }
            }
            if (run > 0) runs.Add(run);
            return runs.Average();
        }

        Assert.True(MeanRunLength(0.8) > 2 * MeanRunLength(0.0),
            "high persistence must produce much longer flyable spells");
    }

    [Fact]
    public void MonteCarlo_requires_a_probabilistic_model()
    {
        var s = Scenario(horizonDays: 10, pilots: 1, engineers: 1, aircraft: 1);
        Camp(s, "c1", requiredSorties: 1);
        Assert.Throws<InvalidOperationException>(() => MonteCarlo.Analyze(s, runs: 5));
    }

    [Fact]
    public void MonteCarlo_reports_high_confidence_for_an_easy_deadline_and_zero_for_an_impossible_one()
    {
        var s = Scenario(horizonDays: 30, pilots: 2, engineers: 2, aircraft: 1);
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.9, 0.3, Start);
        Camp(s, "easy", requiredSorties: 2, target: Start.AddDays(29));
        Camp(s, "impossible", requiredSorties: 60, target: Start.AddDays(5));

        var result = MonteCarlo.Analyze(s, runs: 50, seed: 1);

        var easy = result.Campaigns.Single(c => c.CampaignId == "easy");
        var impossible = result.Campaigns.Single(c => c.CampaignId == "impossible");
        Assert.True(easy.PMeet >= 0.9, $"easy campaign PMeet={easy.PMeet}");
        Assert.NotNull(easy.P50);
        Assert.Equal(0, impossible.PMeet);
        Assert.Equal(1, impossible.PIncomplete);
    }

    [Fact]
    public void Common_random_numbers_same_seed_gives_identical_realizations()
    {
        var m = ProbabilisticFlyableDays.Uniform(0.6, 0.5, Start);
        var a = MonteCarlo.SampleRealizations(m, Start, Start.AddDays(99), runs: 5, seed: 7);
        var b = MonteCarlo.SampleRealizations(m, Start, Start.AddDays(99), runs: 5, seed: 7);

        for (int k = 0; k < 5; k++)
            Assert.True(a[k].Days.SetEquals(b[k].Days));
    }

    [Fact]
    public void ScenarioComparison_shows_the_improvement_of_an_added_aircraft()
    {
        var baseline = Scenario(horizonDays: 12, pilots: 1, engineers: 1, aircraft: 1);
        baseline.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(6) });
        Camp(baseline, "c1", requiredSorties: 2, target: Start.AddDays(5));

        var improved = ScenarioJson.Clone(baseline);
        improved.Aircraft.Add(new Aircraft { Id = "ac2", Name = "Tail-2" });
        improved.Campaigns[0].CompatibleAircraftIds.Add("ac2");

        var result = ScenarioComparison.Compare(
            new[] { baseline, improved }, new[] { "baseline", "+1 aircraft" });

        Assert.Equal(0, result.MetCounts[0]);
        Assert.Equal(1, result.MetCounts[1]);
        Assert.Equal(CampaignStatus.Met, result.Rows[0].Cells[1].Status);
    }

    [Fact]
    public void Periodic_maintenance_blocks_the_tail_after_the_flight_hour_interval()
    {
        var s = Scenario(horizonDays: 40, pilots: 2, engineers: 2, aircraft: 1);
        s.Aircraft[0].PeriodicMaintenances.Add(new PeriodicMaintenanceRule
        {
            IntervalFlightHours = 2,
            DurationDays = 5,
        });
        Camp(s, "c1", requiredSorties: 4);

        var sch = new Scheduler().Run(s);

        Assert.Equal(4, sch.Sorties.Count);
        Assert.True(sch.AutoMaintenance.TryGetValue("ac1", out var windows));
        Assert.True(windows!.Count >= 1);

        foreach (var w in windows)
        {
            Assert.DoesNotContain(sch.Sorties, x => x.AircraftId == "ac1" && w.Contains(x.Date));
            Assert.Equal("2h check", w.Note);
            Assert.Equal(5, w.Days);
        }
    }

    [Fact]
    public void Program_finish_is_always_computed()
    {
        var s = Scenario(horizonDays: 20, pilots: 2, engineers: 2, aircraft: 1);
        Camp(s, "c1", requiredSorties: 4);

        var sch = new Scheduler().Run(s);

        Assert.Equal(sch.Sorties.Max(x => x.Date), sch.ProgramFinish);
        Assert.True(sch.AllCampaignsCompleted());
        Assert.True(sch.MeetsMaster(null));
    }

    [Fact]
    public void Master_deadline_overrun_is_planned_through_and_reported()
    {
        var s = Scenario(horizonDays: 40, pilots: 2, engineers: 2, aircraft: 2);
        s.MasterDeadline = Start.AddDays(3);
        Camp(s, "c1", requiredSorties: 12);

        var sch = new Scheduler().Run(s);

        Assert.Contains(sch.Sorties, x => x.Date > s.MasterDeadline!.Value);

        var report = ProgramReporter.Build(s, sch);
        Assert.False(report.Delivered);
        Assert.True(report.SlackDays < 0);
        Assert.True(report.OverdueSorties > 0);
        Assert.Equal(sch.ProgramFinish, report.ProgramFinish);
    }

    [Fact]
    public void Finishing_early_reports_slack_and_resource_usage()
    {
        var s = Scenario(horizonDays: 40, pilots: 2, engineers: 2, aircraft: 2);
        s.MasterDeadline = Start.AddDays(39);
        Camp(s, "c1", requiredSorties: 4);

        var sch = new Scheduler().Run(s);

        var report = ProgramReporter.Build(s, sch);
        Assert.True(report.AllCompleted);
        Assert.True(report.Delivered);
        Assert.True(report.SlackDays > 0);
        Assert.Equal(4, report.TotalSorties);
        Assert.True(report.TotalFlightHours > 0);
        Assert.True(report.TotalDutyHours > 0);
        Assert.True(report.AircraftUsed >= 1);
        Assert.True(report.CrewUsed >= 2);
    }

    [Fact]
    public void Soft_milestone_slips_do_not_fail_delivery()
    {
        var s = Scenario(horizonDays: 30, pilots: 2, engineers: 2, aircraft: 2);
        s.MasterDeadline = Start.AddDays(29);
        var c = Camp(s, "c1", requiredSorties: 8, target: Start.AddDays(1));

        var sch = new Scheduler().Run(s);
        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.False(sch.MeetsDeadline("c1", c.TargetEndDate));

        var report = ProgramReporter.Build(s, sch);
        Assert.True(report.Delivered);
        Assert.Equal(MilestoneStatus.Missed, report.Campaigns.Single().Status);
    }

    [Fact]
    public void MC_percentile_runs_replay_to_the_reported_outcome()
    {
        var s = Scenario(horizonDays: 40, pilots: 2, engineers: 2, aircraft: 2,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
                Persistence = 0.4,
                PatternLength = 7,
            });
        s.MasterDeadline = Start.AddDays(30);
        Camp(s, "c1", requiredSorties: 6);

        var result = MonteCarlo.Analyze(s, runs: 60, seed: 7);

        Assert.NotNull(result.FinishP50);
        Assert.True(result.FinishP50Run >= 0);

        var (_, sch1) = MonteCarlo.ReplayRun(s, result.FinishP50Run, seed: 7);
        Assert.Equal(result.FinishP50, sch1.ProgramFinish);

        var camp = result.Campaigns.Single();
        Assert.True(camp.P50Run >= 0);
        var (_, sch2) = MonteCarlo.ReplayRun(s, camp.P50Run, seed: 7);
        Assert.Equal(camp.P50, sch2.CampaignEndDates["c1"]);

        var (_, sch3) = MonteCarlo.ReplayRun(s, camp.P50Run, seed: 7);
        Assert.Equal(sch2.Sorties.Count, sch3.Sorties.Count);
        Assert.Equal(sch2.ProgramFinish, sch3.ProgramFinish);
    }

    [Fact]
    public void Recommender_targets_the_program_when_only_the_master_deadline_fails()
    {
        var s = Scenario(horizonDays: 40, pilots: 2, engineers: 2, aircraft: 1,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
                Persistence = 0.3,
                PatternLength = 7,
            });
        s.MasterDeadline = Start.AddDays(7);
        Camp(s, "c1", requiredSorties: 10, target: DateOnly.MaxValue);

        var result = ResourceSuggester.Recommend(
            s, ResourceSuggester.RecommenderLevers.AddAircraft, runs: 40, targetConfidence: 0.9,
            maxSteps: 2, seed: 11);

        Assert.NotEmpty(result.Steps);
        Assert.All(result.Steps, st => Assert.Contains("program", st.Effect));
    }

    [Fact]
    public void Expected_inflation_math_is_right_and_off_by_default()
    {
        var clean = new Campaign { TotalTestPoints = 30, TestPointsPerSortie = 5 };
        Assert.Equal(1.0, clean.ExpectedInflation(), 10);
        Assert.Equal(0.0, clean.RetestProbabilityAt(0), 10);

        var c = new Campaign
        {
            TotalTestPoints = 30,
            TestPointsPerSortie = 5,
            TestEfficiencyEnabled = true,
            RetestProbabilityStart = 0.2,
            RetestProbabilityFloor = 0.2,
            RetestLearningScale = 10,
            AbortProbability = 0.1,
        };
        Assert.Equal(1.0 / (0.8 * 0.9), c.ExpectedInflation(), 6);
        Assert.Equal(0.2, c.RetestProbabilityAt(100), 6);

        c.TestEfficiencyEnabled = false;
        Assert.Equal(1.0, c.ExpectedInflation(), 10);
        Assert.Equal(0.0, c.RetestProbabilityAt(0), 10);
        c.TestEfficiencyEnabled = true;

        var learning = new Campaign
        {
            TotalTestPoints = 30,
            TestEfficiencyEnabled = true,
            RetestProbabilityStart = 0.5,
            RetestProbabilityFloor = 0.1,
            RetestLearningScale = 10,
        };
        Assert.Equal(0.5, learning.RetestProbabilityAt(0), 6);
        Assert.True(learning.RetestProbabilityAt(10) < 0.5);
        Assert.True(learning.RetestProbabilityAt(200) < 0.11);
    }

    [Fact]
    public void Nominal_switch_inflates_the_required_work()
    {
        var s = Scenario(horizonDays: 60, pilots: 2, engineers: 2, aircraft: 2);
        var c = Camp(s, "c1", requiredSorties: 10);
        c.TestEfficiencyEnabled = true;
        c.RetestProbabilityStart = 0.5;
        c.RetestProbabilityFloor = 0.5;
        c.RetestLearningScale = 10;

        var raw = new Scheduler().Run(s);
        Assert.Equal(10, raw.Sorties.Count);

        s.Config.SimulateYieldInNominal = true;
        var inflated = new Scheduler().Run(s);
        Assert.Equal(20, inflated.Sorties.Count);
    }

    [Fact]
    public void Stochastic_yield_model_with_zero_rates_is_lossless()
    {
        var s = Scenario(horizonDays: 30, pilots: 2, engineers: 2, aircraft: 2);
        Camp(s, "c1", requiredSorties: 6);

        var baseline = new Scheduler().Run(s);
        var stochastic = new Scheduler().Run(s, new StochasticYieldModel(new Random(123)));

        Assert.Equal(baseline.Sorties.Count, stochastic.Sorties.Count);
        Assert.Equal(baseline.ProgramFinish, stochastic.ProgramFinish);
    }

    [Fact]
    public void Stochastic_yield_losses_require_extra_sorties()
    {
        var s = Scenario(horizonDays: 90, pilots: 2, engineers: 2, aircraft: 2);
        var c = Camp(s, "c1", requiredSorties: 10);
        c.TestEfficiencyEnabled = true;
        c.RetestProbabilityStart = 0.5;
        c.RetestProbabilityFloor = 0.5;
        c.RetestLearningScale = 10;
        c.AbortProbability = 0.2;

        var sch = new Scheduler().Run(s, new StochasticYieldModel(new Random(42)));

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Sorties.Count > 10);
        Assert.Contains(sch.Sorties, x => x.Aborted);
        Assert.All(sch.Sorties.Where(x => x.Aborted), x => Assert.Equal(0, x.TestPointsFlown));
        Assert.Equal(10, sch.Sorties.Sum(x => x.TestPointsFlown), 6);
    }

    [Fact]
    public void Abort_override_applies_to_every_campaign_even_with_efficiency_off()
    {
        var s = Scenario(horizonDays: 90, pilots: 2, engineers: 2, aircraft: 2);
        Camp(s, "c1", requiredSorties: 10);
        s.Config.OverrideSortieAbort = true;
        s.Config.SortieAbortOverride = 0.5;

        var sch = new Scheduler().Run(s, new StochasticYieldModel(
            new Random(42), s.Config.AbortOverrideOrNull));

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Sorties.Count > 10);
        Assert.Contains(sch.Sorties, x => x.TestPointsFlown == 0);

        Assert.Equal(2.0, s.Campaigns[0].ExpectedInflation(0.5), 6);
    }

    [Fact]
    public void MC_with_yield_losses_widens_and_delays_the_program_finish()
    {
        Scenario Make(bool lossy)
        {
            var s = Scenario(horizonDays: 90, pilots: 2, engineers: 2, aircraft: 2,
                flyable: new ProbabilisticFlyableDays
                {
                    MonthlyRates = Enumerable.Repeat(0.7, 12).ToArray(),
                    Persistence = 0.3,
                    PatternLength = 7,
                });
            var c = Camp(s, "c1", requiredSorties: 10);
            if (lossy)
            {
                c.TestEfficiencyEnabled = true;
                c.RetestProbabilityStart = 0.5;
                c.RetestProbabilityFloor = 0.3;
                c.RetestLearningScale = 5;
                c.AbortProbability = 0.15;
            }
            return s;
        }

        var clean = MonteCarlo.Analyze(Make(lossy: false), runs: 60, seed: 21);
        var lossy = MonteCarlo.Analyze(Make(lossy: true), runs: 60, seed: 21);

        Assert.True(lossy.FinishP50 > clean.FinishP50);
        Assert.True(lossy.Campaigns.Single().P90 >= clean.Campaigns.Single().P90);

        Assert.True(lossy.SortiesP50 > clean.SortiesP50);
        Assert.True(lossy.AbortedSortiesMean > 0);
        Assert.Equal(0, clean.AbortedSortiesMean, 10);
        Assert.Equal(10, clean.SortiesP50);

        var weatherOnly = MonteCarlo.Analyze(Make(lossy: true), runs: 60, seed: 21,
            sources: MonteCarlo.UncertaintySources.Weather);
        Assert.Equal(clean.FinishP50, weatherOnly.FinishP50);
        Assert.Equal(clean.PDelivery, weatherOnly.PDelivery, 10);

        var yieldOnly = MonteCarlo.Analyze(Make(lossy: true), runs: 60, seed: 21,
            sources: MonteCarlo.UncertaintySources.Yield);
        Assert.True(yieldOnly.AbortedSortiesMean > 0);
        Assert.True(yieldOnly.FinishP90 >= yieldOnly.FinishP10);
    }

    [Fact]
    public void Sensitivity_sweeps_parameters_and_sorts_by_swing()
    {
        var s = Scenario(horizonDays: 90, pilots: 2, engineers: 2, aircraft: 2,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
                Persistence = 0.5,
                PatternLength = 7,
            });
        s.MasterDeadline = Start.AddDays(60);
        var c = Camp(s, "c1", requiredSorties: 14);
        c.TestEfficiencyEnabled = true;
        c.RetestProbabilityStart = 0.4;
        c.RetestProbabilityFloor = 0.2;
        c.AbortProbability = 0.1;

        var r = MonteCarlo.Sensitivity(s, runs: 40, seed: 21, sobolSamples: 32);

        Assert.Equal(4, r.Factors.Count);
        Assert.Contains(r.Factors, f => f.Name.Contains("φ"));
        Assert.Contains(r.Factors, f => f.Name.Contains("Abort"));

        for (int i = 1; i < r.Factors.Count; i++)
            Assert.True(r.Factors[i - 1].Swing >= r.Factors[i].Swing);
        Assert.InRange(r.BaselinePDelivery, 0.0, 1.0);
        Assert.All(r.Factors, f =>
        {
            Assert.InRange(f.LowPDelivery, 0.0, 1.0);
            Assert.InRange(f.HighPDelivery, 0.0, 1.0);
        });

        Assert.Equal(new[] { 0.2, 0.5, 0.8, 0.95 }, r.PersistenceSweep.Select(p => p.Value));
        Assert.All(r.PersistenceSweep, p => Assert.InRange(p.PDelivery, 0.0, 1.0));

        Assert.Equal(4, r.Sweeps.Count);
        Assert.Same(r.Sweeps[0].Points, r.PersistenceSweep);
        Assert.Contains(r.Sweeps, sw => sw.Name.Contains("flyable rate"));
        Assert.Contains(r.Sweeps, sw => sw.Name.Contains("Abort"));
        Assert.All(r.Sweeps, sw =>
        {
            Assert.NotEmpty(sw.Points);
            Assert.All(sw.Points, p =>
            {
                Assert.InRange(p.PDelivery, 0.0, 1.0);
                Assert.False(string.IsNullOrEmpty(p.Label));
            });
        });
        Assert.Contains(r.Sweeps.First(sw => sw.Name.Contains("flyable rate")).Points, p => p.Label == "base");

        Assert.Equal(r.Sweeps.Count, r.GlobalImportance.Count);
        Assert.Equal(32 * (r.Sweeps.Count + 2), r.GlobalEvaluations);
        Assert.All(r.GlobalImportance, g =>
        {
            Assert.InRange(g.DirectShare, 0.0, 1.0);
            Assert.InRange(g.TotalShare, 0.0, 1.0);
            Assert.True(g.TotalShare >= g.DirectShare);
        });
    }

    [Fact]
    public void Sensitivity_omits_efficiency_factors_when_efficiency_is_off()
    {
        var s = Scenario(horizonDays: 60, pilots: 2, engineers: 2, aircraft: 2,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.6, 12).ToArray(),
                Persistence = 0.5,
                PatternLength = 7,
            });
        Camp(s, "c1", requiredSorties: 10);

        var r = MonteCarlo.Sensitivity(s, runs: 30, seed: 5, sobolSamples: 32);

        Assert.Equal(2, r.Factors.Count);
        Assert.All(r.Factors, f => Assert.Contains("Weather", f.Name));
    }

    [Fact]
    public void Sobol_estimator_recovers_the_Ishigami_importance_shares()
    {
        const double a = 7, b = 0.1;
        double F(double[] x) =>
            Math.Sin(x[0]) + a * Math.Sin(x[1]) * Math.Sin(x[1]) + b * Math.Pow(x[2], 4) * Math.Sin(x[0]);

        var ranges = new[] { (-Math.PI, Math.PI), (-Math.PI, Math.PI), (-Math.PI, Math.PI) };
        var (first, total, evals) = MonteCarlo.ComputeSobol(3, ranges, F, baseN: 16384);

        Assert.Equal(16384 * (3 + 2), evals);

        Assert.Equal(0.314, first[0], 1);
        Assert.Equal(0.442, first[1], 1);
        Assert.True(Math.Abs(first[2]) < 0.05, $"x3 has no direct effect; got {first[2]:0.###}");

        Assert.True(total[2] > 0.15, $"x3 total-order share should be ~0.24; got {total[2]:0.###}");
        Assert.True(total[0] > first[0], "x1's interactions make its total share exceed its direct share.");
    }

    [Fact]
    public void Recommender_can_propose_efficiency_improvements()
    {
        var s = Scenario(horizonDays: 40, pilots: 2, engineers: 2, aircraft: 2,
            flyable: new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.7, 12).ToArray(),
                Persistence = 0.3,
                PatternLength = 7,
            });
        s.MasterDeadline = Start.AddDays(12);
        var c = Camp(s, "c1", requiredSorties: 8, target: DateOnly.MaxValue);
        c.TestEfficiencyEnabled = true;
        c.RetestProbabilityStart = 0.5;
        c.RetestProbabilityFloor = 0.4;
        c.RetestLearningScale = 10;
        c.AbortProbability = 0.25;

        var result = ResourceSuggester.Recommend(
            s, ResourceSuggester.RecommenderLevers.ImproveRetest | ResourceSuggester.RecommenderLevers.ReduceAborts,
            runs: 40, targetConfidence: 0.95, maxSteps: 2, seed: 13);

        Assert.NotEmpty(result.Steps);
        Assert.All(result.Steps, st =>
            Assert.True(st.Addition.Contains("retest") || st.Addition.Contains("abort")));
    }

    [Fact]
    public void Campaign_without_a_milestone_reports_NoTarget()
    {
        var s = Scenario(horizonDays: 20, pilots: 2, engineers: 2, aircraft: 1);
        s.MasterDeadline = Start.AddDays(19);
        Camp(s, "c1", requiredSorties: 2, target: DateOnly.MaxValue);

        var sch = new Scheduler().Run(s);
        var report = ProgramReporter.Build(s, sch);

        Assert.Equal(MilestoneStatus.NoTarget, report.Campaigns.Single().Status);
        Assert.True(report.Delivered);
    }

    [Fact]
    public void No_periodic_rules_means_no_auto_maintenance()
    {
        var s = Scenario(horizonDays: 20, pilots: 2, engineers: 2, aircraft: 1);
        Camp(s, "c1", requiredSorties: 4);

        var sch = new Scheduler().Run(s);

        Assert.Equal(4, sch.Sorties.Count);
        Assert.Empty(sch.AutoMaintenance);
    }
}
