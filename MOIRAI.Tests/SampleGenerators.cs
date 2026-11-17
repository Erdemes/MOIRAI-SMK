using System.IO;
using System.Text.Json;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

[CollectionDefinition("Repository")]
public sealed class RepositoryCollection { }

[Collection("Repository")]
public class SampleGenerators
{
    [Fact]
    public void Write_revising_engine_demo_session()
    {
        var start = new DateOnly(2026, 6, 1);

        var sc = new Scenario
        {
            Id = "revising-demo",
            Name = "Revising engine demo",
            CreatedAt = "00:00",
            Config = new ScenarioConfig { MaxFlightHoursPer7Days = 6, UseRevisingEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(12),
        };

        sc.Pilots.Add(new Pilot { Id = "SP", Name = "Shared Pilot", ShortName = "SP" });
        sc.Engineers.Add(new Engineer { Id = "E-RTN", Name = "FTE Routine", ShortName = "E-RTN" });
        sc.Engineers.Add(new Engineer { Id = "E-URG", Name = "FTE Urgent", ShortName = "E-URG" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-RTN", Name = "TC-RTN" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-URG", Name = "TC-URG" });

        sc.Campaigns.Add(new Campaign
        {
            Id = "ROUTINE", Name = "Routine survey",
            TotalTestPoints = 99, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-RTN" },
            AssignedPilotIds = { "SP" }, AssignedEngineerIds = { "E-RTN" }, EngineersPerSortie = 1,
            RehearsalRequired = false,
            StartDate = start, TargetEndDate = start.AddDays(40),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "URGENT", Name = "Urgent envelope",
            TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-URG" },
            AssignedPilotIds = { "SP" }, AssignedEngineerIds = { "E-URG" }, EngineersPerSortie = 1,
            RehearsalRequired = false,
            StartDate = start.AddDays(3), TargetEndDate = start.AddDays(5),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var session = new Session { Name = "Revising engine demo", ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);

        var dir = Path.Combine(FindRepoRoot(), "samples");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "revising-engine-demo.json");
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        var urgentTarget = start.AddDays(5);
        Assert.False(new Scheduler().Run(sc).MeetsDeadline("URGENT", urgentTarget));
        Assert.True(new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true, EnableTightnessOrder = true })
            .Run(sc).MeetsDeadline("URGENT", urgentTarget));

        var repo = new JsonRepository();
        repo.LoadSessionFrom(path);
        Assert.True(repo.GetScenario().Config.UseRevisingEngine);
    }

    [Fact]
    public void Write_revising_tightness_demo_session()
    {
        var start = new DateOnly(2026, 6, 1);

        var sc = new Scenario
        {
            Id = "tightness-demo",
            Name = "Tightness order demo",
            CreatedAt = "00:00",
            Config = new ScenarioConfig { UseRevisingEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(24),
        };

        sc.Aircraft.Add(new Aircraft { Id = "TC-01", Name = "TC-01" });
        sc.Pilots.Add(new Pilot
        {
            Id = "FL", Name = "Flutter Lead", ShortName = "FL",
            LeaveWindows = { new DateRange { Start = start.AddDays(4), End = start.AddDays(20), Note = "course" } },
        });
        sc.Pilots.Add(new Pilot { Id = "RP", Name = "Routine Pilot", ShortName = "RP" });
        sc.Engineers.Add(new Engineer { Id = "FE", Name = "Flutter FTE", ShortName = "FE" });
        sc.Engineers.Add(new Engineer { Id = "RE", Name = "Routine FTE", ShortName = "RE" });

        sc.Campaigns.Add(new Campaign
        {
            Id = "FLUTTER", Name = "Flutter clearance",
            TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-01" },
            AssignedPilotIds = { "FL" }, AssignedEngineerIds = { "FE" }, EngineersPerSortie = 1,
            RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start.AddDays(20),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "ROUTINE", Name = "Routine handling",
            TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-01" },
            AssignedPilotIds = { "RP" }, AssignedEngineerIds = { "RE" }, EngineersPerSortie = 1,
            RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start.AddDays(10),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var session = new Session { Name = "Tightness order demo", ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);

        var path = Path.Combine(FindRepoRoot(), "samples", "revising-tightness-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        var flutterTarget = start.AddDays(20);
        Assert.False(new Scheduler().Run(sc).MeetsDeadline("FLUTTER", flutterTarget));
        Assert.True(new RevisingScheduler(new RevisingOptions { EnableLocalRepair = true, EnableTightnessOrder = true })
            .Run(sc).MeetsDeadline("FLUTTER", flutterTarget));

        var repo = new JsonRepository();
        repo.LoadSessionFrom(path);
        Assert.True(repo.GetScenario().Config.UseRevisingEngine);
    }

    [Fact]
    public void Write_revising_scarcity_demo_session()
    {
        var start = new DateOnly(2026, 6, 1);
        var deadline = start.AddDays(1);

        var sc = new Scenario
        {
            Id = "scarcity-demo",
            Name = "Scarcity crew demo",
            CreatedAt = "00:00",
            Config = new ScenarioConfig { UseRevisingEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(2),
        };

        sc.Aircraft.Add(new Aircraft { Id = "TC-A", Name = "TC-A" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-B", Name = "TC-B" });
        sc.Pilots.Add(new Pilot { Id = "SPEC", Name = "Specialist", ShortName = "SPEC", TypeRatings = { "TC-A" } });
        sc.Pilots.Add(new Pilot { Id = "GEN", Name = "Generalist", ShortName = "GEN", TypeRatings = { "TC-A", "TC-B" } });
        sc.Engineers.Add(new Engineer { Id = "E-AL", Name = "FTE Alpha", ShortName = "E-AL" });
        sc.Engineers.Add(new Engineer { Id = "E-BE", Name = "FTE Beta", ShortName = "E-BE" });
        sc.Engineers.Add(new Engineer { Id = "E-LD", Name = "FTE Loader", ShortName = "E-LD" });

        sc.Campaigns.Add(new Campaign
        {
            Id = "LOADER", Name = "Pre-load", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-A" }, AssignedPilotIds = { "SPEC" }, AssignedEngineerIds = { "E-LD" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start, TargetEndDate = start,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "ALPHA", Name = "Alpha (flexible)", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-A" }, AssignedPilotIds = { "SPEC", "GEN" }, AssignedEngineerIds = { "E-AL" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(1), TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "BETA", Name = "Beta (needs the generalist)", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-B" }, AssignedPilotIds = { "GEN" }, AssignedEngineerIds = { "E-BE" },
            EngineersPerSortie = 1, RehearsalRequired = false,
            StartDate = start.AddDays(1), TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var session = new Session { Name = "Scarcity crew demo", ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);

        var path = Path.Combine(FindRepoRoot(), "samples", "revising-scarcity-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        Assert.False(new Scheduler().Run(sc).MeetsDeadline("BETA", deadline));
        Assert.True(new RevisingScheduler(new RevisingOptions { EnableScarcityCrew = true })
            .Run(sc).MeetsDeadline("BETA", deadline));

        var repo = new JsonRepository();
        repo.LoadSessionFrom(path);
        Assert.True(repo.GetScenario().Config.UseRevisingEngine);
    }

    [Fact]
    public void Write_revising_bottleneck_demo_session()
    {
        var start = new DateOnly(2026, 6, 1);
        var deadline = start;

        var sc = new Scenario
        {
            Id = "bottleneck-demo",
            Name = "Bottleneck steering demo",
            CreatedAt = "00:00",
            Config = new ScenarioConfig { UseRevisingEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(2),
        };

        sc.Aircraft.Add(new Aircraft { Id = "TC-SHARED", Name = "TC-SHARED" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-SPARE", Name = "TC-SPARE" });
        sc.Pilots.Add(new Pilot { Id = "PF", Name = "Pilot F", ShortName = "PF" });
        sc.Pilots.Add(new Pilot { Id = "PL", Name = "Pilot L", ShortName = "PL" });
        sc.Engineers.Add(new Engineer { Id = "EF", Name = "FTE F", ShortName = "EF" });
        sc.Engineers.Add(new Engineer { Id = "EL", Name = "FTE L", ShortName = "EL" });

        sc.Campaigns.Add(new Campaign
        {
            Id = "FLEX", Name = "Flexible campaign", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-SHARED", "TC-SPARE" }, AssignedPilotIds = { "PF" }, AssignedEngineerIds = { "EF" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "LOCKED", Name = "Locked to the shared jet", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-SHARED" }, AssignedPilotIds = { "PL" }, AssignedEngineerIds = { "EL" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = deadline,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var session = new Session { Name = "Bottleneck steering demo", ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);

        var path = Path.Combine(FindRepoRoot(), "samples", "revising-bottleneck-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        Assert.False(new Scheduler().Run(sc).MeetsDeadline("LOCKED", deadline));
        Assert.True(new RevisingScheduler(new RevisingOptions { EnableBottleneckSteer = true })
            .Run(sc).MeetsDeadline("LOCKED", deadline));

        var repo = new JsonRepository();
        repo.LoadSessionFrom(path);
        Assert.True(repo.GetScenario().Config.UseRevisingEngine);
    }

    [Fact]
    public void Write_revising_complex_demo_session()
    {
        var start = new DateOnly(2026, 6, 1);

        var sc = new Scenario
        {
            Id = "complex-demo",
            Name = "Complex demo (all levers)",
            CreatedAt = "00:00",
            Config = new ScenarioConfig { UseRevisingEngine = true },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(24),
        };

        sc.Aircraft.Add(new Aircraft { Id = "TC-SHARED", Name = "TC-SHARED" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-SPARE", Name = "TC-SPARE" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-FLT", Name = "TC-FLT" });

        sc.Pilots.Add(new Pilot { Id = "PF", Name = "Pilot Flex", ShortName = "PF" });
        sc.Pilots.Add(new Pilot { Id = "PL", Name = "Pilot Locked", ShortName = "PL" });
        sc.Pilots.Add(new Pilot
        {
            Id = "FL", Name = "Flutter Lead", ShortName = "FL",
            LeaveWindows = { new DateRange { Start = start.AddDays(4), End = start.AddDays(20), Note = "course" } },
        });
        sc.Pilots.Add(new Pilot { Id = "RP", Name = "Routine Pilot", ShortName = "RP" });
        foreach (var e in new[] { "E-FX", "E-LK", "E-FT", "E-RT" })
            sc.Engineers.Add(new Engineer { Id = e, Name = e, ShortName = e });

        sc.Campaigns.Add(new Campaign
        {
            Id = "FLEX", Name = "Flexible campaign", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-SHARED", "TC-SPARE" }, AssignedPilotIds = { "PF" }, AssignedEngineerIds = { "E-FX" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "LOCKED", Name = "Locked to the shared jet", TotalTestPoints = 1, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-SHARED" }, AssignedPilotIds = { "PL" }, AssignedEngineerIds = { "E-LK" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start,
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        sc.Campaigns.Add(new Campaign
        {
            Id = "FLUTTER", Name = "Flutter clearance", TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-FLT" }, AssignedPilotIds = { "FL" }, AssignedEngineerIds = { "E-FT" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start.AddDays(20),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });
        sc.Campaigns.Add(new Campaign
        {
            Id = "ROUTINE", Name = "Routine handling", TotalTestPoints = 3, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "TC-FLT" }, AssignedPilotIds = { "RP" }, AssignedEngineerIds = { "E-RT" },
            EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = 3,
            StartDate = start, TargetEndDate = start.AddDays(10),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
        });

        var session = new Session { Name = "Complex demo (all levers)", ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);

        var path = Path.Combine(FindRepoRoot(), "samples", "revising-complex-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        var greedy = new Scheduler().Run(sc);
        Assert.False(greedy.MeetsDeadline("LOCKED", start));
        Assert.False(greedy.MeetsDeadline("FLUTTER", start.AddDays(20)));

        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true,
        }).Run(sc);
        Assert.True(revised.MeetsDeadline("FLEX", start));
        Assert.True(revised.MeetsDeadline("LOCKED", start));
        Assert.True(revised.MeetsDeadline("FLUTTER", start.AddDays(20)));
        Assert.True(revised.MeetsDeadline("ROUTINE", start.AddDays(10)));
    }

    [Fact]
    public void Write_revising_large_demo_session()
    {
        var start = new DateOnly(2026, 6, 1);
        var end = new DateOnly(2026, 7, 31);

        var sc = new Scenario
        {
            Id = "large-demo",
            Name = "Large program (8 campaigns, 5 aircraft)",
            CreatedAt = "00:00",
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { Holidays = { new DateOnly(2026, 7, 20) } },
            FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = end,
        };

        sc.Aircraft.Add(new Aircraft { Id = "TC-101", Name = "TC-101" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-102", Name = "TC-102" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-201", Name = "TC-201" });
        var heavy = new Aircraft { Id = "TC-301", Name = "TC-301" };
        var modBlock = new DateRange
        {
            Id = "mw-tc301-modblock", Start = new DateOnly(2026, 6, 22), End = new DateOnly(2026, 7, 6),
            Note = "weapon mod-block integration",
        };
        heavy.MaintenanceWindows.Add(modBlock);
        sc.Aircraft.Add(heavy);
        sc.Aircraft.Add(new Aircraft { Id = "TC-401", Name = "TC-401" });

        sc.Pilots.Add(new Pilot { Id = "P1", Name = "Pilot 1", ShortName = "P1" });
        sc.Pilots.Add(new Pilot { Id = "P2", Name = "Pilot 2", ShortName = "P2" });
        sc.Pilots.Add(new Pilot
        {
            Id = "P3", Name = "Pilot 3", ShortName = "P3",
            LeaveWindows = { new DateRange { Id = "lv-p3", Start = new DateOnly(2026, 6, 15), End = new DateOnly(2026, 7, 5), Note = "exchange tour" } },
        });
        sc.Pilots.Add(new Pilot { Id = "P4", Name = "Pilot 4", ShortName = "P4" });
        sc.Pilots.Add(new Pilot { Id = "P5", Name = "Flutter Lead", ShortName = "P5", TypeRatings = { "TC-201" } });
        sc.Pilots.Add(new Pilot
        {
            Id = "P6", Name = "Flutter 2", ShortName = "P6", TypeRatings = { "TC-201" },
            LeaveWindows = { new DateRange { Id = "lv-p6", Start = new DateOnly(2026, 7, 10), End = new DateOnly(2026, 7, 20), Note = "course" } },
        });
        for (int i = 1; i <= 8; i++)
        {
            var e = new Engineer { Id = $"E{i}", Name = $"FTE {i}", ShortName = $"E{i}" };
            if (i == 2) e.LeaveWindows.Add(new DateRange { Id = "lv-e2", Start = new DateOnly(2026, 6, 20), End = new DateOnly(2026, 6, 30), Note = "leave" });
            if (i == 7) e.LeaveWindows.Add(new DateRange { Id = "lv-e7", Start = new DateOnly(2026, 7, 1), End = new DateOnly(2026, 7, 12), Note = "leave" });
            sc.Engineers.Add(e);
        }

        Campaign C(string id, string name, double tp, double per, DateOnly target,
            string[] tails, string[] pilots, string[] engs, int eng = 2,
            int bps = 1, string[]? prereq = null, string[]? prereqMaint = null,
            int pace = 0, DateOnly? startOn = null) => new()
            {
                Id = id, Name = name, TotalTestPoints = tp, TestPointsPerSortie = per,
                StartDate = startOn ?? start, TargetEndDate = target,
                CompatibleAircraftIds = tails.ToList(),
                AssignedPilotIds = pilots.ToList(), AssignedEngineerIds = engs.ToList(),
                EngineersPerSortie = eng, BlocksPerSortie = bps, RehearsalRequired = false,
                PrerequisiteCampaignIds = (prereq ?? Array.Empty<string>()).ToList(),
                PrerequisiteMaintenanceIds = (prereqMaint ?? Array.Empty<string>()).ToList(),
                MinWorkdaysBetweenSorties = pace,
                ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5,
            };

        sc.Campaigns.Add(C("PERF", "Performance", 60, 5, new DateOnly(2026, 6, 19),
            new[] { "TC-101", "TC-102" }, new[] { "P1", "P2" }, new[] { "E1", "E2", "E3" }, eng: 3));
        sc.Campaigns.Add(C("ENV", "Envelope expansion", 40, 4, new DateOnly(2026, 6, 30),
            new[] { "TC-101", "TC-102" }, new[] { "P1", "P2" }, new[] { "E1", "E2" }, prereq: new[] { "PERF" }));
        sc.Campaigns.Add(C("CERT", "Certification", 32, 4, new DateOnly(2026, 7, 10),
            new[] { "TC-101", "TC-102" }, new[] { "P3", "P4" }, new[] { "E3", "E4" }, prereq: new[] { "ENV" }));

        sc.Campaigns.Add(C("FLUT", "Flutter clearance", 36, 3, new DateOnly(2026, 7, 24),
            new[] { "TC-201" }, new[] { "P5", "P6" }, new[] { "E5", "E6" }, bps: 3));
        sc.Campaigns.Add(C("LOADS", "Structural loads", 30, 3, new DateOnly(2026, 7, 17),
            new[] { "TC-201", "TC-301" }, new[] { "P5" }, new[] { "E5", "E6" }, bps: 3));

        sc.Campaigns.Add(C("WEAP", "Weapon integration", 30, 2, new DateOnly(2026, 7, 22),
            new[] { "TC-301" }, new[] { "P1", "P2" }, new[] { "E4", "E5" }, prereqMaint: new[] { "mw-tc301-modblock" }));

        sc.Campaigns.Add(C("AVIO", "Avionics", 18, 3, new DateOnly(2026, 7, 15),
            new[] { "TC-101", "TC-102", "TC-401" }, new[] { "P3", "P4" }, new[] { "E6", "E7" }, pace: 3));
        sc.Campaigns.Add(C("RADAR", "Radar", 24, 3, new DateOnly(2026, 7, 22),
            new[] { "TC-401" }, new[] { "P1", "P4" }, new[] { "E7", "E8" }));

        var session = new Session { Name = sc.Name, ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);

        var path = Path.Combine(FindRepoRoot(), "samples", "revising-large-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        var greedy = new Scheduler().Run(sc);
        var revised = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(sc);

        DateOnly Target(string id) => sc.Campaigns.First(c => c.Id == id).TargetEndDate;

        Assert.False(greedy.MeetsDeadline("ENV", Target("ENV")));
        Assert.False(greedy.MeetsDeadline("CERT", Target("CERT")));
        Assert.False(greedy.MeetsDeadline("WEAP", Target("WEAP")));

        foreach (var c in sc.Campaigns)
            Assert.True(revised.MeetsDeadline(c.Id, c.TargetEndDate),
                $"revised should meet {c.Id} (target {c.TargetEndDate:yyyy-MM-dd})");
    }

    [Fact]
    public void Write_ab_hierarchy_and_advanced_analysis_demo_session()
    {
        var start = new DateOnly(2026, 11, 2);
        var end = new DateOnly(2027, 4, 30);

        var monthly = new[] { 0.45, 0.50, 0.58, 0.66, 0.80, 0.92, 0.97, 0.95, 0.88, 0.74, 0.55, 0.42 };

        var sc = new Scenario
        {
            Id = "ab-advanced-demo",
            Name = "A/B program + advanced analysis",
            CreatedAt = "00:00",
            Config = new ScenarioConfig
            {
                BlocksPerDay = 3,
                UseRevisingEngine = true,
                McUseAdvancedEngine = true,
            },
            Calendar = new FlightCalendar { Holidays = { new DateOnly(2027, 1, 1) } },
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = monthly, Persistence = 0.55, PatternLength = 7, Anchor = start,
            },
            HorizonStart = start,
            HorizonEnd = end,
            MasterDeadline = new DateOnly(2027, 4, 20),
        };

        for (int i = 1; i <= 4; i++)
            sc.Pilots.Add(new Pilot { Id = $"P{i}", Name = $"Pilot {i}", ShortName = $"P{i}" });
        for (int i = 1; i <= 6; i++)
            sc.Engineers.Add(new Engineer { Id = $"E{i}", Name = $"FTE {i}", ShortName = $"E{i}" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-101", Name = "TC-101" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-102", Name = "TC-102" });
        var heavy = new Aircraft
        {
            Id = "TC-201", Name = "TC-201",
            FailuresPer100FlightHours = 2.5, RepairDaysMin = 2, RepairDaysMax = 5,
        };
        heavy.MaintenanceWindows.Add(new DateRange
        {
            Id = "mw-201-modblock", Start = new DateOnly(2026, 12, 14), End = new DateOnly(2026, 12, 23),
            Note = "weapon mod-block integration",
        });
        sc.Aircraft.Add(heavy);

        sc.Locations.Add(new Location
        {
            Id = "KON", Name = "Konya Range",
            FerryDays = 1, FerryFlightHours = 1.5, FerryDutyHours = 5, SetupDays = 2, TeardownDays = 1,
            RangeModel = ProbabilisticFlyableDays.Uniform(0.8, 0.5, start),
            FlyableModel = new ProbabilisticFlyableDays
            {
                MonthlyRates = monthly.Select(r => r * 0.9).ToArray(), Persistence = 0.6, PatternLength = 7, Anchor = start,
            },
        });

        sc.CampaignGroups.Add(new CampaignGroup
        {
            Id = "PH-ENV", Name = "Envelope Expansion",
            TargetStartDate = start, TargetEndDate = new DateOnly(2027, 1, 31), Color = "#5B8DEF",
        });
        sc.CampaignGroups.Add(new CampaignGroup
        {
            Id = "PH-STO", Name = "Stores Clearance",
            PrerequisiteGroupIds = { "PH-ENV" },
            TargetEndDate = new DateOnly(2027, 3, 31), Color = "#F25C54",
        });

        Campaign Camp(string id, string name, double tp, DateOnly target, string[] tails,
            string[] pilots, string[] engs, string group = "", string location = "",
            bool efficiency = false) => new()
            {
                Id = id, Name = name, GroupId = group, LocationId = location,
                TotalTestPoints = tp, TestPointsPerSortie = 1,
                StartDate = start, TargetEndDate = target,
                CompatibleAircraftIds = tails.ToList(),
                AssignedPilotIds = pilots.ToList(), AssignedEngineerIds = engs.ToList(),
                EngineersPerSortie = 1, RehearsalRequired = false,
                ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5,
                TestEfficiencyEnabled = efficiency,
                RetestProbabilityStart = 0.25, RetestProbabilityFloor = 0.05,
                RetestLearningScale = 12, RetestCurve = RetestModel.Exponential, AbortProbability = 0.08,
            };

        var noTarget = DateOnly.MaxValue;

        sc.Campaigns.Add(Camp("ENV-LOW", "Low-speed handling", 5, new DateOnly(2027, 1, 20),
            new[] { "TC-101", "TC-102" }, new[] { "P1", "P2" }, new[] { "E1", "E2" }, group: "PH-ENV"));
        sc.Campaigns.Add(Camp("ENV-AOA", "High-AoA expansion", 5, new DateOnly(2027, 1, 31),
            new[] { "TC-101", "TC-102" }, new[] { "P1", "P3" }, new[] { "E2", "E3" }, group: "PH-ENV",
            efficiency: true));

        sc.Campaigns.Add(Camp("STO-CC", "Captive carry", 4, noTarget,
            new[] { "TC-101", "TC-201" }, new[] { "P2", "P4" }, new[] { "E4", "E5" }, group: "PH-STO"));
        sc.Campaigns.Add(Camp("STO-SEP", "Weapon separation", 4, new DateOnly(2027, 3, 25),
            new[] { "TC-201" }, new[] { "P3", "P4" }, new[] { "E5", "E6" }, group: "PH-STO",
            location: "KON", efficiency: true));

        sc.Campaigns.Add(Camp("AVI", "Avionics regression", 6, new DateOnly(2027, 2, 28),
            new[] { "TC-102" }, new[] { "P1", "P4" }, new[] { "E1", "E6" }));

        var session = new Session { Name = sc.Name, ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);
        var path = Path.Combine(FindRepoRoot(), "samples", "ab-advanced-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        var cc = sc.Campaigns.First(c => c.Id == "STO-CC");
        Assert.Equal(new DateOnly(2027, 3, 31), CampaignDeadlines.EffectiveMilestone(sc, cc));
        var avi = sc.Campaigns.First(c => c.Id == "AVI");
        Assert.Equal(new DateOnly(2027, 2, 28), CampaignDeadlines.EffectiveMilestone(sc, avi));

        var det = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(sc);
        var envIds = new[] { "ENV-LOW", "ENV-AOA" };
        var stoIds = new[] { "STO-CC", "STO-SEP" };
        Assert.All(envIds, id => Assert.True(det.CampaignCompleted.GetValueOrDefault(id), $"{id} should complete"));
        var lastEnv = det.Sorties.Where(s => envIds.Contains(s.CampaignId)).Max(s => s.Date);
        var firstStores = det.Sorties.Where(s => stoIds.Contains(s.CampaignId)).Min(s => s.Date);
        Assert.True(firstStores > lastEnv,
            $"Stores Clearance must fly after Envelope completes (A↔A): first Stores {firstStores}, last Envelope {lastEnv}.");

        var report = ProgramReporter.Build(sc, det);
        Assert.Equal(2, report.Groups.Count);
        Assert.All(report.Groups, g => Assert.Equal(2, g.ChildCount));

        var mc = MonteCarlo.Analyze(sc, runs: 80, seed: 7);
        Assert.InRange(mc.PDelivery, 0.0, 1.0);
        Assert.True(mc.SortiesMean > 0);

        foreach (var src in new[]
                 {
                     MonteCarlo.UncertaintySources.Weather, MonteCarlo.UncertaintySources.Yield,
                     MonteCarlo.UncertaintySources.Range, MonteCarlo.UncertaintySources.Failures,
                     MonteCarlo.UncertaintySources.All,
                 })
            Assert.InRange(MonteCarlo.Analyze(sc, 60, 7, null, default, src).PDelivery, 0.0, 1.0);

        var sens = MonteCarlo.Sensitivity(sc, runs: 60, seed: 7, sobolSamples: 16, orderSpread: true);
        Assert.True(sens.Factors.Count >= 5);
        Assert.All(sens.Factors, f =>
        {
            Assert.True(f.HasBand);
            Assert.InRange(f.LowPDelivery, f.LowPDeliveryLo - 1e-9, f.LowPDeliveryHi + 1e-9);
        });
        Assert.NotEmpty(sens.GlobalImportance);

        var repo = new JsonRepository();
        repo.LoadSessionFrom(path);
        var loaded = repo.GetScenario();
        Assert.Equal(2, loaded.CampaignGroups.Count);
        Assert.Equal("PH-STO", loaded.Campaigns.First(c => c.Id == "STO-SEP").GroupId);
        Assert.Contains("PH-ENV", loaded.CampaignGroups.First(g => g.Id == "PH-STO").PrerequisiteGroupIds);
    }

    [Fact]
    public void Write_one_year_program_demo_session()
    {
        var start = new DateOnly(2026, 1, 5);
        var end = new DateOnly(2026, 12, 31);
        var monthly = new[] { 0.45, 0.48, 0.58, 0.68, 0.80, 0.90, 0.95, 0.93, 0.85, 0.72, 0.55, 0.45 };

        var sc = new Scenario
        {
            Id = "one-year-program",
            Name = "Full-year flight-test program (A/B phases)",
            CreatedAt = "00:00",
            Config = new ScenarioConfig { BlocksPerDay = 3, UseRevisingEngine = true, McUseAdvancedEngine = true },
            Calendar = new FlightCalendar
            {
                Holidays =
                {
                    new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 23), new DateOnly(2026, 5, 1),
                    new DateOnly(2026, 5, 19), new DateOnly(2026, 7, 15), new DateOnly(2026, 8, 30),
                    new DateOnly(2026, 10, 29),
                },
            },
            FlyableModel = new ProbabilisticFlyableDays { MonthlyRates = monthly, Persistence = 0.5, PatternLength = 7, Anchor = start },
            HorizonStart = start,
            HorizonEnd = end,
            MasterDeadline = new DateOnly(2026, 10, 15),
        };

        for (int i = 1; i <= 6; i++) sc.Pilots.Add(new Pilot { Id = $"P{i}", Name = $"Pilot {i}", ShortName = $"P{i}" });
        sc.Pilots[4].TypeRatings.Add("TC-201");
        sc.Pilots[5].TypeRatings.Add("TC-201");
        sc.Pilots[2].LeaveWindows.Add(new DateRange { Id = "lv-p3", Start = new(2026, 7, 6), End = new(2026, 7, 24), Note = "annual leave" });
        sc.Pilots[5].LeaveWindows.Add(new DateRange { Id = "lv-p6", Start = new(2026, 3, 2), End = new(2026, 3, 13), Note = "course" });
        for (int i = 1; i <= 10; i++)
        {
            var e = new Engineer { Id = $"E{i}", Name = $"FTE {i}", ShortName = $"E{i}" };
            if (i == 4) e.LeaveWindows.Add(new DateRange { Id = "lv-e4", Start = new(2026, 8, 3), End = new(2026, 8, 21), Note = "annual leave" });
            sc.Engineers.Add(e);
        }

        var tc101 = new Aircraft { Id = "TC-101", Name = "TC-101" };
        tc101.PeriodicMaintenances.Add(new PeriodicMaintenanceRule { IntervalFlightHours = 100, DurationDays = 2 });
        sc.Aircraft.Add(tc101);
        sc.Aircraft.Add(new Aircraft { Id = "TC-102", Name = "TC-102" });
        sc.Aircraft.Add(new Aircraft { Id = "TC-201", Name = "TC-201" });
        var heavy = new Aircraft { Id = "TC-301", Name = "TC-301", FailuresPer100FlightHours = 2.0, RepairDaysMin = 3, RepairDaysMax = 7 };
        heavy.MaintenanceWindows.Add(new DateRange { Id = "mw-301-modblock", Start = new(2026, 8, 17), End = new(2026, 9, 4), Note = "weapon mod-block integration" });
        heavy.PeriodicMaintenances.Add(new PeriodicMaintenanceRule { IntervalFlightHours = 80, DurationDays = 3 });
        sc.Aircraft.Add(heavy);
        sc.Aircraft.Add(new Aircraft { Id = "TC-401", Name = "TC-401" });

        sc.Simulators.Add(new Simulator { Id = "SIM-1", Name = "Main sim", LocationId = "", MaxBlocksPerDay = 2 });
        sc.Locations.Add(new Location
        {
            Id = "KON", Name = "Konya Range",
            FerryDays = 1, FerryFlightHours = 1.5, FerryDutyHours = 5, SetupDays = 2, TeardownDays = 1,
            RangeModel = ProbabilisticFlyableDays.Uniform(0.8, 0.5, start),
            FlyableModel = new ProbabilisticFlyableDays { MonthlyRates = monthly.Select(r => r * 0.92).ToArray(), Persistence = 0.55, PatternLength = 7, Anchor = start },
        });

        void Phase(string id, string name, DateOnly? ts, DateOnly te, string color, params string[] prereq) =>
            sc.CampaignGroups.Add(new CampaignGroup { Id = id, Name = name, TargetStartDate = ts, TargetEndDate = te, Color = color, PrerequisiteGroupIds = prereq.ToList() });
        Phase("PH-FQ", "Flight Qualities", start, new(2026, 4, 30), "#5B8DEF");
        Phase("PH-PROP", "Propulsion & Air Data", new(2026, 4, 1), new(2026, 6, 15), "#2DA37A", "PH-FQ");
        Phase("PH-AVI", "Avionics & Sensors", new(2026, 4, 1), new(2026, 7, 31), "#9B6BDF", "PH-FQ");
        Phase("PH-ENV", "Envelope (Loads/Flutter)", new(2026, 4, 1), new(2026, 8, 15), "#E0A458", "PH-FQ");
        Phase("PH-WPN", "Weapon Integration", new(2026, 7, 1), new(2026, 9, 30), "#F25C54", "PH-ENV", "PH-AVI");
        Phase("PH-CERT", "Certification", new(2026, 9, 15), new(2026, 10, 15), "#C7456B", "PH-WPN");

        var noTarget = DateOnly.MaxValue;
        Campaign Camp(string id, string name, double tp, double per, DateOnly target, string group,
            string[] tails, string[] pilots, string[] engs, int eng = 2, int bps = 1, string location = "",
            bool eff = false, bool reh = false, int pace = 0, bool single = false, int support = 0,
            string[]? supportTails = null, string[]? prereq = null, string[]? prereqMaint = null) => new()
            {
                Id = id, Name = name, GroupId = group, LocationId = location,
                TotalTestPoints = tp, TestPointsPerSortie = per,
                StartDate = start, TargetEndDate = target,
                CompatibleAircraftIds = tails.ToList(), AssignedPilotIds = pilots.ToList(), AssignedEngineerIds = engs.ToList(),
                EngineersPerSortie = eng, BlocksPerSortie = bps, RehearsalRequired = reh,
                MinWorkdaysBetweenSorties = pace, PreferSingleAircraft = single,
                SupportAircraftPerSortie = support, SupportAircraftIds = (supportTails ?? Array.Empty<string>()).ToList(),
                PrerequisiteCampaignIds = (prereq ?? Array.Empty<string>()).ToList(),
                PrerequisiteMaintenanceIds = (prereqMaint ?? Array.Empty<string>()).ToList(),
                ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5, ExpectedRehearsalDutyHours = 2,
                TestEfficiencyEnabled = eff, RetestProbabilityStart = 0.25, RetestProbabilityFloor = 0.05,
                RetestLearningScale = 15, RetestCurve = RetestModel.Exponential, AbortProbability = 0.07,
            };

        sc.Campaigns.Add(Camp("FQ-FCF", "Functional check flights", 8, 2, new(2026, 2, 15), "PH-FQ", new[] { "TC-101", "TC-102" }, new[] { "P1", "P2" }, new[] { "E1", "E2" }, reh: true));
        sc.Campaigns.Add(Camp("FQ-LSH", "Low-speed handling", 48, 3, new(2026, 3, 20), "PH-FQ", new[] { "TC-101", "TC-102" }, new[] { "P1", "P2", "P3" }, new[] { "E1", "E2", "E3" }, prereq: new[] { "FQ-FCF" }));
        sc.Campaigns.Add(Camp("FQ-AOA", "High-AoA expansion", 36, 2, new(2026, 4, 20), "PH-FQ", new[] { "TC-101", "TC-102" }, new[] { "P1", "P3" }, new[] { "E2", "E3" }, eff: true, prereq: new[] { "FQ-LSH" }));
        sc.Campaigns.Add(Camp("FQ-PERF", "Performance & climb", 44, 4, new(2026, 4, 28), "PH-FQ", new[] { "TC-101", "TC-102" }, new[] { "P2", "P4" }, new[] { "E3", "E4" }));
        sc.Campaigns.Add(Camp("PR-RLT", "Engine relight envelope", 16, 2, new(2026, 5, 20), "PH-PROP", new[] { "TC-101" }, new[] { "P1", "P4" }, new[] { "E1", "E5" }));
        sc.Campaigns.Add(Camp("PR-ADC", "Air-data calibration", 18, 3, new(2026, 6, 12), "PH-PROP", new[] { "TC-102" }, new[] { "P2", "P3" }, new[] { "E2", "E5" }, pace: 2));
        sc.Campaigns.Add(Camp("AV-RDR", "Radar & EW", 30, 3, new(2026, 6, 30), "PH-AVI", new[] { "TC-101", "TC-102", "TC-401" }, new[] { "P3", "P4" }, new[] { "E6", "E7" }, reh: true));
        sc.Campaigns.Add(Camp("AV-NAV", "Navigation & datalink", 24, 3, new(2026, 7, 20), "PH-AVI", new[] { "TC-401" }, new[] { "P2", "P4" }, new[] { "E7", "E8" }, pace: 2));
        sc.Campaigns.Add(Camp("AV-EOIR", "EO/IR sensor (range)", 20, 2, new(2026, 7, 31), "PH-AVI", new[] { "TC-401" }, new[] { "P3", "P4" }, new[] { "E6", "E8" }, location: "KON"));
        sc.Campaigns.Add(Camp("EN-FLUT", "Flutter clearance", 48, 2, new(2026, 7, 31), "PH-ENV", new[] { "TC-201" }, new[] { "P5", "P6" }, new[] { "E5", "E6" }, bps: 3, single: true, eff: true));
        sc.Campaigns.Add(Camp("EN-LOADS", "Structural loads", 40, 2, new(2026, 8, 14), "PH-ENV", new[] { "TC-201", "TC-301" }, new[] { "P5", "P1" }, new[] { "E5", "E9" }, bps: 3));
        sc.Campaigns.Add(Camp("WP-CC", "Captive carry", 20, 2, noTarget, "PH-WPN", new[] { "TC-301", "TC-101" }, new[] { "P1", "P2" }, new[] { "E4", "E5" }, prereqMaint: new[] { "mw-301-modblock" }));
        sc.Campaigns.Add(Camp("WP-SEP", "Weapon separation", 28, 1, new(2026, 9, 28), "PH-WPN", new[] { "TC-301" }, new[] { "P1", "P2", "P3" }, new[] { "E4", "E5" }, location: "", eff: true, support: 1, supportTails: new[] { "TC-401" }));
        sc.Campaigns.Add(Camp("WP-GUN", "Gun firing", 22, 2, new(2026, 9, 25), "PH-WPN", new[] { "TC-301" }, new[] { "P2", "P4" }, new[] { "E5", "E9" }));
        sc.Campaigns.Add(Camp("WP-GND", "Weapon ground tests", 6, 2, noTarget, "PH-WPN", new[] { "TC-301" }, new[] { "P4" }, new[] { "E9", "E10" }));
        sc.Campaigns.Add(Camp("CT-COMP", "Certification compliance", 36, 4, new(2026, 10, 8), "PH-CERT", new[] { "TC-101", "TC-102" }, new[] { "P1", "P3" }, new[] { "E1", "E2" }));
        sc.Campaigns.Add(Camp("CT-ACC", "Customer acceptance demo", 14, 2, new(2026, 10, 14), "PH-CERT", new[] { "TC-101" }, new[] { "P1", "P2" }, new[] { "E1", "E2" }, reh: true, prereq: new[] { "CT-COMP" }));
        sc.Campaigns.Add(Camp("REL", "Reliability growth", 40, 2, new(2026, 10, 12), "", new[] { "TC-102", "TC-401" }, new[] { "P3", "P4" }, new[] { "E7", "E8" }, pace: 3));

        var session = new Session { Name = sc.Name, ActiveScenarioId = sc.Id };
        session.Scenarios.Add(sc);
        var path = Path.Combine(FindRepoRoot(), "samples", "one-year-program-demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, ScenarioJson.Options));

        Assert.Equal(new DateOnly(2026, 9, 30), CampaignDeadlines.EffectiveMilestone(sc, sc.Campaigns.First(c => c.Id == "WP-CC")));
        Assert.Equal(new DateOnly(2026, 9, 30), CampaignDeadlines.EffectiveMilestone(sc, sc.Campaigns.First(c => c.Id == "WP-GND")));
        Assert.Equal(new DateOnly(2026, 10, 12), CampaignDeadlines.EffectiveMilestone(sc, sc.Campaigns.First(c => c.Id == "REL")));

        var det = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(sc);
        Assert.NotEmpty(det.Sorties);
        Assert.Equal(6, ProgramReporter.Build(sc, det).Groups.Count);

        var wpn = new[] { "WP-CC", "WP-SEP", "WP-GUN", "WP-GND" };
        var upstream = new[] { "EN-FLUT", "EN-LOADS", "AV-RDR", "AV-NAV", "AV-EOIR" };
        var wpnSorties = det.Sorties.Where(s => wpn.Contains(s.CampaignId)).ToList();
        if (wpnSorties.Count > 0)
        {
            Assert.All(upstream, id => Assert.True(det.CampaignCompleted.GetValueOrDefault(id), $"{id} must complete before Weapon flies"));
            var lastUpstream = det.Sorties.Where(s => upstream.Contains(s.CampaignId)).Max(s => s.Date);
            Assert.True(wpnSorties.Min(s => s.Date) > lastUpstream, "Weapon sorties must follow the upstream phases (A↔A).");
        }

        var mc = MonteCarlo.Analyze(sc, runs: 40, seed: 7);
        Assert.InRange(mc.PDelivery, 0.0, 1.0);
        Assert.True(mc.SortiesMean > 0);

        var repo = new JsonRepository();
        repo.LoadSessionFrom(path);
        var loaded = repo.GetScenario();
        Assert.Equal(6, loaded.CampaignGroups.Count);
        Assert.Equal(18, loaded.Campaigns.Count);
        Assert.Contains("PH-ENV", loaded.CampaignGroups.First(g => g.Id == "PH-WPN").PrerequisiteGroupIds);
        Assert.Equal("PH-WPN", loaded.Campaigns.First(c => c.Id == "WP-SEP").GroupId);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MOIRAI.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
