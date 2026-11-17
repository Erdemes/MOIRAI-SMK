using System.Text.Json;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class CampaignCalendarTests
{
    private static readonly DateOnly Monday = new(2026, 1, 5);

    private static Scenario TwoTeams(bool aOwnWeek, bool aWorksHolidays = false)
    {
        var sc = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { Holidays = new() { Monday.AddDays(2) } },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Monday },
            HorizonStart = Monday,
            HorizonEnd = Monday.AddDays(30),
        };
        foreach (var n in new[] { "1", "2" })
        {
            sc.Pilots.Add(new Pilot { Id = "P" + n, Name = "P" + n });
            sc.Engineers.Add(new Engineer { Id = "E" + n, Name = "E" + n });
            sc.Aircraft.Add(new Aircraft { Id = "T" + n, Name = "T" + n });
        }
        Campaign Make(string id, string n) => new()
        {
            Id = id, Name = id,
            TotalTestPoints = 10, TestPointsPerSortie = 1, MaxSortiesPerDay = 1,
            StartDate = Monday, TargetEndDate = Monday.AddDays(25),
            RehearsalRequired = false,
            CompatibleAircraftIds = { "T" + n }, AssignedPilotIds = { "P" + n }, AssignedEngineerIds = { "E" + n },
        };
        var a = Make("A", "1");
        a.UseGlobalCalendar = !aOwnWeek;
        a.WeekendDays = new();
        a.WorkOnHolidays = aWorksHolidays;
        sc.Campaigns.Add(a);
        sc.Campaigns.Add(Make("B", "2"));
        return sc;
    }

    private static List<DateOnly> Days(Schedule s, string id) =>
        s.Sorties.Where(x => x.CampaignId == id).Select(x => x.Date).OrderBy(d => d).ToList();

    [Fact]
    public void Own_week_flies_weekends_while_the_global_campaign_does_not()
    {
        foreach (var sch in new[] { new Scheduler().Run(TwoTeams(true)), new RevisingScheduler().Run(TwoTeams(true)) })
        {
            var a = Days(sch, "A");
            var b = Days(sch, "B");
            Assert.Contains(a, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
            Assert.DoesNotContain(b, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
            Assert.DoesNotContain(Monday.AddDays(2), a);
            Assert.DoesNotContain(Monday.AddDays(2), b);
            Assert.True(sch.CampaignEndDates["A"] < sch.CampaignEndDates["B"]);
        }
    }

    [Fact]
    public void Work_on_holidays_opts_into_the_scenario_holidays()
    {
        var sch = new Scheduler().Run(TwoTeams(true, aWorksHolidays: true));
        Assert.Contains(Monday.AddDays(2), Days(sch, "A"));
        Assert.DoesNotContain(Monday.AddDays(2), Days(sch, "B"));
    }

    [Fact]
    public void Global_calendar_is_the_old_behavior_and_ignores_the_own_week()
    {
        var sc = TwoTeams(aOwnWeek: false);
        var sch = new Scheduler().Run(sc);
        Assert.DoesNotContain(Days(sch, "A"), d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(Days(sch, "A").Count, Days(sch, "B").Count);
    }

    [Fact]
    public void Calendar_override_round_trips_through_json()
    {
        var c = new Campaign
        {
            Id = "A", UseGlobalCalendar = false, WorkOnHolidays = true,
            WeekendDays = new() { DayOfWeek.Friday },
        };
        var back = JsonSerializer.Deserialize<Campaign>(JsonSerializer.Serialize(c))!;
        Assert.False(back.UseGlobalCalendar);
        Assert.True(back.WorkOnHolidays);
        Assert.Equal(new[] { DayOfWeek.Friday }, back.WeekendDays.ToArray());
        Assert.True(JsonSerializer.Deserialize<Campaign>("{\"Id\":\"X\"}")!.UseGlobalCalendar);
    }
}
