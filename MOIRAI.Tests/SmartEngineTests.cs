using System.Text.Json;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class SmartEngineTests
{
    private static RevisingOptions Smart() => new()
    {
        EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
        EnableScarcityCrew = true, EnableBottleneckSteer = true,
    };

    private static (int Inc, int Late, int Mk) Score(Scenario sc, Schedule s)
    {
        int inc = 0, late = 0;
        foreach (var c in sc.Campaigns)
        {
            if (!s.CampaignCompleted.GetValueOrDefault(c.Id)) { inc++; continue; }
            if (s.CampaignEndDates.TryGetValue(c.Id, out var e) && e is { } end && end > c.TargetEndDate)
                late += end.DayNumber - c.TargetEndDate.DayNumber;
        }
        int mk = s.ProgramFinish is { } pf ? pf.DayNumber - sc.HorizonStart.DayNumber : 0;
        return (inc, late, mk);
    }

    private static int Cmp((int Inc, int Late, int Mk) a, (int Inc, int Late, int Mk) b)
    {
        if (a.Inc != b.Inc) return a.Inc - b.Inc;
        if (a.Late != b.Late) return a.Late - b.Late;
        return a.Mk - b.Mk;
    }

    private static string Json(Schedule sch)
    {
        foreach (var list in sch.AutoMaintenance.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.UnscheduledRepairs.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.SimulatorRepairs.Values) foreach (var w in list) w.Id = "";
        return JsonSerializer.Serialize(sch, ScenarioJson.Options);
    }

    [Fact]
    public void Smart_is_never_worse_than_greedy_and_sometimes_better()
    {
        int better = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var sc = Tight(seed);
            var g = Score(sc, new Scheduler().Run(sc));
            var sm = Score(sc, new RevisingScheduler(Smart()).Run(sc));
            int c = Cmp(sm, g);
            Assert.True(c <= 0, $"[seed {seed}] smart worse than greedy: smart={sm} greedy={g}");
            if (c < 0) better++;
        }
        Assert.True(better >= 5, $"smart should beat greedy on several contended scenarios; beat {better}/40");
    }

    [Fact]
    public void Smart_with_all_levers_off_is_byte_identical_to_greedy()
    {
        for (int seed = 0; seed < 10; seed++)
        {
            var sc = Tight(seed);
            var g = new Scheduler().Run(sc);
            var off = new RevisingScheduler(RevisingOptions.Off).Run(sc);
            Assert.True(Json(g) == Json(off), $"[seed {seed}] advanced-OFF diverged from greedy");
        }
    }

    private static Scenario Tight(int seed)
    {
        var rng = new Random(seed);
        var start = new DateOnly(2026, 1, 5);
        var sc = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() { DayOfWeek.Saturday, DayOfWeek.Sunday } },
            FlyableModel = new RateBasedFlyableDays { Rate = 0.7, PatternLength = 5, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(120),
        };
        int nPilots = 2 + rng.Next(2), nEng = 3 + rng.Next(2), nAc = 2 + rng.Next(2);
        for (int i = 0; i < nPilots; i++) sc.Pilots.Add(new Pilot { Id = $"P{i}", Name = $"P{i}" });
        for (int i = 0; i < nEng; i++) sc.Engineers.Add(new Engineer { Id = $"E{i}", Name = $"E{i}" });
        for (int i = 0; i < nAc; i++) sc.Aircraft.Add(new Aircraft { Id = $"T{i}", Name = $"T{i}" });
        int nC = 5 + rng.Next(4);
        var ids = new List<string>();
        for (int i = 0; i < nC; i++)
        {
            var c = new Campaign
            {
                Id = $"C{i}", Name = $"C{i}",
                TotalTestPoints = 4 + rng.Next(8), TestPointsPerSortie = 1,
                StartDate = start.AddDays(rng.Next(10)),
                TargetEndDate = start.AddDays(40 + rng.Next(70)),
                BlocksPerSortie = 1 + rng.Next(3),
                RehearsalRequired = rng.Next(2) == 0,
            };
            foreach (var a in sc.Aircraft) if (rng.Next(3) > 0) c.CompatibleAircraftIds.Add(a.Id);
            if (c.CompatibleAircraftIds.Count == 0) c.CompatibleAircraftIds.Add(sc.Aircraft[0].Id);
            foreach (var p in sc.Pilots) if (rng.Next(2) == 0) c.AssignedPilotIds.Add(p.Id);
            if (c.AssignedPilotIds.Count == 0) c.AssignedPilotIds.Add(sc.Pilots[0].Id);
            foreach (var e in sc.Engineers) if (rng.Next(2) == 0) c.AssignedEngineerIds.Add(e.Id);
            if (c.AssignedEngineerIds.Count == 0) c.AssignedEngineerIds.Add(sc.Engineers[0].Id);
            if (i > 0 && rng.Next(3) == 0) c.PrerequisiteCampaignIds.Add(ids[rng.Next(ids.Count)]);
            sc.Campaigns.Add(c);
            ids.Add(c.Id);
        }
        return sc;
    }
}
