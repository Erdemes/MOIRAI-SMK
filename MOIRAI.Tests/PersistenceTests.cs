using System.IO;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

[Collection("Repository")]
public class PersistenceTests
{
    [Fact]
    public void Saving_with_infinite_flight_duty_cap_does_not_throw_and_round_trips()
    {
        var repo = new JsonRepository();
        repo.AddPilot(new Pilot { Name = "Test", ShortName = "TST" });

        var path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.json");
        try
        {
            repo.SaveSessionTo(path);
            repo.LoadSessionFrom(path);
            Assert.True(double.IsPositiveInfinity(repo.GetScenario().Config.MaxFlightDutyHoursPerDay));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Session_with_run_results_round_trips_through_the_file()
    {
        var scenario = new Scenario { Id = "s1", Name = "Baseline", CreatedAt = "14:05" };
        scenario.Campaigns.Add(new Campaign { Id = "c1", Name = "C1" });
        scenario.ScopeCampaignIds.Add("c1");
        scenario.IsSelected = true;
        scenario.Status = "1/1 met · 1 sorties";
        scenario.RunSnapshot = new Scenario { Name = "Baseline", HorizonStart = new DateOnly(2026, 6, 1) };
        scenario.RunSchedule = new Schedule
        {
            Sorties = { new Sortie { CampaignId = "c1", Date = new DateOnly(2026, 6, 3), Block = 2, SortieNo = 1 } },
            CampaignEndDates = { ["c1"] = new DateOnly(2026, 6, 3) },
            CampaignCompleted = { ["c1"] = true },
            Bottlenecks = { ["c1"] = new BottleneckTally { Placed = 1, Aircraft = 2 } },
        };
        scenario.CampaignResults = new()
        {
            new CampaignOutcome { CampaignId = "c1", CampaignName = "C1", Status = "Met", EndText = "2026-06-03" },
        };

        var session = new Session { Name = "FW 2026", ActiveScenarioId = "s1" };
        session.Scenarios.Add(scenario);

        var repo = new JsonRepository();
        var path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(session, ScenarioJson.Options));
            repo.LoadSessionFrom(path);

            var loaded = repo.GetSession();
            Assert.Equal("FW 2026", loaded.Name);
            var sc = Assert.Single(loaded.Scenarios);
            Assert.Equal("Baseline", sc.Name);
            Assert.True(sc.IsSelected);
            Assert.Equal(new[] { "c1" }, sc.ScopeCampaignIds);

            var sortie = Assert.Single(sc.RunSchedule!.Sorties);
            Assert.Equal(new DateOnly(2026, 6, 3), sortie.Date);
            Assert.Equal(2, sortie.Block);
            Assert.True(sc.RunSchedule.MeetsDeadline("c1", new DateOnly(2026, 6, 30)));
            Assert.Equal(2, sc.RunSchedule.Bottlenecks["c1"].Aircraft);

            var result = Assert.Single(sc.CampaignResults!);
            Assert.Equal("Met", result.Status);
            Assert.Equal("Baseline", sc.RunSnapshot!.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void V2_fields_round_trip_through_a_json_clone()
    {
        var s = new Scenario
        {
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = Enumerable.Range(1, 12).Select(m => m / 12.0).ToArray(),
                Persistence = 0.4,
                PatternLength = 10,
                Anchor = new DateOnly(2026, 1, 1),
            },
            HorizonStart = new DateOnly(2026, 6, 1),
            HorizonEnd = new DateOnly(2026, 8, 31),
        };
        s.Pilots.Add(new Pilot
        {
            Id = "p1", Name = "P1",
            LeaveWindows = { new DateRange { Start = new DateOnly(2026, 7, 1), End = new DateOnly(2026, 7, 10), Note = "annual leave" } },
        });
        s.Aircraft.Add(new Aircraft
        {
            Id = "TC-001", Name = "TC-001",
            MaintenanceWindows = { new DateRange { Start = new DateOnly(2026, 6, 5), End = new DateOnly(2026, 6, 20), Note = "200h periodic" } },
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "C1",
            PrerequisiteCampaignIds = { "c0" },
            BlocksPerSortie = 3,
            AllowedBlocks = { 1, 2 },
            TestEfficiencyEnabled = true,
            RetestCurve = RetestModel.PowerLaw,
            RetestExponent = 1.5,
            PriorityKey = 2.5,
            PrerequisiteMaintenanceIds = { "w1" },
        });
        s.Aircraft[0].MaintenanceWindows[0].Id = "w1";
        s.Calendar.ExtraWorkdays.Add(new DateOnly(2026, 7, 4));

        var clone = ScenarioJson.Clone(s);

        var model = Assert.IsType<ProbabilisticFlyableDays>(clone.FlyableModel);
        Assert.Equal(0.4, model.Persistence);
        Assert.Equal(((ProbabilisticFlyableDays)s.FlyableModel).MonthlyRates, model.MonthlyRates);
        Assert.Equal(new DateOnly(2026, 1, 1), model.Anchor);

        var leave = Assert.Single(clone.Pilots.Single().LeaveWindows);
        Assert.Equal(new DateOnly(2026, 7, 1), leave.Start);
        Assert.Equal(new DateOnly(2026, 7, 10), leave.End);
        Assert.Equal("annual leave", leave.Note);

        var maint = Assert.Single(clone.Aircraft.Single().MaintenanceWindows);
        Assert.Equal(new DateOnly(2026, 6, 5), maint.Start);
        Assert.Equal(new DateOnly(2026, 6, 20), maint.End);
        Assert.Equal("200h periodic", maint.Note);

        var camp = clone.Campaigns.Single();
        Assert.Equal(new[] { "c0" }, camp.PrerequisiteCampaignIds);
        Assert.Equal(3, camp.BlocksPerSortie);
        Assert.Equal(new[] { 1, 2 }, camp.AllowedBlocks);
        Assert.Equal(RetestModel.PowerLaw, camp.RetestCurve);
        Assert.Equal(1.5, camp.RetestExponent);
        Assert.Equal(2.5, camp.PriorityKey);
        Assert.Contains(new DateOnly(2026, 7, 4), clone.Calendar.ExtraWorkdays);
        Assert.Equal(new[] { "w1" }, camp.PrerequisiteMaintenanceIds);
        Assert.Equal("w1", clone.Aircraft.Single().MaintenanceWindows.Single().Id);
    }

    [Fact]
    public void Stored_Monte_Carlo_result_round_trips_through_a_json_clone()
    {
        var s = new Scenario
        {
            HorizonStart = new DateOnly(2026, 6, 1),
            HorizonEnd = new DateOnly(2026, 8, 31),
            McResult = new MOIRAI.Analysis.MonteCarlo.Result
            {
                Runs = 2000,
                PDelivery = 0.62,
                FinishP50 = new DateOnly(2026, 8, 10),
                FinishP50Run = 137,
                Campaigns = new()
                {
                    new MOIRAI.Analysis.MonteCarlo.CampaignRisk
                    {
                        CampaignId = "c1", CampaignName = "C1",
                        PMeet = 0.8, P50 = new DateOnly(2026, 7, 20), P50Run = 42,
                    },
                },
            },
            McReplay = new McReplayInfo { Seed = 12345, FinishP10Run = 5, FinishP50Run = 137, FinishP90Run = 900 },
        };

        var clone = ScenarioJson.Clone(s);

        var mc = Assert.IsType<MOIRAI.Analysis.MonteCarlo.Result>(clone.McResult);
        Assert.Equal(2000, mc.Runs);
        Assert.Equal(0.62, mc.PDelivery);
        Assert.Equal(new DateOnly(2026, 8, 10), mc.FinishP50);
        Assert.Equal(137, mc.FinishP50Run);
        var risk = Assert.Single(mc.Campaigns);
        Assert.Equal("c1", risk.CampaignId);
        Assert.Equal(0.8, risk.PMeet);
        Assert.Equal(42, risk.P50Run);
        Assert.Equal(900, clone.McReplay!.FinishP90Run);
    }

    [Fact]
    public void Legacy_single_scenario_file_migrates_into_a_session()
    {
        const string legacy = """
        {
          "name": "Old Program",
          "pilots": [{ "id": "p1", "name": "P1", "shortName": "P1" }],
          "campaigns": [
            { "id": "c1", "name": "C1", "totalTestPoints": 10, "testPointsPerSortie": 5 },
            { "id": "c2", "name": "C2", "totalTestPoints": 8, "testPointsPerSortie": 4 }
          ],
          "horizonStart": "2026-06-01",
          "horizonEnd": "2026-08-31",
          "plans": [
            {
              "name": "Plus one jet",
              "createdAt": "09:30",
              "status": "1/1 met · 2 sorties",
              "isSelected": true,
              "runScenario": { "name": "Old Program", "campaigns": [{ "id": "c1", "name": "C1" }] },
              "runSchedule": { "campaignCompleted": { "c1": true } },
              "campaignResults": [
                { "campaignId": "c1", "campaignName": "C1", "status": "Met", "endText": "2026-06-20" }
              ]
            }
          ]
        }
        """;

        var repo = new JsonRepository();
        var path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, legacy);
            repo.LoadSessionFrom(path);

            var session = repo.GetSession();
            Assert.Equal("Old Program", session.Name);
            Assert.Equal(2, session.Scenarios.Count);

            var baseScenario = session.Scenarios[0];
            Assert.Equal(session.ActiveScenarioId, baseScenario.Id);
            Assert.Single(baseScenario.Pilots);
            Assert.Equal(2, baseScenario.Campaigns.Count);

            var migrated = session.Scenarios[1];
            Assert.Equal("Plus one jet", migrated.Name);
            Assert.True(migrated.IsSelected);
            Assert.Equal("1/1 met · 2 sorties", migrated.Status);
            Assert.NotNull(migrated.RunSnapshot);
            Assert.True(migrated.RunSchedule!.CampaignCompleted["c1"]);
            Assert.Equal("Met", Assert.Single(migrated.CampaignResults!).Status);
            Assert.Equal(2, migrated.Campaigns.Count);
            Assert.Equal(new[] { "c1" }, migrated.ScopeCampaignIds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Referential_integrity_guards_block_deleting_still_referenced_resources()
    {
        var scenario = new Scenario { Id = "s1", Name = "Base" };
        scenario.Aircraft.Add(new Aircraft { Id = "CHASE-1", Name = "Chase" });
        scenario.Locations.Add(new Location { Id = "L1", Name = "Range" });
        scenario.Simulators.Add(new Simulator { Id = "SIM-A", Name = "Sim A" });
        scenario.Simulators.Add(new Simulator { Id = "SIM-L", Name = "Range sim", LocationId = "L1" });
        scenario.Campaigns.Add(new Campaign
        {
            Id = "c1", Name = "C1",
            SupportAircraftPerSortie = 1, SupportAircraftIds = { "CHASE-1" },
            CompatibleSimulatorIds = { "SIM-A" },
        });
        var session = new Session { Name = "T", ActiveScenarioId = "s1" };
        session.Scenarios.Add(scenario);

        var repo = new JsonRepository();
        var path = Path.Combine(Path.GetTempPath(), $"moirai-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(session, ScenarioJson.Options));
            repo.LoadSessionFrom(path);

            Assert.Contains("C1", Assert.Throws<InvalidOperationException>(() => repo.DeleteAircraft("CHASE-1")).Message);
            Assert.Contains("C1", Assert.Throws<InvalidOperationException>(() => repo.DeleteSimulator("SIM-A")).Message);
            Assert.Contains("Range sim", Assert.Throws<InvalidOperationException>(() => repo.DeleteLocation("L1")).Message);

            repo.DeleteSimulator("SIM-L");
            Assert.DoesNotContain(repo.GetSimulators(), s => s.Id == "SIM-L");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
