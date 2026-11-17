using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class ScenarioValidatorTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Scn()
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(120),
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "acA", Name = "A" });
        s.Aircraft.Add(new Aircraft { Id = "acB", Name = "B" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, string id = "c1", params string[] tails)
    {
        var c = new Campaign
        {
            Id = id, Name = id, TotalTestPoints = 5, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        };
        c.CompatibleAircraftIds.AddRange(tails.Length > 0 ? tails : new[] { "acA" });
        c.AssignedPilotIds.Add("p1");
        c.AssignedEngineerIds.Add("e1");
        s.Campaigns.Add(c);
        return c;
    }

    private static bool Has(IEnumerable<ValidationIssue> issues, string category, IssueSeverity sev) =>
        issues.Any(i => i.Category == category && i.Severity == sev);

    [Fact]
    public void Clean_scenario_has_no_issues()
    {
        var s = Scn();
        Camp(s);
        Assert.Empty(ScenarioValidator.Validate(s));
    }

    [Fact]
    public void No_compatible_aircraft_is_an_error()
    {
        var s = Scn();
        var c = Camp(s);
        c.CompatibleAircraftIds.Clear();
        Assert.True(Has(ScenarioValidator.Validate(s), "Aircraft", IssueSeverity.Error));
    }

    [Fact]
    public void Too_few_assigned_crew_is_an_error()
    {
        var s = Scn();
        var c = Camp(s);
        c.AssignedPilotIds.Clear();
        Assert.True(Has(ScenarioValidator.Validate(s), "Crew", IssueSeverity.Error));
    }

    [Fact]
    public void No_type_rated_crew_for_any_compatible_tail_is_an_error()
    {
        var s = Scn();
        var c = Camp(s, tails: "acA");
        s.Pilots[0].TypeRatings.Add("acB");
        var issues = ScenarioValidator.Validate(s);
        Assert.True(Has(issues, "Type ratings", IssueSeverity.Error));
    }

    [Fact]
    public void Target_before_start_warns()
    {
        var s = Scn();
        var c = Camp(s);
        c.TargetEndDate = c.StartDate.AddDays(-1);
        Assert.True(Has(ScenarioValidator.Validate(s), "Schedule", IssueSeverity.Warning));
    }

    [Fact]
    public void Unreachable_deadline_warns()
    {
        var s = Scn();
        var c = Camp(s);
        c.TotalTestPoints = 30; c.TestPointsPerSortie = 1;
        c.TargetEndDate = c.StartDate.AddDays(3);
        Assert.True(Has(ScenarioValidator.Validate(s), "Schedule", IssueSeverity.Warning));
    }

    [Fact]
    public void Prerequisite_cycle_is_an_error()
    {
        var s = Scn();
        var a = Camp(s, "a");
        var b = Camp(s, "b");
        a.PrerequisiteCampaignIds.Add("b");
        b.PrerequisiteCampaignIds.Add("a");
        Assert.True(Has(ScenarioValidator.Validate(s), "Prerequisites", IssueSeverity.Error));
    }

    [Fact]
    public void Blocks_per_sortie_exceeding_the_day_is_an_error()
    {
        var s = Scn();
        var c = Camp(s);
        c.BlocksPerSortie = 4;
        Assert.True(Has(ScenarioValidator.Validate(s), "Blocks", IssueSeverity.Error));
    }

    [Fact]
    public void Scope_limits_validation_to_selected_campaigns()
    {
        var s = Scn();
        Camp(s, "good");
        var bad = Camp(s, "bad");
        bad.CompatibleAircraftIds.Clear();

        var scoped = ScenarioValidator.Validate(s, new[] { "good" });
        Assert.Empty(scoped);
        Assert.NotEmpty(ScenarioValidator.Validate(s));
    }

    [Fact]
    public void All_pilots_on_leave_for_the_window_warns()
    {
        var s = Scn();
        var c = Camp(s);
        s.Pilots[0].LeaveWindows.Add(new DateRange { Start = c.StartDate, End = c.TargetEndDate });
        Assert.True(Has(ScenarioValidator.Validate(s), "Crew", IssueSeverity.Warning));
    }
}
