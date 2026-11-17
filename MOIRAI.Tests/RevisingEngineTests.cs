using System.Text.Json;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

public class RevisingEngineTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);
    private static readonly DateOnly End = new(2026, 8, 31);

    private static Scenario Build()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { Holidays = { new DateOnly(2026, 7, 20) } },
            FlyableModel = new RateBasedFlyableDays { Rate = 0.7, PatternLength = 5, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = End,
        };
        s.Pilots.AddRange(new[] { "P1", "P2", "P3" }.Select(id => new Pilot { Id = id, Name = id }));
        for (int i = 1; i <= 4; i++) s.Engineers.Add(new Engineer { Id = $"E{i}", Name = $"E{i}" });
        foreach (var t in new[] { "TC-01", "TC-02" }) s.Aircraft.Add(new Aircraft { Id = t, Name = t });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        s.Aircraft.Single(a => a.Id == "TC-02").MaintenanceWindows.Add(
            new DateRange { Start = new DateOnly(2026, 6, 10), End = new DateOnly(2026, 6, 25) });

        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A",
            TotalTestPoints = 24, TestPointsPerSortie = 4,
            CompatibleAircraftIds = { "TC-01" },
            AssignedPilotIds = { "P1", "P2" }, AssignedEngineerIds = { "E1", "E2" },
            StartDate = Start, TargetEndDate = new DateOnly(2026, 6, 30),
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B", Name = "B",
            TotalTestPoints = 18, TestPointsPerSortie = 3,
            CompatibleAircraftIds = { "TC-01", "TC-02" },
            AssignedPilotIds = { "P2", "P3" }, AssignedEngineerIds = { "E3", "E4" },
            PrerequisiteCampaignIds = { "A" },
            StartDate = Start, TargetEndDate = new DateOnly(2026, 7, 31),
            BlocksPerSortie = 3,
        });
        return s;
    }

    private static string Json(Schedule sch) => JsonSerializer.Serialize(sch, ScenarioJson.Options);

    private static void AssertFlightCapRespected(Scenario s, Schedule sch)
    {
        double cap = s.Config.MaxFlightHoursPer7Days;
        var fh = s.Campaigns.ToDictionary(c => c.Id, c => c.ExpectedSortieFlightHours);
        var perPersonDay = new Dictionary<string, Dictionary<int, double>>();
        foreach (var so in sch.Sorties)
        {
            double hours = fh.GetValueOrDefault(so.CampaignId);
            int day = so.Date.DayNumber;
            foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.EngineerIds).Concat(so.SupportPilotIds))
            {
                if (!perPersonDay.TryGetValue(pid, out var byDay)) perPersonDay[pid] = byDay = new();
                byDay[day] = byDay.GetValueOrDefault(day) + hours;
            }
        }
        foreach (var (pid, byDay) in perPersonDay)
            foreach (var endDay in byDay.Keys)
            {
                double sum = 0;
                for (int d = endDay - 6; d <= endDay; d++) sum += byDay.GetValueOrDefault(d);
                Assert.True(sum <= cap + 1e-9,
                    $"{pid} flies {sum}h in the 7-day window ending {DateOnly.FromDayNumber(endDay):yyyy-MM-dd} (cap {cap})");
            }
    }

    [Fact]
    public void Revising_off_is_byte_identical_to_greedy()
    {
        var s = Build();
        var greedy = new Scheduler().Run(s);
        var revising = new RevisingScheduler().Run(s);
        Assert.Equal(Json(greedy), Json(revising));
    }

    [Fact]
    public void Revising_off_is_byte_identical_to_greedy_on_a_probabilistic_model()
    {
        var s = Build();
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.7, 0.5, Start);
        var greedy = new Scheduler().Run(s);
        var revising = new RevisingScheduler().Run(s);
        Assert.Equal(Json(greedy), Json(revising));
    }

    [Fact]
    public void Core_engine_matches_greedy_on_a_feature_rich_core_scenario()
    {
        var s = Build();
        s.Pilots.First(p => p.Id == "P1").TypeRatings.Add("TC-01");
        s.Campaigns.First(c => c.Id == "A").AllowedBlocks.Add(1);
        var b = s.Campaigns.First(c => c.Id == "B");
        b.PreferSingleAircraft = true;
        b.MinWorkdaysBetweenSorties = 2;
        b.MaxSortiesPerDay = 1;

        Assert.True(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));
        Assert.Equal(Json(new Scheduler().Run(s)), Json(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Falls_back_to_greedy_for_unsupported_features()
    {
        var s = Build();
        s.Config.SimulateYieldInNominal = true;

        Assert.False(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));
        Assert.Equal(Json(new Scheduler().Run(s)), Json(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Revising_does_not_mutate_the_input_scenario()
    {
        var s = Build();
        var before = JsonSerializer.Serialize(s, ScenarioJson.Options);
        _ = new RevisingScheduler().Run(s);
        Assert.Equal(before, JsonSerializer.Serialize(s, ScenarioJson.Options));
    }

    private static MOIRAI.Scheduling.Revising.PlacementState NewState(out List<DateOnly> days)
    {
        var people = new List<Resource> { new Pilot { Id = "P1", Name = "P1" }, new Engineer { Id = "E1", Name = "E1" } };
        var aircraft = new List<Aircraft> { new() { Id = "TC-01", Name = "TC-01" }, new() { Id = "TC-02", Name = "TC-02" } };
        days = Enumerable.Range(0, 40).Select(i => Start.AddDays(i)).ToList();
        var cfg = new ScenarioConfig();
        return new MOIRAI.Scheduling.Revising.PlacementState(people, aircraft, days, cfg.BlocksPerDay, cfg);
    }

    private static MOIRAI.Scheduling.Revising.SortieMove Move(int flightDay, int tail = 0, int bps = 1) => new()
    {
        Crew = new[] { new PersonIndex(0), new PersonIndex(1) },
        Tail = new TailIndex(tail),
        FlightDay = flightDay, FlightBlock = 1, Bps = bps,
        RehearsalDay = flightDay - 2, RehearsalBlock = 2,
        FlightHours = 2, DutyHours = 5, FlightDutyHours = 0, RehearsalHours = 2,
    };

    [Fact]
    public void Local_repair_flies_an_alternative_tail_when_the_first_tails_crew_is_unavailable()
    {
        var day0 = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar(),
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = day0 },
            HorizonStart = day0, HorizonEnd = day0,
        };
        s.Pilots.Add(new Pilot { Id = "P1", Name = "P1", TypeRatings = { "T1" },
            LeaveWindows = { new DateRange { Start = day0, End = day0 } } });
        s.Pilots.Add(new Pilot { Id = "P2", Name = "P2", TypeRatings = { "T2" } });
        s.Engineers.Add(new Engineer { Id = "E1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 4, TestPointsPerSortie = 4,
            CompatibleAircraftIds = { "T1", "T2" }, AssignedPilotIds = { "P1", "P2" },
            AssignedEngineerIds = { "E1" }, EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = day0, TargetEndDate = day0,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.CampaignCompleted["A"]);
        Assert.Empty(greedy.Sorties);

        var revised = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(s);
        Assert.True(revised.CampaignCompleted["A"]);
        var sortie = Assert.Single(revised.Sorties);
        Assert.Equal("T2", sortie.AircraftId);
        Assert.Equal("P2", sortie.PilotId);
    }

    [Fact]
    public void Cross_day_repair_relocates_a_lower_priority_sortie_to_unblock_an_urgent_campaign()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(12),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft { Id = "TL", Name = "TL" });

        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(3), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("H", hTarget));

        var revised = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(s);
        Assert.True(revised.MeetsDeadline("H", hTarget));
        AssertFlightCapRespected(s, revised);
    }

    [Fact]
    public void Cross_day_repair_can_relocate_a_campaign_that_has_a_prerequisite()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(6);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(13),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Pilots.Add(new Pilot { Id = "PR", Name = "PR" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Engineers.Add(new Engineer { Id = "EPRE", Name = "EPRE" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft { Id = "TL", Name = "TL" });
        s.Aircraft.Add(new Aircraft { Id = "TP", Name = "TP" });

        s.Campaigns.Add(new Campaign
        {
            Id = "PRE", Name = "PRE", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TP" }, AssignedPilotIds = { "PR" }, AssignedEngineerIds = { "EPRE" },
            EngineersPerSortie = 1, RehearsalRequired = false, StartDate = start, TargetEndDate = start.AddDays(30),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            PrerequisiteCampaignIds = { "PRE" },
            StartDate = start, TargetEndDate = start.AddDays(30),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(4), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("H", hTarget));

        var revised = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(s);
        Assert.True(revised.MeetsDeadline("H", hTarget));
        AssertFlightCapRespected(s, revised);
    }

    [Fact]
    public void Cross_day_repair_relocates_a_paced_campaign_and_keeps_its_gap()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(6);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 4 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(13),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft { Id = "TL", Name = "TL" });

        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false, MinWorkdaysBetweenSorties = 2,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(4), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("H", hTarget));

        var revised = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(s);
        Assert.True(revised.MeetsDeadline("H", hTarget));
        AssertFlightCapRespected(s, revised);

        var lDays = revised.Sorties.Where(x => x.CampaignId == "L")
            .Select(x => x.Date.DayNumber).Distinct().OrderBy(d => d).ToList();
        for (int j = 1; j < lDays.Count; j++)
            Assert.True(lDays[j] - lDays[j - 1] >= 3, "L pacing gap must be respected after relocation");
    }

    [Fact]
    public void Cross_day_repair_relocates_a_daily_capped_campaign_within_its_cap()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(13),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft { Id = "TL", Name = "TL" });

        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false, MaxSortiesPerDay = 1,
            StartDate = start, TargetEndDate = start.AddDays(30),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(3), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("H", hTarget));

        var revised = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(s);
        Assert.True(revised.MeetsDeadline("H", hTarget));
        AssertFlightCapRespected(s, revised);

        bool overCap = revised.Sorties.Where(x => x.CampaignId == "L")
            .GroupBy(x => x.Date).Any(g => g.Count() > 1);
        Assert.False(overCap, "L's daily sortie cap must be respected after relocation");
    }

    [Fact]
    public void Tightness_order_prioritises_a_campaign_whose_window_is_squeezed_by_leave()
    {
        var start = new DateOnly(2026, 6, 1);
        var aTarget = start.AddDays(20);
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(24),
        };
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Pilots.Add(new Pilot { Id = "PA", Name = "PA",
            LeaveWindows = { new DateRange { Start = start.AddDays(4), End = start.AddDays(20) } } });
        s.Pilots.Add(new Pilot { Id = "PB", Name = "PB" });
        s.Engineers.Add(new Engineer { Id = "EA", Name = "EA" });
        s.Engineers.Add(new Engineer { Id = "EB", Name = "EB" });

        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "PA" }, AssignedEngineerIds = { "EA" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = aTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "PB" }, AssignedEngineerIds = { "EB" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start.AddDays(10),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("A", aTarget));

        var revised = new RevisingScheduler(new RevisingOptions { EnableTightnessOrder = true }).Run(s);
        Assert.True(revised.MeetsDeadline("A", aTarget));
        Assert.True(revised.MeetsDeadline("B", start.AddDays(10)));
    }

    [Fact]
    public void Scarcity_crew_choice_keeps_a_generalist_free_for_the_campaign_that_needs_it()
    {
        var start = new DateOnly(2026, 6, 1);
        var deadline = start.AddDays(1);
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(2),
        };
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Pilots.Add(new Pilot { Id = "P1", Name = "P1", TypeRatings = { "T1" } });
        s.Pilots.Add(new Pilot { Id = "P2", Name = "P2", TypeRatings = { "T1", "T2" } });
        s.Engineers.Add(new Engineer { Id = "EA", Name = "EA" });
        s.Engineers.Add(new Engineer { Id = "EB", Name = "EB" });
        s.Engineers.Add(new Engineer { Id = "EZ", Name = "EZ" });

        s.Campaigns.Add(new Campaign
        {
            Id = "LOADER", Name = "Loader", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "EZ" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start, TargetEndDate = start,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "P1", "P2" }, AssignedEngineerIds = { "EA" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(1), TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T2" }, AssignedPilotIds = { "P2" }, AssignedEngineerIds = { "EB" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(1), TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("B", deadline));

        var revised = new RevisingScheduler(new RevisingOptions { EnableScarcityCrew = true }).Run(s);
        Assert.True(revised.MeetsDeadline("A", deadline));
        Assert.True(revised.MeetsDeadline("B", deadline));
    }

    [Fact]
    public void Bottleneck_steering_leaves_a_contended_tail_for_the_campaign_with_no_alternative()
    {
        var start = new DateOnly(2026, 6, 1);
        var deadline = start;
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(2),
        };
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Pilots.Add(new Pilot { Id = "PF", Name = "PF" });
        s.Pilots.Add(new Pilot { Id = "PL", Name = "PL" });
        s.Engineers.Add(new Engineer { Id = "EF", Name = "EF" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });

        s.Campaigns.Add(new Campaign
        {
            Id = "FLEX", Name = "Flexible", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1", "T2" }, AssignedPilotIds = { "PF" }, AssignedEngineerIds = { "EF" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "LOCKED", Name = "Locked to T1", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "PL" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("LOCKED", deadline));

        var revised = new RevisingScheduler(new RevisingOptions { EnableBottleneckSteer = true }).Run(s);
        Assert.True(revised.MeetsDeadline("LOCKED", deadline));
        Assert.True(revised.MeetsDeadline("FLEX", deadline));
    }

    [Fact]
    public void Local_repair_off_still_matches_greedy_byte_for_byte()
    {
        var s = Build();
        var greedy = new Scheduler().Run(s);
        var revisedOff = new RevisingScheduler(RevisingOptions.Off).Run(s);
        Assert.Equal(Json(greedy), Json(revisedOff));
    }

    [Fact]
    public void Placement_journal_undo_restores_the_pre_placement_state()
    {
        var state = NewState(out _);
        var journal = new MOIRAI.Scheduling.Revising.PlacementJournal(state);
        var p0 = new PersonIndex(0);
        var tail = new TailIndex(0);

        journal.Commit(Move(flightDay: 10));
        Assert.Equal(1, journal.Count);
        Assert.Equal(1, state.Fleet.Use(tail));
        Assert.True(state.Crew.IsBlockBusy(p0, 0, 10));
        Assert.True(state.Crew.IsBlockBusy(p0, 1, 8));
        Assert.False(state.DayPlanAt(10).SpanFree(0, 1, 1));
        Assert.True(state.Crew.RecentDuty(p0, 10) > 0);

        journal.UndoLast();
        Assert.Equal(0, journal.Count);
        Assert.Equal(0, state.Fleet.Use(tail));
        Assert.False(state.Crew.IsBlockBusy(p0, 0, 10));
        Assert.False(state.Crew.IsBlockBusy(p0, 1, 8));
        Assert.True(state.DayPlanAt(10).SpanFree(0, 1, 1));
        Assert.Equal(0, state.Crew.RecentDuty(p0, 10));
        Assert.Equal(0, state.Crew.RecentDuty(p0, 8));
    }

    [Fact]
    public void Placement_journal_rolls_back_to_a_checkpoint()
    {
        var state = NewState(out _);
        var journal = new MOIRAI.Scheduling.Revising.PlacementJournal(state);
        var tail = new TailIndex(0);

        journal.Commit(Move(flightDay: 10));
        int checkpoint = journal.Count;
        journal.Commit(Move(flightDay: 15));
        journal.Commit(Move(flightDay: 20));
        Assert.Equal(3, journal.Count);
        Assert.Equal(3, state.Fleet.Use(tail));

        journal.UndoTo(checkpoint);
        Assert.Equal(1, journal.Count);
        Assert.Equal(1, state.Fleet.Use(tail));
        Assert.False(state.DayPlanAt(10).SpanFree(0, 1, 1));
        Assert.True(state.DayPlanAt(15).SpanFree(0, 1, 1));
        Assert.True(state.DayPlanAt(20).SpanFree(0, 1, 1));
    }

    private static (int Incomplete, int Missed, int Lateness, int Makespan) ScoreOf(Scenario s, Schedule sch)
    {
        int incomplete = 0, missed = 0, lateness = 0;
        foreach (var c in s.Campaigns)
        {
            if (!sch.CampaignCompleted.GetValueOrDefault(c.Id)) { incomplete++; continue; }
            DateOnly? dl = c.TargetEndDate != DateOnly.MaxValue ? c.TargetEndDate : s.MasterDeadline;
            if (dl is { } d && sch.CampaignEndDates.TryGetValue(c.Id, out var end) && end is { } e && e > d)
            {
                missed++; lateness += e.DayNumber - d.DayNumber;
            }
        }
        int makespan = sch.ProgramFinish is { } pf ? Math.Max(0, pf.DayNumber - s.HorizonStart.DayNumber) : 0;
        return (incomplete, missed, lateness, makespan);
    }

    private static bool BetterOrEqual((int, int, int, int) a, (int, int, int, int) b) =>
        Comparer<(int, int, int, int)>.Default.Compare(a, b) <= 0;

    private static Scenario BuildContendedTail()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(2),
        };
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "T2", Name = "T2" });
        s.Pilots.Add(new Pilot { Id = "PF", Name = "PF" });
        s.Pilots.Add(new Pilot { Id = "PL", Name = "PL" });
        s.Engineers.Add(new Engineer { Id = "EF", Name = "EF" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Campaigns.Add(new Campaign
        {
            Id = "FLEX", Name = "Flexible", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1", "T2" }, AssignedPilotIds = { "PF" }, AssignedEngineerIds = { "EF" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "LOCKED", Name = "Locked to T1", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "T1" }, AssignedPilotIds = { "PL" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        return s;
    }

    [Fact]
    public void Global_search_off_is_byte_identical_to_greedy()
    {
        var s = Build();
        var greedy = new Scheduler().Run(s);
        var revising = new RevisingScheduler(new RevisingOptions { EnableGlobalSearch = false }).Run(s);
        Assert.Equal(Json(greedy), Json(revising));
    }

    [Fact]
    public void Global_search_reorders_the_processing_order_to_unblock_a_contended_tail()
    {
        var s = BuildContendedTail();
        var deadline = s.HorizonStart;

        var greedy = new Scheduler().Run(s);
        Assert.False(greedy.MeetsDeadline("LOCKED", deadline));

        var revised = new RevisingScheduler(new RevisingOptions { EnableGlobalSearch = true }).Run(s);
        Assert.True(revised.MeetsDeadline("LOCKED", deadline));
        Assert.True(revised.MeetsDeadline("FLEX", deadline));
    }

    [Fact]
    public void Global_search_never_regresses_below_the_greedy_baseline()
    {
        var s = Build();
        var greedy = new Scheduler().Run(s);
        var revised = new RevisingScheduler(new RevisingOptions { EnableGlobalSearch = true }).Run(s);
        Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
    }

    [Fact]
    public void Global_search_is_deterministic()
    {
        var s = BuildContendedTail();
        var opt = new RevisingOptions { EnableGlobalSearch = true };
        Assert.Equal(Json(new RevisingScheduler(opt).Run(s)), Json(new RevisingScheduler(opt).Run(s)));
    }

    [Fact]
    public void Global_search_with_progress_matches_the_plain_run()
    {
        var s = BuildContendedTail();
        var opt = new RevisingOptions { EnableGlobalSearch = true };
        var plain = new RevisingScheduler(opt).Run(s);
        var withProgress = new RevisingScheduler(opt).Run(s, new Progress<double>(_ => { }), CancellationToken.None);
        Assert.Equal(Json(plain), Json(withProgress));
    }

    [Fact]
    public void Global_search_honours_cancellation()
    {
        var s = BuildContendedTail();
        var opt = new RevisingOptions { EnableGlobalSearch = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new RevisingScheduler(opt).Run(s, null, cts.Token));
    }

    private static Scenario BuildWithPeriodic()
    {
        var s = Build();
        s.Aircraft.Single(a => a.Id == "TC-01").PeriodicMaintenances.Add(
            new PeriodicMaintenanceRule { IntervalFlightHours = 5, DurationDays = 2 });
        return s;
    }

    [Fact]
    public void Periodic_maintenance_is_now_handled_and_off_matches_greedy()
    {
        var s = BuildWithPeriodic();
        Assert.True(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));

        var greedy = new Scheduler().Run(s);
        Assert.NotEmpty(greedy.AutoMaintenance);

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    private static string JsonNoWindowIds(Schedule sch)
    {
        foreach (var list in sch.AutoMaintenance.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.UnscheduledRepairs.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.SimulatorRepairs.Values) foreach (var w in list) w.Id = "";
        return Json(sch);
    }

    private static void AssertNoSortieInMaintenanceWindow(Schedule sch)
    {
        bool InWindow(string tailId, DateOnly d) =>
            sch.AutoMaintenance.TryGetValue(tailId, out var ws) && ws.Any(w => d >= w.Start && d <= w.End);
        foreach (var so in sch.Sorties)
        {
            Assert.False(InWindow(so.AircraftId, so.Date),
                $"sortie on {so.AircraftId} {so.Date:yyyy-MM-dd} lands inside a maintenance window");
            foreach (var sa in so.SupportAircraftIds)
                Assert.False(InWindow(sa, so.Date), $"support sortie on {sa} {so.Date:yyyy-MM-dd} inside a window");
        }
    }

    [Fact]
    public void Cross_day_repair_runs_for_a_periodic_scenario_and_stays_maintenance_legal()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(12),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        var th = new Aircraft { Id = "TH", Name = "TH" };
        th.PeriodicMaintenances.Add(new PeriodicMaintenanceRule { IntervalFlightHours = 1.5, DurationDays = 2 });
        s.Aircraft.Add(th);
        var tl = new Aircraft { Id = "TL", Name = "TL" };
        tl.PeriodicMaintenances.Add(new PeriodicMaintenanceRule { IntervalFlightHours = 100, DurationDays = 1 });
        s.Aircraft.Add(tl);

        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(3), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        Assert.False(new Scheduler().Run(s).MeetsDeadline("H", hTarget));

        var revised = new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true }).Run(s);
        Assert.True(revised.MeetsDeadline("H", hTarget));
        Assert.NotEmpty(revised.AutoMaintenance);
        AssertFlightCapRespected(s, revised);
        AssertNoSortieInMaintenanceWindow(revised);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MOIRAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public void Advanced_mc_repairs_a_blocked_campaign_when_no_failures_are_modelled()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6, McUseAdvancedEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0.0, start),
            HorizonStart = start, HorizonEnd = start.AddDays(12),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft { Id = "TL", Name = "TL" });
        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(3), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var mc = MonteCarlo.Analyze(s, runs: 8, seed: 1);
        Assert.Equal(1.0, mc.Campaigns.Single(c => c.CampaignId == "H").PMeet, 6);
    }

    [Fact]
    public void Advanced_mc_repairs_with_a_failure_rate_fleet()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6, McUseAdvancedEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0.0, start),
            HorizonStart = start, HorizonEnd = start.AddDays(12),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft
        {
            Id = "TL", Name = "TL",
            FailuresPer100FlightHours = 20, RepairDaysMin = 1, RepairDaysMax = 1,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(3), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var mc = MonteCarlo.Analyze(s, runs: 50, seed: 1);
        double pH = mc.Campaigns.Single(c => c.CampaignId == "H").PMeet;
        Assert.True(pH > 0.5, $"H should meet in most realizations once repair runs; got {pH:P0}");
    }

    [Fact]
    public void Advanced_mc_repairs_with_a_failure_rate_simulator()
    {
        var start = new DateOnly(2026, 6, 1);
        var hTarget = start.AddDays(5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6, McUseAdvancedEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0.0, start),
            HorizonStart = start, HorizonEnd = start.AddDays(12),
        };
        s.Pilots.Add(new Pilot { Id = "P", Name = "P" });
        s.Engineers.Add(new Engineer { Id = "EH", Name = "EH" });
        s.Engineers.Add(new Engineer { Id = "EL", Name = "EL" });
        s.Aircraft.Add(new Aircraft { Id = "TH", Name = "TH" });
        s.Aircraft.Add(new Aircraft { Id = "TL", Name = "TL" });
        s.Simulators.Add(new Simulator
        {
            Id = "SIM", Name = "SIM",
            FailuresPer100Hours = 20, RepairDaysMin = 1, RepairDaysMax = 1,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "L", Name = "L", TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TL" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = true,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4, ExpectedRehearsalDutyHours = 2,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "H", Name = "H", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TH" }, AssignedPilotIds = { "P" }, AssignedEngineerIds = { "EH" },
            EngineersPerSortie = 1, RehearsalRequired = true,
            StartDate = start.AddDays(3), TargetEndDate = hTarget,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4, ExpectedRehearsalDutyHours = 2,
        });

        var mc = MonteCarlo.Analyze(s, runs: 50, seed: 1);
        double pH = mc.Campaigns.Single(c => c.CampaignId == "H").PMeet;
        Assert.True(pH > 0.5, $"H should meet in most realizations once repair runs with a failing sim; got {pH:P0}");
    }

    [Fact]
    public void Advanced_mc_reproduces_the_committed_plan_under_no_weather_uncertainty()
    {
        var path = Path.Combine(FindRepoRoot(), "samples", "revising-large-demo.json");
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path), ScenarioJson.Options)!;
        var s = session.Scenarios[0];
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(1.0, 0.0, s.HorizonStart);
        s.Config.McUseAdvancedEngine = true;

        var plan = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableScarcityCrew = true,
            EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s);
        foreach (var c in s.Campaigns)
            Assert.True(plan.MeetsDeadline(c.Id, c.TargetEndDate), $"committed plan should meet {c.Id}");

        var mc = MonteCarlo.Analyze(s, runs: 8, seed: 1);
        foreach (var cr in mc.Campaigns)
            Assert.Equal(1.0, cr.PMeet, 6);
    }

    [Fact]
    public void Global_search_plans_periodic_windows_and_never_regresses()
    {
        var s = BuildWithPeriodic();
        var greedy = new Scheduler().Run(s);
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s);

        Assert.NotEmpty(revised.AutoMaintenance);
        Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
    }

    private static Scenario BuildDeployment(bool rehearsals = false, bool hasSimulator = true)
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(59),
        };
        s.Locations.Add(new Location
        {
            Id = "rng", Name = "Test Range",
            FerryDays = 1, SetupDays = 2, TeardownDays = 1,
            FerryFlightHours = 2, FerryDutyHours = 6,
        });
        if (hasSimulator)
            s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = "rng" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "Tail-2" });
        s.Campaigns.Add(new Campaign
        {
            Id = "REMOTE", Name = "Range work", TotalTestPoints = 5, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(40), LocationId = "rng",
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            EngineersPerSortie = 1, RehearsalRequired = rehearsals,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "MAIN", Name = "Main base", TotalTestPoints = 5, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(40),
            CompatibleAircraftIds = { "TC-002" }, AssignedPilotIds = { "p2" }, AssignedEngineerIds = { "e2" },
            EngineersPerSortie = 1, RehearsalRequired = rehearsals,
        });
        return s;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Deployment_is_handled_in_core_and_off_matches_greedy(bool rehearsals, bool hasSim)
    {
        var s = BuildDeployment(rehearsals, hasSim);
        Assert.True(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));

        var greedy = new Scheduler().Run(s);
        Assert.Single(greedy.Deployments);

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Deployment_with_support_aircraft_off_matches_greedy()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(59),
        };
        s.Locations.Add(new Location
        {
            Id = "rng", Name = "Test Range",
            FerryDays = 1, SetupDays = 2, TeardownDays = 1,
            FerryFlightHours = 2, FerryDutyHours = 6,
        });
        s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = "rng" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Test article" });
        s.Aircraft.Add(new Aircraft { Id = "TC-CHASE", Name = "Chase" });
        s.Campaigns.Add(new Campaign
        {
            Id = "REMOTE", Name = "Range work", TotalTestPoints = 6, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(50), LocationId = "rng",
            CompatibleAircraftIds = { "TC-001" },
            SupportAircraftPerSortie = 1, SupportAircraftIds = { "TC-CHASE" },
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e1" },
            EngineersPerSortie = 1, RehearsalRequired = false,
        });

        var greedy = new Scheduler().Run(s);
        Assert.Single(greedy.Deployments);
        Assert.True(greedy.CampaignCompleted["REMOTE"]);
        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Deployment_waits_for_a_prerequisite_campaign_before_departing()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(89),
        };
        s.Locations.Add(new Location
        {
            Id = "rng", Name = "Range",
            FerryDays = 1, SetupDays = 2, TeardownDays = 1, FerryFlightHours = 2, FerryDutyHours = 6,
        });
        s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = "rng" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Engineers.Add(new Engineer { Id = "e2", Name = "E2" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Main tail" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "Remote tail" });
        s.Campaigns.Add(new Campaign
        {
            Id = "PRE", Name = "Prerequisite", TotalTestPoints = 20, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(80),
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            EngineersPerSortie = 1, RehearsalRequired = false,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "REM", Name = "Range work", TotalTestPoints = 4, TestPointsPerSortie = 1, LocationId = "rng",
            StartDate = start, TargetEndDate = start.AddDays(80), PrerequisiteCampaignIds = { "PRE" },
            CompatibleAircraftIds = { "TC-002" }, AssignedPilotIds = { "p2" }, AssignedEngineerIds = { "e2" },
            EngineersPerSortie = 1, RehearsalRequired = false,
        });

        var greedy = new Scheduler().Run(s);
        var preEnd = greedy.CampaignEndDates["PRE"]!.Value;
        var dep = Assert.Single(greedy.Deployments);
        Assert.True(dep.FlyFrom >= preEnd,
            $"deployment fly-from {dep.FlyFrom} should be >= prerequisite completion {preEnd}");
        Assert.True(dep.FlyFrom > start.AddDays(7), $"deployment should be held well past the start; flew {dep.FlyFrom}");
        Assert.True(greedy.CampaignCompleted["REM"]);

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Pre_deployment_rehearsals_pack_against_departure_and_off_matches_greedy()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(89),
        };
        s.Locations.Add(new Location
        {
            Id = "rng", Name = "Range",
            FerryDays = 1, SetupDays = 2, TeardownDays = 1, FerryFlightHours = 2, FerryDutyHours = 6,
        });
        s.Simulators.Add(new Simulator { Id = "SIM-rng", Name = "Range sim", LocationId = "rng" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Engineers.Add(new Engineer { Id = "e2", Name = "E2" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Main tail" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "Remote tail" });
        s.Campaigns.Add(new Campaign
        {
            Id = "PRE", Name = "Prerequisite", TotalTestPoints = 20, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(80),
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            EngineersPerSortie = 1, RehearsalRequired = true,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "REM", Name = "Range work", TotalTestPoints = 4, TestPointsPerSortie = 1, LocationId = "rng",
            StartDate = start, TargetEndDate = start.AddDays(80), PrerequisiteCampaignIds = { "PRE" },
            CompatibleAircraftIds = { "TC-002" }, AssignedPilotIds = { "p2" }, AssignedEngineerIds = { "e2" },
            EngineersPerSortie = 1, RehearsalRequired = true,
        });

        var greedy = new Scheduler().Run(s);
        var dep = Assert.Single(greedy.Deployments);
        foreach (var r in greedy.Rehearsals.Where(r => r.CampaignId == "REM"))
            Assert.True(r.Date >= dep.Departure.AddDays(-14),
                $"pre-deployment rehearsal {r.Date} stranded far before departure {dep.Departure}");

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Deployment_global_search_preserves_remote_semantics_and_never_regresses()
    {
        var s = BuildDeployment();
        var greedy = new Scheduler().Run(s);
        var advanced = new RevisingScheduler(new RevisingOptions { EnableGlobalSearch = true })
            .Run(s, null, CancellationToken.None);

        Assert.True(advanced.CampaignCompleted["REMOTE"]);
        Assert.Single(advanced.Deployments);
        Assert.True(advanced.Sorties.Where(x => x.CampaignId == "REMOTE").Min(x => x.Date)
                    >= s.HorizonStart.AddDays(3));
        Assert.True(BetterOrEqual(ScoreOf(s, advanced), ScoreOf(s, greedy)));
    }

    [Fact]
    public void Deployment_with_repair_on_stays_valid_and_never_regresses()
    {
        var s = BuildDeployment();
        var greedy = new Scheduler().Run(s);
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s, null, CancellationToken.None);

        Assert.True(revised.CampaignCompleted["REMOTE"]);
        Assert.Single(revised.Deployments);
        Assert.True(revised.Sorties.Where(x => x.CampaignId == "REMOTE").Min(x => x.Date)
                    >= s.HorizonStart.AddDays(3));
        AssertFlightCapRespected(s, revised);
        Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
    }

    [Fact]
    public void Deployment_search_is_deterministic()
    {
        var s = BuildDeployment();
        var opt = new RevisingOptions { EnableGlobalSearch = true };
        var r1 = new RevisingScheduler(opt).Run(s, null, CancellationToken.None);
        var r2 = new RevisingScheduler(opt).Run(s, null, CancellationToken.None);
        Assert.Equal(r1.Sorties.Count, r2.Sorties.Count);
        Assert.Equal(r1.ProgramFinish, r2.ProgramFinish);
        Assert.Equal(ScoreOf(s, r1), ScoreOf(s, r2));
    }

    private static Scenario BuildWithSimulator(int maxBlocksPerDay)
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(40),
        };
        s.Simulators.Add(new Simulator { Id = "SIM1", Name = "Sim 1", MaxBlocksPerDay = maxBlocksPerDay });
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "T1" });
        s.Aircraft.Add(new Aircraft { Id = "TC-002", Name = "T2" });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 6, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(30), CompatibleAircraftIds = { "TC-001" },
            AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" }, EngineersPerSortie = 1, RehearsalRequired = true,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 6, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(30), CompatibleAircraftIds = { "TC-002" },
            AssignedPilotIds = { "p2" }, AssignedEngineerIds = { "e2" }, EngineersPerSortie = 1, RehearsalRequired = true,
        });
        return s;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Simulators_are_handled_in_core_and_off_matches_greedy(int maxBlocks)
    {
        var s = BuildWithSimulator(maxBlocks);
        Assert.True(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));

        var greedy = new Scheduler().Run(s);
        Assert.NotEmpty(greedy.Rehearsals);

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Simulator_global_search_never_regresses()
    {
        var s = BuildWithSimulator(1);
        var greedy = new Scheduler().Run(s);
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s, null, CancellationToken.None);
        Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
    }

    [Fact]
    public void Monte_carlo_runs_on_the_advanced_engine_and_is_reproducible()
    {
        var s = Build();
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.6, 0.5, Start);
        s.Config.McUseAdvancedEngine = true;

        var r1 = MonteCarlo.Analyze(s, runs: 60, seed: 7);
        var r2 = MonteCarlo.Analyze(s, runs: 60, seed: 7);

        Assert.Equal(s.Campaigns.Count, r1.Campaigns.Count);
        Assert.Equal(r1.PDelivery, r2.PDelivery);
        Assert.Equal(r1.Campaigns[0].PMeet, r2.Campaigns[0].PMeet);
    }

    [Fact]
    public void Monte_carlo_greedy_and_advanced_engines_both_produce_valid_profiles()
    {
        var s = Build();
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.6, 0.5, Start);

        s.Config.McUseAdvancedEngine = false;
        var greedy = MonteCarlo.Analyze(s, runs: 60, seed: 7);
        s.Config.McUseAdvancedEngine = true;
        var advanced = MonteCarlo.Analyze(s, runs: 60, seed: 7);

        foreach (var r in new[] { greedy, advanced })
        {
            Assert.Equal(s.Campaigns.Count, r.Campaigns.Count);
            Assert.InRange(r.PDelivery, 0.0, 1.0);
            Assert.All(r.Campaigns, c => Assert.InRange(c.PMeet, 0.0, 1.0));
        }
    }

    private static Scenario BuildFormation()
    {
        var start = new DateOnly(2026, 6, 1);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start, HorizonEnd = start.AddDays(30),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        s.Pilots.Add(new Pilot { Id = "p3", Name = "P3" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Engineers.Add(new Engineer { Id = "e2", Name = "E2" });
        s.Aircraft.Add(new Aircraft { Id = "TEST", Name = "Test article" });
        s.Aircraft.Add(new Aircraft { Id = "CHASE", Name = "Chase" });
        s.Aircraft.Add(new Aircraft { Id = "OTHER", Name = "Other" });
        s.Campaigns.Add(new Campaign
        {
            Id = "FORM", Name = "Formation", TotalTestPoints = 6, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(20),
            CompatibleAircraftIds = { "TEST" }, SupportAircraftPerSortie = 1, SupportAircraftIds = { "CHASE" },
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e1" }, EngineersPerSortie = 1, RehearsalRequired = false,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "OTHER", Name = "Other", TotalTestPoints = 6, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(20),
            CompatibleAircraftIds = { "OTHER" }, AssignedPilotIds = { "p3" }, AssignedEngineerIds = { "e2" },
            EngineersPerSortie = 1, RehearsalRequired = false,
        });
        return s;
    }

    [Fact]
    public void Multi_ship_is_handled_in_core_and_off_matches_greedy()
    {
        var s = BuildFormation();
        Assert.True(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));

        var greedy = new Scheduler().Run(s);
        Assert.NotEmpty(greedy.Sorties.SelectMany(x => x.SupportAircraftIds));

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Multi_ship_global_search_never_regresses()
    {
        var s = BuildFormation();
        var greedy = new Scheduler().Run(s);
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s, null, CancellationToken.None);
        Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
        Assert.NotEmpty(revised.Sorties.SelectMany(x => x.SupportAircraftIds));
    }

    private static Scenario BuildWithActuals()
    {
        var s = Build();
        var baseline = new Scheduler().Run(s);
        var freeze = new DateOnly(2026, 6, 15);
        s.FreezeDate = freeze;
        foreach (var x in baseline.Sorties.Where(x => x.Date < freeze)) s.ActualSorties.Add(x);
        foreach (var x in baseline.Rehearsals.Where(x => x.Date < freeze)) s.ActualRehearsals.Add(x);
        return s;
    }

    [Fact]
    public void Execution_tracking_is_handled_in_core_and_off_matches_greedy()
    {
        var s = BuildWithActuals();
        Assert.True(RevisingScheduler.CanHandle(s, LosslessYieldModel.Instance, NoFailuresModel.Instance));

        var greedy = new Scheduler().Run(s);
        Assert.Contains(greedy.Sorties, x => x.IsActual);

        Assert.Equal(JsonNoWindowIds(greedy), JsonNoWindowIds(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Repair_runs_for_simulator_multiship_and_execution_tracking_without_breaking_FTOM()
    {
        var opt = new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        };
        foreach (var s in new[] { BuildWithSimulator(1), BuildFormation(), BuildWithActuals() })
        {
            var greedy = new Scheduler().Run(s);
            var revised = new RevisingScheduler(opt).Run(s, null, CancellationToken.None);
            AssertFlightCapRespected(s, revised);
            Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
            Assert.Equal(ScoreOf(s, revised),
                ScoreOf(s, new RevisingScheduler(opt).Run(s, null, CancellationToken.None)));
        }
    }

    [Fact]
    public void Execution_tracking_global_search_replans_only_the_future_and_never_regresses()
    {
        var s = BuildWithActuals();
        var greedy = new Scheduler().Run(s);
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(s, null, CancellationToken.None);

        Assert.Contains(revised.Sorties, x => x.IsActual);
        Assert.DoesNotContain(revised.Sorties, x => !x.IsActual && x.Date < s.FreezeDate!.Value);
        Assert.True(BetterOrEqual(ScoreOf(s, revised), ScoreOf(s, greedy)));
    }
}
