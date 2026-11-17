using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class OptimizerTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario Make(int targetDay, int sorties)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 2, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.97, 12).ToArray(),
                Persistence = 0.2, PatternLength = 7, Anchor = Start,
            },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(39),
            MasterDeadline = Start.AddDays(targetDay),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        s.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "FT-PERF", TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(targetDay), BlocksPerSortie = 3,
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1", "p2" },
            AssignedEngineerIds = { "e1", "e2", "e3" },
        });
        return s;
    }

    [Fact]
    public void Optimizer_shifts_blocking_maintenance_to_recover_a_campaign()
    {
        var s = Make(targetDay: 15, sorties: 8);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(10) });

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Maintenance,
            Iterations = 250, SearchRuns = 20, FinalRuns = 50, MaxShiftDays = 20,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved, $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}");
        Assert.True(result.OptimizedPDelivery > result.BaselinePDelivery + 0.3);
        Assert.Contains(result.Changes, c => c.Kind == "Maintenance");
    }

    [Fact]
    public void Optimizer_authorizes_extra_workdays_to_recover_a_weekend_bound_campaign()
    {
        var s = Make(targetDay: 13, sorties: 11);
        s.Calendar = new FlightCalendar();

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.ExtraWorkdays,
            Iterations = 250, SearchRuns = 20, FinalRuns = 50, MaxExtraWorkdays = 4,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved, $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}");
        var workdays = result.Changes.Where(c => c.Kind == "Workday").ToList();
        Assert.InRange(workdays.Count, 1, 4);
    }

    [Fact]
    public void Optimizer_widens_a_crew_pool_to_lift_a_pilot_bound_campaign()
    {
        var cfg = new ScenarioConfig
        {
            PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3, MaxSortiesPerPersonPerDay = 1,
        };
        var s = new Scenario
        {
            Config = cfg,
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Repeat(0.97, 12).ToArray(),
                Persistence = 0.2, PatternLength = 7, Anchor = Start,
            },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(19),
            MasterDeadline = Start.AddDays(6),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        s.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "FT-PERF", TotalTestPoints = 10, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(6),
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1" },
            AssignedEngineerIds = { "e1", "e2", "e3" },
        });

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.CrewAssignment,
            Iterations = 120, SearchRuns = 20, FinalRuns = 50,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved, $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}");
        Assert.Contains(result.Changes, c => c.Kind == "Crew" && c.Description.Contains("P2"));
    }

    [Fact]
    public void Extra_workday_overrides_weekend_and_holiday()
    {
        var cal = new FlightCalendar();
        var saturday = new DateOnly(2026, 6, 6);
        var holiday = new DateOnly(2026, 6, 10);
        cal.Holidays.Add(holiday);

        Assert.False(cal.IsWorkday(saturday));
        Assert.False(cal.IsWorkday(holiday));

        cal.ExtraWorkdays.Add(saturday);
        cal.ExtraWorkdays.Add(holiday);
        Assert.True(cal.IsWorkday(saturday));
        Assert.True(cal.IsWorkday(holiday));
    }

    [Fact]
    public void Campaign_gated_by_prerequisite_maintenance_opens_the_day_after_it_ends()
    {
        var s = Make(targetDay: 30, sorties: 4);
        var mod = new DateRange { Start = Start.AddDays(5), End = Start.AddDays(9), Note = "mod-block" };
        s.Aircraft[0].MaintenanceWindows.Add(mod);
        s.Campaigns[0].PrerequisiteMaintenanceIds.Add(mod.Id);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["c1"]);
        Assert.True(sch.Sorties.Min(x => x.Date) >= Start.AddDays(10),
            $"first sortie {sch.Sorties.Min(x => x.Date)} must be after the mod-block ends");
    }

    [Fact]
    public void Optimizer_reschedules_a_gating_mod_block_to_recover_the_campaign()
    {
        var s = Make(targetDay: 30, sorties: 15);
        var mod = new DateRange { Start = Start.AddDays(10), End = Start.AddDays(19), Note = "mod-block" };
        s.Aircraft[0].MaintenanceWindows.Add(mod);
        s.Campaigns[0].PrerequisiteMaintenanceIds.Add(mod.Id);

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Maintenance,
            Iterations = 250, SearchRuns = 20, FinalRuns = 50, MaxShiftDays = 12,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved, $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}");
        Assert.Contains(result.Changes, c => c.Kind == "Maintenance" && c.Description.Contains("earlier"));
    }

    [Fact]
    public void Maintenance_shift_is_clamped_to_the_freeze_date()
    {
        var s = Make(targetDay: 35, sorties: 15);
        s.HorizonEnd = Start.AddDays(44);
        var mod = new DateRange { Start = Start.AddDays(20), End = Start.AddDays(29), Note = "mod-block" };
        s.Aircraft[0].MaintenanceWindows.Add(mod);
        s.Campaigns[0].PrerequisiteMaintenanceIds.Add(mod.Id);
        s.FreezeDate = Start.AddDays(5);

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Maintenance,
            Iterations = 250, SearchRuns = 20, FinalRuns = 50, MaxShiftDays = 25,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.Contains(result.Changes, c => c.Kind == "Maintenance" && c.Description.Contains("earlier"));
        var win = result.Optimized.Aircraft[0].MaintenanceWindows[0];
        Assert.True(win.Start >= Start.AddDays(5),
            $"window start {win.Start} must not be dragged before the freeze {Start.AddDays(5)}");
    }

    [Fact]
    public void Joint_optimization_buys_an_aircraft_when_the_fleet_is_the_binding_constraint()
    {
        var s = Make(targetDay: 6, sorties: 12);

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Resources,
            Iterations = 150, SearchRuns = 20, FinalRuns = 50,
            MaxAddedPilots = 2, MaxAddedEngineers = 2, MaxAddedAircraft = 2,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved, $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}");
        Assert.Contains(result.Changes, c => c.Kind == "Resource" && c.Description.Contains("aircraft"));

        Assert.True(result.Frontier.Count >= 2);
        Assert.Equal(0, result.Frontier[0].Additions);
        Assert.True(result.Frontier[^1].PDelivery > result.Frontier[0].PDelivery + 0.3);
    }

    [Fact]
    public void Resource_levers_are_independent_per_type()
    {
        var s = Make(targetDay: 6, sorties: 12);
        s.Engineers.Add(new Engineer { Id = "e4", Name = "E4" });
        s.Engineers.Add(new Engineer { Id = "e5", Name = "E5" });
        s.Campaigns[0].AssignedEngineerIds.Add("e4");
        s.Campaigns[0].AssignedEngineerIds.Add("e5");

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.AddAircraft,
            Iterations = 150, SearchRuns = 20, FinalRuns = 50,
            MaxAddedPilots = 2, MaxAddedEngineers = 2, MaxAddedAircraft = 2,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved,
            $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}; " +
            $"changes: {string.Join("; ", result.Changes.Select(c => c.Kind + ":" + c.Description))}");
        var resource = Assert.Single(result.Changes, c => c.Kind == "Resource");
        Assert.Contains("aircraft", resource.Description);
        Assert.DoesNotContain("pilot", resource.Description);
        Assert.DoesNotContain("engineer", resource.Description);
    }

    [Fact]
    public void Genetic_search_also_recovers_the_maintenance_blocked_campaign()
    {
        var s = Make(targetDay: 15, sorties: 8);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(10) });

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Maintenance,
            Search = Optimizer.Method.Genetic,
            Iterations = 240, PopulationSize = 12,
            SearchRuns = 20, FinalRuns = 50, MaxShiftDays = 20,
        };
        var a = Optimizer.Optimize(s, opts);
        var b = Optimizer.Optimize(s, opts);

        Assert.True(a.Improved, $"baseline {a.BaselineScore:0.###} → optimized {a.OptimizedScore:0.###}");
        Assert.Contains(a.Changes, c => c.Kind == "Maintenance");
        Assert.Equal(a.OptimizedScore, b.OptimizedScore, 6);
    }

    [Fact]
    public void Each_change_applies_individually_to_a_scenario_copy()
    {
        var s = Make(targetDay: 15, sorties: 8);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(10) });

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Maintenance,
            Iterations = 250, SearchRuns = 20, FinalRuns = 50, MaxShiftDays = 20,
        };
        var result = Optimizer.Optimize(s, opts);
        Assert.True(result.Improved);

        var copy = MOIRAI.Persistence.ScenarioJson.Clone(s);
        foreach (var c in result.Changes) c.Apply(copy);

        var optWin = result.Optimized.Aircraft[0].MaintenanceWindows[0];
        var copyWin = copy.Aircraft[0].MaintenanceWindows[0];
        Assert.Equal(optWin.Start, copyWin.Start);
        Assert.Equal(optWin.End, copyWin.End);

        Assert.Equal(Start, s.Aircraft[0].MaintenanceWindows[0].Start);
        Assert.Equal(Start.AddDays(10), s.Aircraft[0].MaintenanceWindows[0].End);

        var untouched = MOIRAI.Persistence.ScenarioJson.Clone(s);
        Assert.Equal(Start, untouched.Aircraft[0].MaintenanceWindows[0].Start);
    }

    [Fact]
    public void Genetic_search_respects_the_extra_workday_cap()
    {
        var s = Make(targetDay: 13, sorties: 14);
        s.Calendar = new FlightCalendar();

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.ExtraWorkdays,
            Search = Optimizer.Method.Genetic,
            Iterations = 320, PopulationSize = 16,
            SearchRuns = 20, FinalRuns = 50, MaxExtraWorkdays = 2,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Optimized.Calendar.ExtraWorkdays.Count <= 2,
            $"winner authorized {result.Optimized.Calendar.ExtraWorkdays.Count} extra workdays, cap is 2");
        Assert.True(result.Changes.Count(c => c.Kind == "Workday") <= 2);
    }

    [Fact]
    public void Extra_workday_lever_with_a_zero_cap_does_not_crash()
    {
        var s = Make(targetDay: 13, sorties: 11);
        s.Calendar = new FlightCalendar();

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.ExtraWorkdays,
            Iterations = 250, SearchRuns = 20, FinalRuns = 50, MaxExtraWorkdays = 0,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.Empty(result.Optimized.Calendar.ExtraWorkdays);
        Assert.DoesNotContain(result.Changes, c => c.Kind == "Workday");
    }

    [Fact]
    public void Reordering_an_already_prioritized_scenario_reports_the_change_it_made()
    {
        var s = TwoCampaignSharedTail();
        s.Campaigns.First(c => c.Id == "early").PriorityKey = 1;
        s.Campaigns.First(c => c.Id == "late").PriorityKey = 0;

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.Ordering,
            Iterations = 200, SearchRuns = 20, FinalRuns = 60,
        };
        var result = Optimizer.Optimize(s, opts);

        Assert.True(result.Improved, $"baseline {result.BaselineScore:0.###} → optimized {result.OptimizedScore:0.###}");
        Assert.Contains(result.Changes, c => c.Kind == "Ordering");
    }

    private static Scenario TwoCampaignSharedTail()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start, HorizonEnd = Start.AddDays(25),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" });
        for (int i = 1; i <= 3; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "TC-001", Name = "Tail-1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        s.Campaigns.Add(new Campaign
        {
            Id = "early", Name = "Early", TotalTestPoints = 8, TestPointsPerSortie = 1, BlocksPerSortie = 3,
            StartDate = Start, TargetEndDate = Start.AddDays(10),
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1", "p2" },
            AssignedEngineerIds = { "e1", "e2", "e3" },
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "late", Name = "Late", TotalTestPoints = 8, TestPointsPerSortie = 1, BlocksPerSortie = 3,
            StartDate = Start, TargetEndDate = Start.AddDays(20),
            CompatibleAircraftIds = { "TC-001" }, AssignedPilotIds = { "p1", "p2" },
            AssignedEngineerIds = { "e1", "e2", "e3" },
        });
        return s;
    }

    [Fact]
    public void Optimizer_does_no_harm_and_is_reproducible_on_a_comfortable_plan()
    {
        var s = Make(targetDay: 39, sorties: 8);

        var opts = new Optimizer.Options
        {
            Levers = Optimizer.Levers.All,
            Iterations = 120, SearchRuns = 20, FinalRuns = 50, MaxShiftDays = 14,
        };
        var a = Optimizer.Optimize(s, opts);
        var b = Optimizer.Optimize(s, opts);

        Assert.False(a.Improved);
        Assert.Empty(a.Changes);
        Assert.Equal(a.OptimizedPDelivery, b.OptimizedPDelivery, 6);
        Assert.Equal(a.BaselineScore, b.BaselineScore, 6);
    }
}
