using System.Text.Json;
using ClosedXML.Excel;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

public class SimulatorCampaignTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario AircraftOnly()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar(),
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 5, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(60),
        };
        s.Pilots.Add(new Pilot { Id = "P1", Name = "P1" });
        s.Engineers.AddRange(new[] { "E1", "E2" }.Select(id => new Engineer { Id = id, Name = id }));
        s.Aircraft.Add(new Aircraft { Id = "TC-01", Name = "TC-01" });
        s.Campaigns.Add(new Campaign
        {
            Id = "A", Name = "A",
            TotalTestPoints = 12, TestPointsPerSortie = 4,
            CompatibleAircraftIds = { "TC-01" },
            AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "E1", "E2" },
            StartDate = Start, TargetEndDate = Start.AddDays(30),
            RehearsalRequired = false,
        });
        return s;
    }

    [Fact]
    public void Campaign_venue_defaults_to_aircraft()
        => Assert.Equal(CampaignVenue.Aircraft, new Campaign().Venue);

    [Fact]
    public void Schedule_starts_with_an_empty_simulator_sortie_list()
        => Assert.Empty(new Schedule().SimulatorSorties);

    [Fact]
    public void Missing_venue_in_json_deserializes_to_aircraft()
    {
        var c = JsonSerializer.Deserialize<Campaign>("{}", ScenarioJson.Options)!;
        Assert.Equal(CampaignVenue.Aircraft, c.Venue);
    }

    [Fact]
    public void Venue_and_simulator_sortie_roundtrip_through_json()
    {
        var c = new Campaign
        {
            Id = "S", Name = "Sim",
            Venue = CampaignVenue.Simulator,
            CompatibleSimulatorIds = { "SIM-1" },
        };
        var backC = JsonSerializer.Deserialize<Campaign>(
            JsonSerializer.Serialize(c, ScenarioJson.Options), ScenarioJson.Options)!;
        Assert.Equal(CampaignVenue.Simulator, backC.Venue);
        Assert.Equal(new[] { "SIM-1" }, backC.CompatibleSimulatorIds);

        var ss = new SimulatorSortie
        {
            CampaignId = "S", SimulatorId = "SIM-1", Block = 2, SortieNo = 3, TestPointsFlown = 4,
        };
        var backS = JsonSerializer.Deserialize<SimulatorSortie>(
            JsonSerializer.Serialize(ss, ScenarioJson.Options), ScenarioJson.Options)!;
        Assert.Equal("SIM-1", backS.SimulatorId);
        Assert.Equal(2, backS.Block);
        Assert.Equal(3, backS.SortieNo);
        Assert.Equal(4, backS.TestPointsFlown);
    }

    [Fact]
    public void Aircraft_only_program_has_no_simulator_sorties_and_stays_byte_identical()
    {
        var s = AircraftOnly();
        var greedy = new Scheduler().Run(s);
        Assert.Empty(greedy.SimulatorSorties);

        var revising = new RevisingScheduler().Run(s);
        Assert.Equal(
            JsonSerializer.Serialize(greedy, ScenarioJson.Options),
            JsonSerializer.Serialize(revising, ScenarioJson.Options));
    }

    private static Scenario SimScenario(
        int devices = 1, int maxBlocksPerDay = 0, double flyableRate = 1.0,
        double totalTp = 12, double tpPerSortie = 4, int blocksPerSortie = 1,
        int engineersPerSortie = 1, int pilotsPerSortie = 0, int maxSortiesPerDay = 0)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar(),
            FlyableModel = new RateBasedFlyableDays { Rate = flyableRate, PatternLength = 5, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(120),
        };
        s.Pilots.Add(new Pilot { Id = "P1", Name = "P1" });
        s.Engineers.AddRange(new[] { "E1", "E2" }.Select(id => new Engineer { Id = id, Name = id }));
        for (int d = 1; d <= devices; d++)
            s.Simulators.Add(new Simulator { Id = $"SIM-{d}", Name = $"SIM-{d}", MaxBlocksPerDay = maxBlocksPerDay });
        s.Campaigns.Add(new Campaign
        {
            Id = "DEV", Name = "DEV",
            Venue = CampaignVenue.Simulator,
            TotalTestPoints = totalTp, TestPointsPerSortie = tpPerSortie,
            BlocksPerSortie = blocksPerSortie,
            AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "E1", "E2" },
            PilotsPerSortie = pilotsPerSortie, EngineersPerSortie = engineersPerSortie,
            MaxSortiesPerDay = maxSortiesPerDay,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        });
        return s;
    }

    [Fact]
    public void Sim_campaign_with_a_device_completes_and_uses_no_aircraft()
    {
        var sch = new Scheduler().Run(SimScenario());
        Assert.True(sch.CampaignCompleted["DEV"]);
        Assert.Equal(3, sch.SimulatorSorties.Count);
        Assert.Empty(sch.Sorties);
        Assert.All(sch.SimulatorSorties, s => Assert.Equal("SIM-1", s.SimulatorId));
        Assert.NotNull(sch.ProgramFinish);
        Assert.Equal(new[] { 1, 2, 3 }, sch.SimulatorSorties.OrderBy(s => s.Date).Select(s => s.SortieNo));
    }

    [Fact]
    public void Sim_campaign_with_no_device_is_blocked_with_a_simulator_bottleneck()
    {
        var s = SimScenario(devices: 0);
        var sch = new Scheduler().Run(s);
        Assert.False(sch.CampaignCompleted["DEV"]);
        Assert.Empty(sch.SimulatorSorties);
        Assert.True(sch.Bottlenecks["DEV"].Simulator > 0, "expected a Simulator bottleneck");
        Assert.Contains(DayLogCodes.Simulator, sch.DayLog["DEV"]);
    }

    [Fact]
    public void Sim_campaign_completes_under_a_fully_unflyable_model()
    {
        var sch = new Scheduler().Run(SimScenario(flyableRate: 0.0));
        Assert.True(sch.CampaignCompleted["DEV"]);
        Assert.Equal(3, sch.SimulatorSorties.Count);
    }

    [Fact]
    public void Sim_sessions_respect_the_device_max_blocks_per_day()
    {
        var sch = new Scheduler().Run(SimScenario(maxBlocksPerDay: 1));
        Assert.True(sch.CampaignCompleted["DEV"]);
        foreach (var byDay in sch.SimulatorSorties.GroupBy(s => (s.Date, s.SimulatorId)))
            Assert.True(byDay.Count() <= 1, $"{byDay.Key} hosted {byDay.Count()} sessions over its 1-block cap");
    }

    [Fact]
    public void Sim_campaign_respects_max_sorties_per_day()
    {
        var sch = new Scheduler().Run(SimScenario(devices: 3, maxSortiesPerDay: 1));
        Assert.True(sch.CampaignCompleted["DEV"]);
        foreach (var byDay in sch.SimulatorSorties.GroupBy(s => s.Date))
            Assert.True(byDay.Count() <= 1, $"{byDay.Key} placed {byDay.Count()} sessions over the daily cap of 1");
    }

    [Fact]
    public void Sim_sessions_are_not_a_flying_day_so_rule_19_does_not_apply()
    {
        var s = SimScenario(maxBlocksPerDay: 1, totalTp: 10, tpPerSortie: 1, maxSortiesPerDay: 1);
        var sch = new Scheduler().Run(s);
        Assert.True(sch.CampaignCompleted["DEV"]);
        Assert.Equal(10, sch.SimulatorSorties.Count);
    }

    [Fact]
    public void Sim_campaign_gates_a_flight_campaign_as_a_prerequisite()
    {
        var s = SimScenario();
        s.Aircraft.Add(new Aircraft { Id = "TC-01", Name = "TC-01" });
        s.Campaigns.Add(new Campaign
        {
            Id = "FLT", Name = "FLT",
            TotalTestPoints = 8, TestPointsPerSortie = 4,
            CompatibleAircraftIds = { "TC-01" },
            AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "E1", "E2" },
            EngineersPerSortie = 1,
            PrerequisiteCampaignIds = { "DEV" },
            RehearsalRequired = false,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        });

        var sch = new Scheduler().Run(s);
        Assert.True(sch.CampaignCompleted["DEV"]);
        Assert.True(sch.CampaignCompleted["FLT"]);
        var devEnd = sch.CampaignEndDates["DEV"]!.Value;
        var fltFirst = sch.Sorties.Where(x => x.CampaignId == "FLT").Min(x => x.Date);
        Assert.True(fltFirst > devEnd, $"FLT first sortie {fltFirst} should follow DEV completion {devEnd}");
    }

    private static string Json(Schedule sch) => JsonSerializer.Serialize(sch, ScenarioJson.Options);

    private static RevisingScheduler SmartEngine() => new(new RevisingOptions
    {
        EnableLocalRepair = true, EnableTightnessOrder = true,
        EnableScarcityCrew = true, EnableBottleneckSteer = true,
    });

    private static Scenario MixedScenario()
    {
        var s = SimScenario();
        s.Aircraft.Add(new Aircraft { Id = "TC-01", Name = "TC-01" });
        s.Campaigns.Add(new Campaign
        {
            Id = "FLT", Name = "FLT",
            TotalTestPoints = 8, TestPointsPerSortie = 4,
            CompatibleAircraftIds = { "TC-01" },
            AssignedPilotIds = { "P1" }, AssignedEngineerIds = { "E1", "E2" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            PrerequisiteCampaignIds = { "DEV" },
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        });
        return s;
    }

    [Fact]
    public void Advanced_off_is_byte_identical_to_greedy_on_a_sim_scenario()
    {
        var s = SimScenario();
        Assert.Equal(Json(new Scheduler().Run(s)), Json(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Advanced_off_is_byte_identical_to_greedy_on_a_mixed_sim_and_flight_scenario()
    {
        var s = MixedScenario();
        Assert.Equal(Json(new Scheduler().Run(s)), Json(new RevisingScheduler().Run(s)));
    }

    [Fact]
    public void Smart_engine_completes_a_sim_campaign_and_is_never_worse_than_greedy()
    {
        var s = MixedScenario();
        var greedy = new Scheduler().Run(s);
        var smart = SmartEngine().Run(s);
        Assert.True(smart.CampaignCompleted["DEV"]);
        Assert.True(smart.CampaignCompleted["FLT"]);
        Assert.True(smart.SimulatorSorties.Count >= greedy.SimulatorSorties.Count);
        Assert.True(smart.ProgramFinish <= greedy.ProgramFinish, "smart finish should not be later than greedy");
    }

    [Fact]
    public void Validator_errors_when_a_sim_campaign_has_no_compatible_device()
    {
        var issues = ScenarioValidator.Validate(SimScenario(devices: 0));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Category == "Simulators");
        Assert.DoesNotContain(issues, i => i.Category is "Aircraft" or "Type ratings");
    }

    [Fact]
    public void Validator_accepts_a_sim_campaign_with_a_device_and_zero_pilots()
    {
        var issues = ScenarioValidator.Validate(SimScenario());
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Validator_does_not_require_aircraft_for_a_pure_sim_scenario()
    {
        var issues = ScenarioValidator.Validate(SimScenario());
        Assert.DoesNotContain(issues,
            i => i.Category == "Aircraft" && i.Message.Contains("No aircraft are defined"));
    }

    [Fact]
    public void Deleting_the_last_main_base_device_a_sim_campaign_relies_on_is_blocked()
    {
        var repo = new JsonRepository();
        repo.NewSession();
        try
        {
            repo.AddSimulator(new Simulator { Name = "SIM-1" });
            repo.AddCampaign(new Campaign
            {
                Name = "DEV", Venue = CampaignVenue.Simulator,
                TotalTestPoints = 4, TestPointsPerSortie = 4,
            });
            var simId = repo.GetScenario().Simulators.Single().Id;
            Assert.Throws<InvalidOperationException>(() => repo.DeleteSimulator(simId));
        }
        finally
        {
            repo.NewSession();
        }
    }

    [Fact]
    public void Reporting_includes_a_simulator_sorties_sheet_and_does_not_crash()
    {
        var s = SimScenario();
        var sch = new Scheduler().Run(s);
        Assert.NotEmpty(sch.SimulatorSorties);

        string xlsx = Path.Combine(Path.GetTempPath(), $"moirai-sim-{Guid.NewGuid():N}.xlsx");
        string pdf = Path.Combine(Path.GetTempPath(), $"moirai-sim-{Guid.NewGuid():N}.pdf");
        try
        {
            ExcelExporter.Export(s, sch, xlsx);
            using (var wb = new XLWorkbook(xlsx))
                Assert.Contains(wb.Worksheets, w => w.Name == "Simulator sorties");
            PdfReporter.Export(s, sch, pdf);
            Assert.True(new FileInfo(pdf).Length > 0);
        }
        finally
        {
            File.Delete(xlsx);
            File.Delete(pdf);
        }
    }

    [Fact]
    public void Program_report_counts_sim_sessions_in_its_totals_and_crew()
    {
        var s = SimScenario();
        var sch = new Scheduler().Run(s);
        var report = ProgramReporter.Build(s, sch);

        Assert.True(sch.SimulatorSorties.Count > 0);
        Assert.Equal(sch.Sorties.Count + sch.SimulatorSorties.Count, report.TotalSorties);
        Assert.True(report.TotalDutyHours > 0, "sim session duty should be counted");
        Assert.True(report.CrewUsed > 0, "sim session crew should be counted");
        Assert.NotNull(report.ProgramFinish);
    }

    [Fact]
    public void Decision_log_includes_sim_campaign_sessions()
    {
        var s = SimScenario();
        var sch = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true,
        }) { CaptureDecisions = true }.Run(s);
        Assert.Contains(sch.DecisionLog, e => e.CampaignId == "DEV" && e.Message.Contains("sim session"));
    }

    [Fact]
    public void Sim_actual_sessions_replay_as_history_and_the_rest_replans()
    {
        var s = SimScenario();
        s.ActualSimulatorSorties.Add(new SimulatorSortie
        {
            CampaignId = "DEV", Date = Start, Block = 1, SortieNo = 1,
            SimulatorId = "SIM-1", EngineerIds = { "E1" }, TestPointsFlown = 4,
        });
        s.FreezeDate = Start.AddDays(1);

        var sch = new Scheduler().Run(s);
        Assert.Contains(sch.SimulatorSorties, ss => ss.IsActual && ss.SortieNo == 1);
        Assert.True(sch.CampaignCompleted["DEV"]);
        var planned = sch.SimulatorSorties.Where(ss => !ss.IsActual).ToList();
        Assert.Equal(2, planned.Count);
        Assert.All(planned, ss => Assert.True(ss.Date >= s.FreezeDate));

        var adv = SmartEngine().Run(s);
        Assert.True(adv.CampaignCompleted["DEV"]);
        Assert.Contains(adv.SimulatorSorties, ss => ss.IsActual);
    }
}
