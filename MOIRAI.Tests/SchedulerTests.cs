using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class SchedulerTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    private static FlightCalendar NoWeekend() => new() { WeekendDays = new() };
    private static FlyableDayModel AllFlyable() => new RateBasedFlyableDays { Rate = 1, PatternLength = 1 };

    private static Scenario SimpleScenario(
        int horizonDays,
        Campaign camp,
        ScenarioConfig? cfg = null,
        FlyableDayModel? flyable = null)
    {
        return new Scenario
        {
            Pilots = { new Pilot { Id = "p1", Name = "Pilot1" } },
            Engineers = { new Engineer { Id = "e1", Name = "Eng1" } },
            Aircraft = { new Aircraft { Id = "ac1", Name = "Tail-1" } },
            Simulators = { new Simulator { Id = "SIM", Name = "Main sim" } },
            Campaigns = { camp },
            Config = cfg ?? new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = NoWeekend(),
            FlyableModel = flyable ?? AllFlyable(),
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
    }

    private static Campaign Camp(int requiredSorties, DateOnly? target = null) => new()
    {
        Id = "c1",
        Name = "Alpha",
        TotalTestPoints = requiredSorties,
        TestPointsPerSortie = 1,
        CompatibleAircraftIds = { "ac1" },
        AssignedPilotIds = { "p1" },
        AssignedEngineerIds = { "e1" },
        StartDate = Start,
        TargetEndDate = target ?? Start.AddDays(60),
    };

    private static int DaysFromStart(DateOnly d) => d.DayNumber - Start.DayNumber;

    [Fact]
    public void HappyPath_schedules_required_sorties_and_each_sortie_has_a_rehearsal()
    {
        var target = Start.AddDays(20);
        var camp = Camp(3, target);
        var s = SimpleScenario(horizonDays: 10, camp);

        var schedule = new Scheduler().Run(s);

        Assert.Equal(3, schedule.Sorties.Count);
        Assert.Equal(3, schedule.Rehearsals.Count);
        Assert.True(schedule.CampaignCompleted["c1"]);
        Assert.True(schedule.MeetsDeadline("c1", target));
        Assert.Empty(schedule.Diagnostics);

        foreach (var r in schedule.Rehearsals)
        {
            int gap = r.SortieDate.DayNumber - r.Date.DayNumber;
            Assert.InRange(gap, 1, s.Config.MaxRehearsalGapDays);
        }

        Assert.DoesNotContain(schedule.Sorties, so => so.Date == Start);

        foreach (var so in schedule.Sorties)
        {
            Assert.Equal("p1", so.PilotId);
            Assert.Single(so.EngineerIds);
            Assert.Equal(1, so.TestPointsFlown);
        }
    }

    [Fact]
    public void Rehearsal_is_not_subject_to_flyability_can_be_on_non_flyable_workday()
    {
        var camp = Camp(1, Start.AddDays(20));
        var flyable = new ExplicitFlyableDays { Days = { Start.AddDays(2) } };
        var s = SimpleScenario(horizonDays: 4, camp, flyable: flyable);

        var schedule = new Scheduler().Run(s);

        Assert.Single(schedule.Sorties);
        Assert.Equal(Start.AddDays(2), schedule.Sorties[0].Date);

        var reh = Assert.Single(schedule.Rehearsals);
        Assert.False(s.IsUsableDay(reh.Date));
    }

    [Fact]
    public void RehearsalGap_zero_no_sortie_can_be_scheduled()
    {
        var camp = Camp(2);
        var cfg = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, MaxRehearsalGapDays = 0 };
        var s = SimpleScenario(horizonDays: 10, camp, cfg);

        var schedule = new Scheduler().Run(s);

        Assert.Empty(schedule.Sorties);
        Assert.False(schedule.CampaignCompleted["c1"]);
        Assert.NotEmpty(schedule.Diagnostics);
    }

    [Fact]
    public void Flight_28day_cap_limits_the_sortie_count()
    {
        var camp = Camp(5);
        var cfg = new ScenarioConfig
        {
            PilotsPerSortie = 1,
            EngineersPerSortie = 1,
            MaxFlightHoursPer28Days = 2,
        };
        var s = SimpleScenario(horizonDays: 15, camp, cfg);

        var schedule = new Scheduler().Run(s);

        Assert.Equal(2, schedule.Sorties.Count);
        Assert.False(schedule.CampaignCompleted["c1"]);
        Assert.Contains(schedule.Diagnostics, d => d.Contains("Alpha"));
    }

    [Fact]
    public void RestRule_prevents_sorties_on_consecutive_days()
    {
        var camp = Camp(10);
        var cfg = new ScenarioConfig
        {
            PilotsPerSortie = 1,
            EngineersPerSortie = 1,
            MinRestHours = 21,
            MaxRehearsalGapDays = 2,
        };
        var s = SimpleScenario(horizonDays: 6, camp, cfg);

        var schedule = new Scheduler().Run(s);

        var sortieDays = schedule.Sorties.Select(so => DaysFromStart(so.Date)).OrderBy(x => x).ToList();

        for (int k = 1; k < sortieDays.Count; k++)
            Assert.True(sortieDays[k] - sortieDays[k - 1] >= 2);
    }

    [Fact]
    public void Sortie_cannot_be_scheduled_with_a_nonexistent_aircraft()
    {
        var camp = Camp(2);
        camp.CompatibleAircraftIds.Clear();
        camp.CompatibleAircraftIds.Add("nonexistent");
        var s = SimpleScenario(horizonDays: 10, camp);

        var schedule = new Scheduler().Run(s);

        Assert.Empty(schedule.Sorties);
        Assert.False(schedule.CampaignCompleted["c1"]);
        Assert.NotEmpty(schedule.Diagnostics);
    }

    [Fact]
    public void Campaign_does_not_start_until_its_prerequisite_is_complete()
    {
        var a = new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 2, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "ac1" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            StartDate = Start, TargetEndDate = Start.AddDays(60),
        };
        var b = new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 2, TestPointsPerSortie = 1,
            CompatibleAircraftIds = { "ac1" }, AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" },
            StartDate = Start, TargetEndDate = Start.AddDays(60),
            PrerequisiteCampaignIds = { "A" },
        };
        var s = new Scenario
        {
            Pilots = { new Pilot { Id = "p1", Name = "P1" } },
            Engineers = { new Engineer { Id = "e1", Name = "E1" } },
            Aircraft = { new Aircraft { Id = "ac1", Name = "Tail-1" } },
            Simulators = { new Simulator { Id = "SIM", Name = "Main sim" } },
            Campaigns = { a, b },
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = NoWeekend(),
            FlyableModel = AllFlyable(),
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(20),
        };

        var schedule = new Scheduler().Run(s);

        Assert.True(schedule.CampaignCompleted["A"]);
        Assert.True(schedule.CampaignCompleted["B"]);

        var aLast = schedule.Sorties.Where(x => x.CampaignId == "A").Max(x => x.Date);
        var bFirst = schedule.Sorties.Where(x => x.CampaignId == "B").Min(x => x.Date);
        Assert.True(bFirst > aLast, "B must not fly until A is complete");
    }

    [Fact]
    public void Campaign_does_not_fly_before_its_start_date()
    {
        var camp = Camp(2);
        camp.StartDate = Start.AddDays(5);
        var s = SimpleScenario(horizonDays: 12, camp);

        var schedule = new Scheduler().Run(s);

        Assert.NotEmpty(schedule.Sorties);
        Assert.All(schedule.Sorties, so => Assert.True(so.Date >= Start.AddDays(5)));
    }
}
