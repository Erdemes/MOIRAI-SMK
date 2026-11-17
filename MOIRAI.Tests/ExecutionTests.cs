using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class ExecutionTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static FlightCalendar NoWeekend() => new() { WeekendDays = new() };
    private static FlyableDayModel AllFlyable() => new RateBasedFlyableDays { Rate = 1, PatternLength = 1 };

    private static Scenario Scenario(int horizonDays, int pilots = 2, int engineers = 2, int aircraft = 1)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = NoWeekend(),
            FlyableModel = AllFlyable(),
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= pilots; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= engineers; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        for (int i = 1; i <= aircraft; i++) s.Aircraft.Add(new Aircraft { Id = $"ac{i}", Name = $"Tail-{i}" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, string id, int requiredSorties)
    {
        var c = new Campaign
        {
            Id = id,
            Name = id,
            TotalTestPoints = requiredSorties,
            TestPointsPerSortie = 1,
            StartDate = Start,
            TargetEndDate = Start.AddDays(60),
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    private static Sortie Actual(Campaign c, DateOnly date, int sortieNo, double tp,
        bool aborted = false, string pilot = "p1", string engineer = "e1", string tail = "ac1") => new()
    {
        CampaignId = c.Id,
        Date = date,
        Block = 1,
        SortieNo = sortieNo,
        AircraftId = tail,
        PilotId = pilot,
        EngineerIds = new List<string> { engineer },
        TestPointsFlown = aborted ? 0 : tp,
        Aborted = aborted,
        RehearsalDate = date.AddDays(-1),
    };

    [Fact]
    public void Actuals_reduce_remaining_work_and_planning_starts_at_freeze_date()
    {
        var s = Scenario(horizonDays: 30);
        var c = Camp(s, "c1", requiredSorties: 6);

        s.ActualSorties.Add(Actual(c, Start, 1, 1));
        s.ActualSorties.Add(Actual(c, Start.AddDays(1), 2, 1));
        s.ActualSorties.Add(Actual(c, Start.AddDays(2), 3, 1));
        s.FreezeDate = Start.AddDays(5);

        var sch = new Scheduler().Run(s);

        Assert.Equal(6, sch.Sorties.Count);
        Assert.Equal(3, sch.Sorties.Count(x => x.IsActual));
        Assert.All(sch.Sorties.Where(x => !x.IsActual),
            x => Assert.True(x.Date >= s.FreezeDate, $"planned sortie on {x.Date} is before the freeze date"));
        Assert.True(sch.CampaignCompleted[c.Id]);
    }

    [Fact]
    public void Aborted_actual_consumes_the_sortie_but_yields_no_progress()
    {
        var s = Scenario(horizonDays: 30);
        var c = Camp(s, "c1", requiredSorties: 3);

        s.ActualSorties.Add(Actual(c, Start, 1, 1));
        s.ActualSorties.Add(Actual(c, Start.AddDays(1), 2, 1, aborted: true));
        s.FreezeDate = Start.AddDays(3);

        var sch = new Scheduler().Run(s);

        Assert.Equal(2, sch.Sorties.Count(x => !x.IsActual));
        Assert.True(sch.CampaignCompleted[c.Id]);
    }

    [Fact]
    public void Campaign_completed_by_actuals_alone_plans_nothing_new()
    {
        var s = Scenario(horizonDays: 30);
        var c = Camp(s, "c1", requiredSorties: 2);

        s.ActualSorties.Add(Actual(c, Start, 1, 1));
        s.ActualSorties.Add(Actual(c, Start.AddDays(1), 2, 1));
        s.FreezeDate = Start.AddDays(3);

        var sch = new Scheduler().Run(s);

        Assert.Equal(2, sch.Sorties.Count);
        Assert.All(sch.Sorties, x => Assert.True(x.IsActual));
        Assert.True(sch.CampaignCompleted[c.Id]);
        Assert.Equal(Start.AddDays(1), sch.CampaignEndDates[c.Id]);
    }

    [Fact]
    public void Replayed_hours_count_toward_FTOM_windows()
    {
        var s = Scenario(horizonDays: 20, pilots: 1, engineers: 1);
        var c = Camp(s, "c1", requiredSorties: 3);
        c.ExpectedSortieFlightHours = 4;
        s.Config.MaxFlightHoursPer7Days = 8;

        s.ActualSorties.Add(Actual(c, Start, 1, 1));
        s.ActualSorties.Add(Actual(c, Start.AddDays(1), 2, 1));
        s.FreezeDate = Start.AddDays(2);

        var sch = new Scheduler().Run(s);

        var firstPlanned = sch.Sorties.Where(x => !x.IsActual).Min(x => x.Date);
        Assert.True(firstPlanned >= Start.AddDays(7),
            $"first planned sortie {firstPlanned} ignores the replayed flight hours");
    }

    [Fact]
    public void New_rehearsals_never_land_before_the_freeze_date()
    {
        var s = Scenario(horizonDays: 30);
        var c = Camp(s, "c1", requiredSorties: 2);

        s.ActualSorties.Add(Actual(c, Start, 1, 1));
        s.FreezeDate = Start.AddDays(4);

        var sch = new Scheduler().Run(s);

        Assert.All(sch.Rehearsals.Where(r => !r.IsActual),
            r => Assert.True(r.Date >= s.FreezeDate, $"rehearsal on {r.Date} is before the freeze date"));
    }

    [Fact]
    public void Actual_hours_feed_the_periodic_maintenance_counters()
    {
        var s = Scenario(horizonDays: 30);
        s.Aircraft[0].PeriodicMaintenances.Add(new PeriodicMaintenanceRule
        {
            IntervalFlightHours = 5,
            DurationDays = 2,
        });
        var c = Camp(s, "c1", requiredSorties: 8);
        c.ExpectedSortieFlightHours = 3;

        s.ActualSorties.Add(Actual(c, Start, 1, 1));
        s.ActualSorties.Add(Actual(c, Start.AddDays(1), 2, 1));
        s.FreezeDate = Start.AddDays(2);

        var sch = new Scheduler().Run(s);

        var windows = sch.AutoMaintenance["ac1"];
        Assert.Contains(windows, w => w.Start == s.FreezeDate);
        Assert.DoesNotContain(sch.Sorties,
            x => !x.IsActual && x.Date >= s.FreezeDate && x.Date <= s.FreezeDate.Value.AddDays(1));
    }

    [Fact]
    public void Without_freeze_date_behavior_is_unchanged()
    {
        var s1 = Scenario(horizonDays: 20);
        Camp(s1, "c1", requiredSorties: 4);
        var s2 = Scenario(horizonDays: 20);
        Camp(s2, "c1", requiredSorties: 4);

        var a = new Scheduler().Run(s1);
        var b = new Scheduler().Run(s2);

        Assert.Equal(
            a.Sorties.Select(x => (x.Date, x.Block, x.AircraftId, x.PilotId)),
            b.Sorties.Select(x => (x.Date, x.Block, x.AircraftId, x.PilotId)));
    }
}
