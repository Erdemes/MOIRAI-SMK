using System.Text.Json;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using Xunit;

namespace MOIRAI.Tests;

public class IdleExplainerTests
{
    private static readonly DateOnly Start = new(2026, 1, 5);

    private static Scenario Scenario(int pilots = 2, int engineers = 2, int aircraft = 1, int days = 30)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = Start },
            HorizonStart = Start,
            HorizonEnd = Start.AddDays(days - 1),
        };
        for (int i = 1; i <= pilots; i++) s.Pilots.Add(new Pilot { Id = $"p{i}", Name = $"P{i}" });
        for (int i = 1; i <= engineers; i++) s.Engineers.Add(new Engineer { Id = $"e{i}", Name = $"E{i}" });
        for (int i = 1; i <= aircraft; i++) s.Aircraft.Add(new Aircraft { Id = $"ac{i}", Name = $"ac{i}" });
        return s;
    }

    private static Campaign Camp(Scenario s, string id, int sorties, int start = 0, int maxPerDay = 0)
    {
        var c = new Campaign
        {
            Id = id, Name = id,
            TotalTestPoints = sorties, TestPointsPerSortie = 1,
            StartDate = Start.AddDays(start), TargetEndDate = Start.AddDays(25),
            MaxSortiesPerDay = maxPerDay, RehearsalRequired = false,
        };
        c.CompatibleAircraftIds.AddRange(s.Aircraft.Select(a => a.Id));
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);
        return c;
    }

    private static char Code(IdleReport r, string id, int day) =>
        (r.PersonLog.GetValueOrDefault(id) ?? r.AircraftLog[id])[day];

    [Fact]
    public void Leave_and_not_yet_open_campaigns_are_explained()
    {
        var s = Scenario();
        Camp(s, "A", 6, start: 3, maxPerDay: 1);
        s.Pilots[1].LeaveWindows.Add(new DateRange { Start = Start.AddDays(4), End = Start.AddDays(5), Note = "annual" });

        var sch = new Scheduler().Run(s);
        var r = IdleReport.Build(s, sch);

        Assert.Equal(IdleCodes.NoWork, Code(r, "p1", 0));
        Assert.Contains(r.Explain("p1", Start)!.Details, l => l.Contains("opens"));
        Assert.Equal(IdleCodes.Leave, Code(r, "p2", 4));
        Assert.Contains("annual", r.Explain("p2", Start.AddDays(4))!.Headline);
        Assert.Equal(IdleCategory.Unavailable, r.Explain("p2", Start.AddDays(4))!.Category);
    }

    [Fact]
    public void A_daily_cap_leaves_the_second_pilot_not_needed()
    {
        var s = Scenario();
        Camp(s, "A", 6, maxPerDay: 1);

        var sch = new Scheduler().Run(s);
        Assert.Equal(DayLogCodes.DailyCap, sch.DayStopLog["A"][0]);
        var r = IdleReport.Build(s, sch);

        string idle = Code(r, "p1", 0) == IdleCodes.Worked ? "p2" : "p1";
        Assert.Equal(IdleCodes.NotNeeded, Code(r, idle, 0));
        var e = r.Explain(idle, Start)!;
        Assert.Equal(IdleCategory.EngineChoice, e.Category);
        Assert.Contains(e.Details, l => l.Contains("daily cap"));
        Assert.Equal(IdleCodes.Worked, Code(r, "ac1", 0));
        Assert.Contains("blocks free", r.Explain("ac1", Start)!.Headline);
    }

    [Fact]
    public void Rule19_wall_is_reported_as_an_ftom_limit()
    {
        var s = Scenario(pilots: 1, engineers: 1, days: 20);
        s.Config.MaxConsecutiveFlyingDays = 3;
        Camp(s, "A", 15);

        var sch = new Scheduler().Run(s);
        var r = IdleReport.Build(s, sch);

        int wall = Enumerable.Range(0, 20).First(i => Code(r, "p1", i) == IdleCodes.Ftom);
        Assert.Equal(3, wall);
        Assert.Contains("consecutive days 3/3", r.Explain("p1", Start.AddDays(wall))!.Headline);
        Assert.Equal(IdleCategory.Limits, r.Explain("p1", Start.AddDays(wall))!.Category);
        Assert.Equal(IdleCodes.Blocked, Code(r, "ac1", wall));
    }

    [Fact]
    public void Aircraft_maintenance_and_unused_tails_are_explained()
    {
        var s = Scenario(pilots: 1, engineers: 1, aircraft: 2);
        s.Config.MaxConsecutiveFlyingDays = 30;
        s.Aircraft[1].MaintenanceWindows.Add(new DateRange { Start = Start, End = Start.AddDays(2), Note = "200h" });
        Camp(s, "A", 8, maxPerDay: 1);

        var sch = new Scheduler().Run(s);
        var r = IdleReport.Build(s, sch);

        Assert.Equal(IdleCodes.Maintenance, Code(r, "ac2", 0));
        Assert.Contains("200h", r.Explain("ac2", Start)!.Headline);
        int day = Enumerable.Range(3, 5).First(i => Code(r, "ac2", i) != IdleCodes.Worked);
        Assert.Equal(IdleCodes.NotNeeded, Code(r, "ac2", day));
        int last = r.Days.Count - 1;
        Assert.Equal(IdleCodes.NoWork, Code(r, "ac1", last));
    }

    [Fact]
    public void Rest_stagger_days_are_recorded_and_reported_as_voluntary_rest()
    {
        var start = new DateOnly(2026, 1, 5);
        var s = new Scenario
        {
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 3 },
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = new RateBasedFlyableDays { Rate = 1, PatternLength = 1, Anchor = start },
            HorizonStart = start,
            HorizonEnd = start.AddDays(150),
        };
        for (int i = 0; i < 3; i++) s.Pilots.Add(new Pilot { Id = $"P{i}", Name = $"P{i}" });
        for (int i = 0; i < 3; i++) s.Engineers.Add(new Engineer { Id = $"E{i}", Name = $"E{i}" });
        s.Aircraft.Add(new Aircraft { Id = "T1", Name = "T1" });
        var c = new Campaign
        {
            Id = "C1", Name = "C1", TotalTestPoints = 90, TestPointsPerSortie = 1,
            StartDate = start, TargetEndDate = start.AddDays(45), RehearsalRequired = false,
            CompatibleAircraftIds = { "T1" },
        };
        c.AssignedPilotIds.AddRange(s.Pilots.Select(p => p.Id));
        c.AssignedEngineerIds.AddRange(s.Engineers.Select(e => e.Id));
        s.Campaigns.Add(c);

        var sch = new RevisingScheduler(new RevisingOptions { EnableRestStagger = true }).Run(s);
        Assert.NotEmpty(sch.VoluntaryRest);
        var r = IdleReport.Build(s, sch);

        var (pid, days) = sch.VoluntaryRest.First();
        var rested = days.First(d => r.PersonLog[pid][d.DayNumber - start.DayNumber] != IdleCodes.Worked);
        Assert.Equal(IdleCodes.VoluntaryRest, r.PersonLog[pid][rested.DayNumber - start.DayNumber]);
        Assert.Equal(IdleCategory.EngineChoice, r.Explain(pid, rested)!.Category);
    }

    [Fact]
    public void Stop_log_is_byte_identical_between_greedy_and_advanced_with_levers_off()
    {
        var s = Scenario(pilots: 2, engineers: 2, aircraft: 2);
        Camp(s, "A", 10, maxPerDay: 2);
        Camp(s, "B", 6, start: 2);
        var g = new Scheduler().Run(s);
        var a = new RevisingScheduler(RevisingOptions.Off).Run(s);
        Assert.Equal(JsonSerializer.Serialize(g.DayStopLog), JsonSerializer.Serialize(a.DayStopLog));
        Assert.Empty(g.VoluntaryRest);

        var back = JsonSerializer.Deserialize<Schedule>(JsonSerializer.Serialize(g, ScenarioJson.Options), ScenarioJson.Options)!;
        Assert.Equal(g.DayStopLog["A"], back.DayStopLog["A"]);
    }

    [Fact]
    public void Excel_export_carries_the_idle_matrix_and_detail_list()
    {
        var s = Scenario();
        Camp(s, "A", 6, maxPerDay: 1);
        var sch = new Scheduler().Run(s);
        string path = Path.Combine(Path.GetTempPath(), $"idle-{Guid.NewGuid():N}.xlsx");
        try
        {
            ExcelExporter.Export(s, sch, path);
            using var wb = new ClosedXML.Excel.XLWorkbook(path);
            var matrix = wb.Worksheet("Idle-Reasons");
            Assert.Equal("Resource", matrix.Cell(1, 1).GetString());
            var detail = wb.Worksheet("Idle-Detail");
            Assert.False(detail.Cell(2, 1).IsEmpty());
            Assert.Contains(detail.Column(5).CellsUsed(), c => c.GetString() == "Not needed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_rehearsal_day_is_work_not_idle()
    {
        var s = Scenario(pilots: 1, engineers: 1, days: 20);
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Sim" });
        var c = Camp(s, "A", 3, start: 2, maxPerDay: 1);
        c.RehearsalRequired = true;

        var sch = new Scheduler().Run(s);
        var reh = sch.Rehearsals.First(r => !sch.Sorties.Any(x => x.Date == r.Date));
        var r = IdleReport.Build(s, sch);

        int day = reh.Date.DayNumber - Start.DayNumber;
        Assert.Equal(IdleCodes.Worked, Code(r, "p1", day));
        Assert.StartsWith("Rehearsal only", r.Explain("p1", reh.Date)!.Headline);
        Assert.Equal(IdleCategory.None, r.Explain("p1", reh.Date)!.Category);
    }

    [Fact]
    public void Non_workdays_and_frozen_history_are_not_explained()
    {
        var s = Scenario();
        s.Calendar.WeekendDays = new() { DayOfWeek.Saturday, DayOfWeek.Sunday };
        Camp(s, "A", 4);
        s.FreezeDate = Start.AddDays(1);
        var r = IdleReport.Build(s, new Scheduler().Run(s));
        Assert.Equal(IdleCodes.NotRelevant, Code(r, "p1", 0));
        Assert.Equal(IdleCodes.NotRelevant, Code(r, "p1", 5));
        Assert.Null(r.Explain("p1", Start.AddDays(5)));
    }
}
