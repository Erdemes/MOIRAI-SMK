using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

public class CampaignGroupTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static Scenario NewScenario(int horizonDays) => new()
    {
        Config = new ScenarioConfig(),
        Calendar = new FlightCalendar { WeekendDays = new() },
        FlyableModel = new RateBasedFlyableDays { Rate = 1.0, PatternLength = 1, Anchor = Start },
        HorizonStart = Start,
        HorizonEnd = Start.AddDays(horizonDays),
    };

    private static Campaign Camp(string id, string tail, string pilot, string eng,
        double tp = 1, DateOnly? target = null, string group = "", int bps = 3) => new()
    {
        Id = id, Name = id, GroupId = group,
        TotalTestPoints = tp, TestPointsPerSortie = 1,
        CompatibleAircraftIds = { tail }, AssignedPilotIds = { pilot }, AssignedEngineerIds = { eng },
        EngineersPerSortie = 1, RehearsalRequired = false, BlocksPerSortie = bps,
        StartDate = Start, TargetEndDate = target ?? DateOnly.MaxValue,
        ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 4,
    };

    [Fact]
    public void Group_target_drives_ordering_for_a_child_with_no_own_target()
    {
        Scenario Build(DateOnly? groupTarget)
        {
            var sc = NewScenario(5);
            sc.Aircraft.Add(new Aircraft { Id = "TC", Name = "TC" });
            sc.Pilots.Add(new Pilot { Id = "PG", Name = "PG", ShortName = "PG" });
            sc.Pilots.Add(new Pilot { Id = "PU", Name = "PU", ShortName = "PU" });
            sc.Engineers.Add(new Engineer { Id = "EG", Name = "EG", ShortName = "EG" });
            sc.Engineers.Add(new Engineer { Id = "EU", Name = "EU", ShortName = "EU" });
            sc.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Phase", TargetEndDate = groupTarget });
            sc.Campaigns.Add(Camp("GROUPED", "TC", "PG", "EG", group: "G"));
            sc.Campaigns.Add(Camp("UNGROUPED", "TC", "PU", "EU", target: Start.AddDays(5)));
            return sc;
        }

        var withGroupTarget = new Scheduler().Run(Build(Start));
        Assert.Equal(Start, withGroupTarget.Sorties.Single(s => s.CampaignId == "GROUPED").Date);

        var noGroupTarget = new Scheduler().Run(Build(null));
        Assert.Equal(Start.AddDays(1), noGroupTarget.Sorties.Single(s => s.CampaignId == "GROUPED").Date);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Group_prerequisite_holds_a_child_until_the_prerequisite_groups_children_complete(bool advanced)
    {
        ISchedulerService engine = advanced
            ? new RevisingScheduler(new RevisingOptions())
            : new Scheduler();

        Scenario Build(bool withGroupPrereq)
        {
            var sc = NewScenario(8);
            sc.Aircraft.Add(new Aircraft { Id = "TC1", Name = "TC1" });
            sc.Aircraft.Add(new Aircraft { Id = "TC2", Name = "TC2" });
            sc.Pilots.Add(new Pilot { Id = "P1", Name = "P1", ShortName = "P1" });
            sc.Pilots.Add(new Pilot { Id = "P2", Name = "P2", ShortName = "P2" });
            sc.Engineers.Add(new Engineer { Id = "E1", Name = "E1", ShortName = "E1" });
            sc.Engineers.Add(new Engineer { Id = "E2", Name = "E2", ShortName = "E2" });
            sc.CampaignGroups.Add(new CampaignGroup { Id = "A1", Name = "Phase 1" });
            sc.CampaignGroups.Add(new CampaignGroup
            {
                Id = "A2", Name = "Phase 2",
                PrerequisiteGroupIds = withGroupPrereq ? new() { "A1" } : new(),
            });
            sc.Campaigns.Add(Camp("C1", "TC1", "P1", "E1", tp: 3, group: "A1"));
            sc.Campaigns.Add(Camp("C2", "TC2", "P2", "E2", group: "A2"));
            return sc;
        }

        var free = engine.Run(Build(false));
        Assert.Equal(Start, free.Sorties.Single(s => s.CampaignId == "C2").Date);

        var gated = engine.Run(Build(true));
        Assert.Equal(Start.AddDays(3), gated.Sorties.Single(s => s.CampaignId == "C2").Date);
        Assert.True(gated.Bottlenecks["C2"].Prereq >= 3);
    }

    [Fact]
    public void Group_rollup_is_met_when_every_child_meets_the_group_target()
    {
        var sc = NewScenario(5);
        sc.Aircraft.Add(new Aircraft { Id = "TA", Name = "TA" });
        sc.Aircraft.Add(new Aircraft { Id = "TB", Name = "TB" });
        sc.Pilots.Add(new Pilot { Id = "PA", Name = "PA", ShortName = "PA" });
        sc.Pilots.Add(new Pilot { Id = "PB", Name = "PB", ShortName = "PB" });
        sc.Engineers.Add(new Engineer { Id = "EA", Name = "EA", ShortName = "EA" });
        sc.Engineers.Add(new Engineer { Id = "EB", Name = "EB", ShortName = "EB" });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Phase", TargetEndDate = Start.AddDays(10) });
        sc.Campaigns.Add(Camp("CA", "TA", "PA", "EA", group: "G"));
        sc.Campaigns.Add(Camp("CB", "TB", "PB", "EB", group: "G"));

        var report = ProgramReporter.Build(sc, new Scheduler().Run(sc));
        var row = Assert.Single(report.Groups);
        Assert.Equal("G", row.GroupId);
        Assert.Equal(2, row.ChildCount);
        Assert.Equal(MilestoneStatus.Met, row.Status);
        Assert.Equal(Start, row.End);
    }

    [Fact]
    public void Group_rollup_is_missed_when_a_child_overshoots_the_group_target()
    {
        var sc = NewScenario(5);
        sc.Aircraft.Add(new Aircraft { Id = "TA", Name = "TA" });
        sc.Pilots.Add(new Pilot { Id = "PA", Name = "PA", ShortName = "PA" });
        sc.Engineers.Add(new Engineer { Id = "EA", Name = "EA", ShortName = "EA" });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Phase", TargetEndDate = Start.AddDays(1) });
        sc.Campaigns.Add(Camp("CA", "TA", "PA", "EA", tp: 3, group: "G"));

        var report = ProgramReporter.Build(sc, new Scheduler().Run(sc));
        var row = Assert.Single(report.Groups);
        Assert.Equal(MilestoneStatus.Missed, row.Status);
        Assert.Equal(Start.AddDays(2), row.End);
    }

    [Fact]
    public void Group_rollup_is_incomplete_until_all_children_complete()
    {
        var sc = NewScenario(2);
        sc.Aircraft.Add(new Aircraft { Id = "TA", Name = "TA" });
        sc.Pilots.Add(new Pilot { Id = "PA", Name = "PA", ShortName = "PA" });
        sc.Engineers.Add(new Engineer { Id = "EA", Name = "EA", ShortName = "EA" });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Phase", TargetEndDate = Start.AddDays(10) });
        sc.Campaigns.Add(Camp("CA", "TA", "PA", "EA", tp: 5, group: "G"));

        var report = ProgramReporter.Build(sc, new Scheduler().Run(sc));
        var row = Assert.Single(report.Groups);
        Assert.Equal(MilestoneStatus.Incomplete, row.Status);
        Assert.Null(row.End);
    }

    [Fact]
    public void Phase_target_is_inherited_by_the_status_of_a_child_with_no_own_target()
    {
        var sc = NewScenario(5);
        sc.Aircraft.Add(new Aircraft { Id = "T", Name = "T" });
        sc.Pilots.Add(new Pilot { Id = "P", Name = "P", ShortName = "P" });
        sc.Engineers.Add(new Engineer { Id = "E", Name = "E", ShortName = "E" });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Phase", TargetEndDate = Start.AddDays(1) });
        var child = Camp("C", "T", "P", "E", tp: 3, group: "G");

        sc.Campaigns.Add(child);
        var sch = new Scheduler().Run(sc);

        Assert.Equal(CampaignStatus.Late, sch.StatusOf(sc, child));

        sc.CampaignGroups[0].TargetEndDate = null;
        Assert.Equal(CampaignStatus.Met, sch.StatusOf(sc, child));
    }

    [Fact]
    public void EffectiveTarget_cascades_own_then_group_then_master()
    {
        var sc = NewScenario(30);
        sc.MasterDeadline = Start.AddDays(20);
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", TargetEndDate = Start.AddDays(10) });

        var ownTarget = Camp("OWN", "T", "P", "E", target: Start.AddDays(3), group: "G");
        var groupOnly = Camp("GRP", "T", "P", "E", group: "G");
        var ungrouped = Camp("UNG", "T", "P", "E");

        Assert.Equal(Start.AddDays(3), CampaignDeadlines.EffectiveTarget(sc, ownTarget));
        Assert.Equal(Start.AddDays(10), CampaignDeadlines.EffectiveTarget(sc, groupOnly));
        Assert.Equal(Start.AddDays(20), CampaignDeadlines.EffectiveTarget(sc, ungrouped));
    }

    [Fact]
    public void EffectiveMilestone_stops_at_the_group_and_never_falls_back_to_master()
    {
        var sc = NewScenario(30);
        sc.MasterDeadline = Start.AddDays(20);
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", TargetEndDate = Start.AddDays(10) });

        var groupOnly = Camp("GRP", "T", "P", "E", group: "G");
        var ungrouped = Camp("UNG", "T", "P", "E");

        Assert.Equal(Start.AddDays(10), CampaignDeadlines.EffectiveMilestone(sc, groupOnly));
        Assert.Null(CampaignDeadlines.EffectiveMilestone(sc, ungrouped));
    }

    [Fact]
    public void EffectivePrerequisites_expand_group_prerequisites_to_their_children()
    {
        var sc = NewScenario(10);
        sc.CampaignGroups.Add(new CampaignGroup { Id = "A1" });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "A2", PrerequisiteGroupIds = { "A1" } });
        var c1a = Camp("C1A", "T", "P", "E", group: "A1");
        var c1b = Camp("C1B", "T", "P", "E", group: "A1");
        var c2 = Camp("C2", "T", "P", "E", group: "A2");
        c2.PrerequisiteCampaignIds.Add("OWN-PREREQ");
        sc.Campaigns.AddRange(new[] { c1a, c1b, c2 });

        var eff = CampaignDeadlines.EffectivePrerequisiteCampaignIds(sc, c2);
        Assert.Contains("OWN-PREREQ", eff);
        Assert.Contains("C1A", eff);
        Assert.Contains("C1B", eff);

        Assert.Equal(c1a.PrerequisiteCampaignIds, CampaignDeadlines.EffectivePrerequisiteCampaignIds(sc, c1a));
    }

    [Fact]
    public void Validator_flags_group_prerequisite_cycles_and_bad_windows()
    {
        var sc = NewScenario(30);
        sc.MasterDeadline = Start.AddDays(10);
        sc.CampaignGroups.Add(new CampaignGroup { Id = "A1", Name = "Phase 1", PrerequisiteGroupIds = { "A2" } });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "A2", Name = "Phase 2", PrerequisiteGroupIds = { "A1" } });
        sc.CampaignGroups.Add(new CampaignGroup
        {
            Id = "A3", Name = "Phase 3",
            TargetStartDate = Start.AddDays(5), TargetEndDate = Start.AddDays(2),
        });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "A4", Name = "Phase 4", TargetEndDate = Start.AddDays(20) });

        var issues = ScenarioValidator.Validate(sc);
        var aLevel = issues.Where(i => i.Category == "A-Level").ToList();

        Assert.Contains(aLevel, i => i.Severity == IssueSeverity.Error && i.Message.Contains("cycle"));
        Assert.Contains(aLevel, i => i.Severity == IssueSeverity.Error && i.Message.Contains("ends before it starts"));
        Assert.Contains(aLevel, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("after the master deadline"));
    }

    [Fact]
    public void Validator_warns_when_a_child_target_is_after_its_phase_target()
    {
        var sc = NewScenario(30);
        sc.Aircraft.Add(new Aircraft { Id = "T", Name = "T" });
        sc.Pilots.Add(new Pilot { Id = "P", Name = "P", ShortName = "P" });
        sc.Engineers.Add(new Engineer { Id = "E", Name = "E", ShortName = "E" });
        sc.CampaignGroups.Add(new CampaignGroup { Id = "G", Name = "Phase", TargetEndDate = Start.AddDays(5) });
        sc.Campaigns.Add(Camp("C", "T", "P", "E", target: Start.AddDays(9), group: "G"));

        var issues = ScenarioValidator.Validate(sc);
        Assert.Contains(issues, i => i.Category == "A-Level" && i.Severity == IssueSeverity.Warning
            && i.Message.Contains("after its A-Level phase target"));
    }

    [Fact]
    public void Groups_and_group_ids_survive_a_serialization_round_trip()
    {
        var sc = NewScenario(10);
        sc.CampaignGroups.Add(new CampaignGroup
        {
            Id = "G", Name = "Phase", TargetStartDate = Start, TargetEndDate = Start.AddDays(5),
            PrerequisiteGroupIds = { "OTHER" }, Color = "#abcdef",
        });
        sc.Campaigns.Add(Camp("C", "T", "P", "E", group: "G"));

        var clone = ScenarioJson.Clone(sc);
        var g = Assert.Single(clone.CampaignGroups);
        Assert.Equal("Phase", g.Name);
        Assert.Equal(Start.AddDays(5), g.TargetEndDate);
        Assert.Equal("OTHER", Assert.Single(g.PrerequisiteGroupIds));
        Assert.Equal("#abcdef", g.Color);
        Assert.Equal("G", clone.Campaigns.Single().GroupId);
    }

    [Fact]
    public void Old_scenarios_with_no_groups_default_cleanly()
    {
        var sc = NewScenario(10);
        var c = Camp("C", "T", "P", "E", target: Start.AddDays(4));
        sc.Campaigns.Add(c);

        Assert.Empty(sc.CampaignGroups);
        Assert.Null(sc.GroupOf(c));
        Assert.Equal(Start.AddDays(4), CampaignDeadlines.EffectiveTarget(sc, c));
        Assert.Equal(Start.AddDays(4), CampaignDeadlines.EffectiveMilestone(sc, c));
    }
}

[Collection("Repository")]
public class CampaignGroupPersistenceTests
{
    [Fact]
    public void Cannot_delete_a_group_still_referenced_by_a_campaign()
    {
        var repo = new JsonRepository();
        repo.NewSession();
        repo.AddCampaignGroup(new CampaignGroup { Id = "G1", Name = "Phase 1" });
        repo.AddCampaignGroup(new CampaignGroup { Id = "G2", Name = "Phase 2", PrerequisiteGroupIds = { "G1" } });
        repo.AddCampaign(new Campaign { Id = "C", Name = "Child", GroupId = "G1" });

        var ex = Assert.Throws<System.InvalidOperationException>(() => repo.DeleteCampaignGroup("G1"));
        Assert.Contains("Child", ex.Message);

        repo.UpdateCampaign(new Campaign { Id = "C", Name = "Child", GroupId = "" });
        repo.DeleteCampaignGroup("G1");

        Assert.DoesNotContain(repo.GetCampaignGroups(), g => g.Id == "G1");
        Assert.Empty(repo.GetCampaignGroups().Single(g => g.Id == "G2").PrerequisiteGroupIds);
    }
}
