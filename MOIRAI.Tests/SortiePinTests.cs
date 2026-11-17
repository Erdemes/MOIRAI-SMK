using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class SortiePinTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    public static IEnumerable<object[]> Engines() => new[]
    {
        new object[] { "greedy" }, new object[] { "advanced-off" }, new object[] { "smart" },
    };

    private static Schedule Run(string engine, Scenario s) => engine switch
    {
        "greedy" => new Scheduler().Run(s),
        "advanced-off" => new RevisingScheduler(RevisingOptions.Off).Run(s),
        _ => new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true,
        }).Run(s),
    };

    private static Scenario Scenario(int horizonDays = 40, int pilots = 3, int engineers = 3, int aircraft = 2)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= pilots; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= engineers; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        for (int i = 1; i <= aircraft; i++) s.Aircraft.Add(new Aircraft { Id = $"ac{i}", Name = $"Tail-{i}" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, string id, int sorties, int start = 0)
    {
        var c = new Campaign
        {
            Id = id, Name = id,
            TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start.AddDays(start), TargetEndDate = Start.AddDays(30),
            MaxSortiesPerDay = 1,
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    private static Sortie No(Schedule sch, string camp, int n) =>
        sch.Sorties.Single(x => x.CampaignId == camp && x.SortieNo == n);

    [Theory]
    [MemberData(nameof(Engines))]
    public void Pinned_sortie_flies_with_exactly_the_pinned_crew_and_tail(string engine)
    {
        var s = Scenario();
        Camp(s, "A", 6);
        Camp(s, "B", 6);
        s.SortiePins.Add(new SortiePin
        {
            CampaignId = "A", SortieNo = 3, AircraftId = "ac2",
            PilotIds = { "p3" }, EngineerIds = { "e3" },
        });

        var sch = Run(engine, s);

        var so = No(sch, "A", 3);
        Assert.Equal("ac2", so.AircraftId);
        Assert.Equal("p3", so.PilotId);
        Assert.Empty(so.AdditionalPilotIds);
        Assert.Equal(new[] { "e3" }, so.EngineerIds);
        Assert.True(sch.CampaignCompleted["A"]);
        Assert.True(sch.CampaignCompleted["B"]);
        var reh = sch.Rehearsals.Single(r => r.CampaignId == "A" && r.SortieNo == 3);
        Assert.Equal("p3", reh.PilotId);
        Assert.Equal(new[] { "e3" }, reh.EngineerIds);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Date_floats_and_the_campaign_waits_while_a_pinned_person_is_on_leave(string engine)
    {
        var s = Scenario();
        Camp(s, "A", 4);
        s.Pilots.Single(p => p.Id == "p2").LeaveWindows.Add(new DateRange { Start = Start, End = Start.AddDays(9) });
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 2, PilotIds = { "p2" } });

        var sch = Run(engine, s);

        var s2 = No(sch, "A", 2);
        Assert.Equal("p2", s2.PilotId);
        Assert.True(s2.Date >= Start.AddDays(10));
        Assert.True(No(sch, "A", 3).Date > s2.Date);
        Assert.True(sch.Bottlenecks["A"].Pinned > 0);
        Assert.Contains(DayLogCodes.Pinned, sch.DayLog["A"]);
        Assert.True(sch.CampaignCompleted["A"]);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Unresolvable_pin_is_never_released(string engine)
    {
        var s = Scenario();
        var a = Camp(s, "A", 4);
        a.AssignedPilotIds.Remove("p3");
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 2, PilotIds = { "p3" } });

        var sch = Run(engine, s);

        Assert.False(sch.CampaignCompleted["A"]);
        Assert.Single(sch.Sorties, x => x.CampaignId == "A");
        Assert.Equal("pinned resources", sch.Bottlenecks["A"].Dominant()?.Reason);
        Assert.Contains(ScenarioValidator.Validate(s),
            i => i.Category == "Pins" && i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Pinning_a_whole_plan_reproduces_its_crew_and_tails_after_a_change(string engine)
    {
        var s = Scenario();
        Camp(s, "A", 5);
        Camp(s, "B", 5, start: 2);
        var first = Run(engine, s);
        foreach (var so in first.Sorties) s.SortiePins.Add(SortiePin.From(so));

        Camp(s, "C", 4);
        var second = Run(engine, s);

        foreach (var pin in s.SortiePins)
        {
            var so = No(second, pin.CampaignId, pin.SortieNo);
            Assert.Equal(pin.AircraftId, so.AircraftId);
            Assert.Equal(pin.PilotIds, new[] { so.PilotId }.Concat(so.AdditionalPilotIds));
            Assert.Equal(pin.EngineerIds, so.EngineerIds);
        }
        Assert.True(second.CampaignCompleted["A"]);
        Assert.True(second.CampaignCompleted["B"]);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Simulator_session_pin_fixes_device_and_crew(string engine)
    {
        var s = Scenario();
        s.Simulators.Add(new Simulator { Id = "SIM2", Name = "Second sim" });
        s.Campaigns.Add(new Campaign
        {
            Id = "DEV", Name = "DEV", Venue = CampaignVenue.Simulator,
            TotalTestPoints = 4, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            AssignedPilotIds = { "p1", "p2" }, AssignedEngineerIds = { "e1", "e2" },
            PilotsPerSortie = 1, EngineersPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(30),
        });
        s.SortiePins.Add(new SortiePin
        {
            CampaignId = "DEV", SortieNo = 2, SimulatorId = "SIM2", PilotIds = { "p2" }, EngineerIds = { "e2" },
        });

        var sch = Run(engine, s);

        var ss = sch.SimulatorSorties.Single(x => x.CampaignId == "DEV" && x.SortieNo == 2);
        Assert.Equal("SIM2", ss.SimulatorId);
        Assert.Equal("p2", ss.PilotId);
        Assert.Equal(new[] { "e2" }, ss.EngineerIds);
        Assert.True(sch.CampaignCompleted["DEV"]);
    }

    [Fact]
    public void No_pins_means_a_pin_list_round_trips_and_changes_nothing()
    {
        var s = Scenario();
        Camp(s, "A", 5);
        var before = new Scheduler().Run(s);
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 99 });
        var after = new Scheduler().Run(s);
        Assert.Equal(
            before.Sorties.Select(x => (x.Date, x.Block, x.AircraftId, x.PilotId, string.Join(",", x.EngineerIds))),
            after.Sorties.Select(x => (x.Date, x.Block, x.AircraftId, x.PilotId, string.Join(",", x.EngineerIds))));

        var back = MOIRAI.Persistence.ScenarioJson.Clone(s);
        Assert.Single(back.SortiePins);
        Assert.Equal(99, back.SortiePins[0].SortieNo);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Date_pinned_sortie_flies_on_its_day_and_later_sorties_follow(string engine)
    {
        var s = Scenario();
        Camp(s, "A", 6);
        var pinDay = Start.AddDays(9);
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 3, Date = pinDay });

        var sch = Run(engine, s);
        Assert.Equal(pinDay, No(sch, "A", 3).Date);
        Assert.True(No(sch, "A", 2).Date < pinDay);
        for (int n = 4; n <= 6; n++) Assert.True(No(sch, "A", n).Date > No(sch, "A", n - 1).Date);
        Assert.True(sch.Bottlenecks["A"].Pinned > 0);
        Assert.DoesNotContain(sch.Diagnostics, d => d.Contains("pinned to"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Date_pin_wins_the_contested_resources_on_its_day(string engine)
    {
        var s = Scenario(pilots: 1, engineers: 1, aircraft: 1);
        var a = Camp(s, "A", 3);
        var b = Camp(s, "B", 10);
        b.TargetEndDate = Start.AddDays(12);
        a.TargetEndDate = Start.AddDays(30);
        var pinDay = Start.AddDays(2);
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 1, Date = pinDay });

        var sch = Run(engine, s);
        Assert.Equal(pinDay, No(sch, "A", 1).Date);
        Assert.DoesNotContain(sch.Sorties, x => x.CampaignId == "B" && x.Date == pinDay);
    }

    [Fact]
    public void Missed_pinned_day_flies_later_and_is_reported()
    {
        var s = Scenario(aircraft: 1);
        Camp(s, "A", 4);
        var pinDay = Start.AddDays(5);
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = pinDay, End = pinDay.AddDays(1) });
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 2, Date = pinDay });

        var sch = new Scheduler().Run(s);
        Assert.Equal(pinDay.AddDays(2), No(sch, "A", 2).Date);
        Assert.Contains(sch.Diagnostics, d => d.Contains("pinned to") && d.Contains("#2"));
    }

    [Fact]
    public void Date_only_pin_is_not_empty_and_round_trips()
    {
        var s = Scenario();
        Camp(s, "A", 3);
        s.SortiePins.Add(new SortiePin { CampaignId = "A", SortieNo = 2, Date = Start.AddDays(4) });
        Assert.False(s.SortiePins[0].IsEmpty);
        var back = MOIRAI.Persistence.ScenarioJson.Clone(s);
        Assert.Equal(Start.AddDays(4), back.SortiePins[0].Date);
    }
}
