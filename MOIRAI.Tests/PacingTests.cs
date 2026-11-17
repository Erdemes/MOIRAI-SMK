using MOIRAI.Domain;
using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class PacingTests
{
    private static readonly DateOnly Start = new(2025, 6, 2);

    private static Scenario Scenario(int horizonDays, bool weekends = false,
        int pilots = 3, int engineers = 3)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = weekends
                ? new FlightCalendar()
                : new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1 },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(horizonDays - 1),
        };
        for (int i = 1; i <= pilots; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= engineers; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "ac1", Name = "Tail-1" });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim" });
        return s;
    }

    private static Campaign Camp(Scenario s, int requiredSorties,
        int reviewDays = 0, int maxPerDay = 0)
    {
        var c = new Campaign
        {
            Id = "c1",
            Name = "c1",
            TotalTestPoints = requiredSorties,
            TestPointsPerSortie = 1,
            StartDate = Start,
            TargetEndDate = Start.AddDays(90),
            MinWorkdaysBetweenSorties = reviewDays,
            MaxSortiesPerDay = maxPerDay,
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    [Fact]
    public void Review_gap_spaces_flying_days_by_working_days()
    {
        var s = Scenario(horizonDays: 30);
        var c = Camp(s, requiredSorties: 4, reviewDays: 2, maxPerDay: 1);

        var sch = new Scheduler().Run(s);

        var days = sch.Sorties.Select(x => x.Date).OrderBy(d => d).ToList();
        Assert.Equal(4, days.Count);
        for (int i = 1; i < days.Count; i++)
            Assert.True(days[i].DayNumber - days[i - 1].DayNumber >= 3,
                $"{days[i]} is too close to {days[i - 1]}");

        Assert.True(sch.Bottlenecks[c.Id].Review > 0);
        Assert.Contains(DayLogCodes.Review, sch.DayLog[c.Id]);
    }

    [Fact]
    public void Weekend_days_do_not_count_toward_the_review_gap()
    {
        var s = Scenario(horizonDays: 30, weekends: true);
        Camp(s, requiredSorties: 2, reviewDays: 2, maxPerDay: 1);

        var sch = new Scheduler().Run(s);
        var days = sch.Sorties.Select(x => x.Date).OrderBy(d => d).ToList();

        for (int i = 1; i < days.Count; i++)
        {
            int workdaysBetween = 0;
            for (var d = days[i - 1].AddDays(1); d < days[i]; d = d.AddDays(1))
                if (s.Calendar.IsWorkday(d)) workdaysBetween++;
            Assert.True(workdaysBetween >= 2,
                $"only {workdaysBetween} working days between {days[i - 1]} and {days[i]}");
        }
    }

    [Fact]
    public void Daily_cap_limits_sorties_per_day_without_a_gap()
    {
        var s = Scenario(horizonDays: 10);
        Camp(s, requiredSorties: 6, maxPerDay: 2);

        var sch = new Scheduler().Run(s);

        Assert.Equal(6, sch.Sorties.Count);
        foreach (var g in sch.Sorties.GroupBy(x => x.Date))
            Assert.True(g.Count() <= 2, $"{g.Count()} sorties on {g.Key}");
    }

    [Fact]
    public void Cap_and_gap_compose_two_sorties_per_flying_day()
    {
        var s = Scenario(horizonDays: 30);
        Camp(s, requiredSorties: 4, reviewDays: 1, maxPerDay: 2);

        var sch = new Scheduler().Run(s);
        var byDay = sch.Sorties.GroupBy(x => x.Date).OrderBy(g => g.Key).ToList();

        Assert.Equal(2, byDay.Count);
        Assert.All(byDay, g => Assert.Equal(2, g.Count()));
        Assert.True(byDay[1].Key.DayNumber - byDay[0].Key.DayNumber >= 2);
    }

    private static (Campaign A, Campaign B) PrereqPair(Scenario s, int postReviewDays)
    {
        var a = new Campaign
        {
            Id = "A", Name = "A", TotalTestPoints = 2, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(90),
            PostCompletionReviewWorkdays = postReviewDays,
        };
        var b = new Campaign
        {
            Id = "B", Name = "B", TotalTestPoints = 2, TestPointsPerSortie = 1,
            StartDate = Start, TargetEndDate = Start.AddDays(90),
            PrerequisiteCampaignIds = { "A" },
        };
        foreach (var c in new[] { a, b })
        {
            c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(x => x.Id));
            c.AssignedPilotIds.AddRange(s.Pilots.Select(x => x.Id));
            c.AssignedEngineerIds.AddRange(s.Engineers.Select(x => x.Id));
            s.Campaigns.Add(c);
        }
        return (a, b);
    }

    [Fact]
    public void Post_completion_review_delays_the_dependent_campaign()
    {
        var s = Scenario(horizonDays: 30);
        var (a, b) = PrereqPair(s, postReviewDays: 3);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["A"]);
        Assert.True(sch.CampaignCompleted["B"]);
        var aLast = sch.Sorties.Where(x => x.CampaignId == "A").Max(x => x.Date);
        var bFirst = sch.Sorties.Where(x => x.CampaignId == "B").Min(x => x.Date);
        Assert.True(bFirst >= aLast.AddDays(4),
            $"B flew {bFirst}, inside A's post-completion review after {aLast}");

        Assert.True(sch.Bottlenecks["B"].Review >= 3);
        Assert.Contains(DayLogCodes.Review, sch.DayLog["B"]);
    }

    [Fact]
    public void Post_completion_review_of_zero_keeps_the_day_after_behavior()
    {
        var s = Scenario(horizonDays: 30);
        PrereqPair(s, postReviewDays: 0);

        var sch = new Scheduler().Run(s);

        var aLast = sch.Sorties.Where(x => x.CampaignId == "A").Max(x => x.Date);
        var bFirst = sch.Sorties.Where(x => x.CampaignId == "B").Min(x => x.Date);
        Assert.Equal(aLast.AddDays(1), bFirst);
        Assert.Equal(0, sch.Bottlenecks["B"].Review);
    }

    [Fact]
    public void Post_completion_review_counts_working_days_only()
    {
        var s = Scenario(horizonDays: 40, weekends: true);
        var (a, _) = PrereqPair(s, postReviewDays: 3);

        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["B"]);
        var aLast = sch.Sorties.Where(x => x.CampaignId == "A").Max(x => x.Date);
        var bFirst = sch.Sorties.Where(x => x.CampaignId == "B").Min(x => x.Date);
        int workdaysBetween = 0;
        for (var d = aLast.AddDays(1); d < bFirst; d = d.AddDays(1))
            if (s.Calendar.IsWorkday(d)) workdaysBetween++;
        Assert.True(workdaysBetween >= a.PostCompletionReviewWorkdays,
            $"only {workdaysBetween} working days between A's end {aLast} and B's first sortie {bFirst}");
    }

    [Fact]
    public void Replayed_actuals_anchor_the_review_gap()
    {
        var s = Scenario(horizonDays: 30);
        var c = Camp(s, requiredSorties: 3, reviewDays: 3, maxPerDay: 1);
        s.ActualSorties.Add(new Sortie
        {
            CampaignId = c.Id,
            Date = Start.AddDays(4),
            Block = 1,
            SortieNo = 1,
            AircraftId = "ac1",
            PilotId = "p1",
            EngineerIds = new List<string> { "e1" },
            TestPointsFlown = 1,
            RehearsalDate = Start.AddDays(3),
        });
        s.FreezeDate = Start.AddDays(5);

        var sch = new Scheduler().Run(s);
        var firstPlanned = sch.Sorties.Where(x => !x.IsActual).Min(x => x.Date);

        Assert.True(firstPlanned >= Start.AddDays(8),
            $"first planned sortie {firstPlanned} ignores the review gap across the freeze");
    }
}
