using System.Text.Json;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Tests;

public class EngineFuzzTests
{
    private static readonly DateOnly Origin = new(2026, 6, 1);

    private static Scenario Generate(int seed)
    {
        var rng = new Random(seed);
        int Next(int lo, int hi) => rng.Next(lo, hi + 1);
        bool Chance(double p) => rng.NextDouble() < p;

        int horizon = Next(20, 55);
        bool weekends = Chance(0.5);
        var cal = new FlightCalendar
        {
            WeekendDays = weekends ? new() { DayOfWeek.Saturday, DayOfWeek.Sunday } : new(),
        };
        if (Chance(0.4)) cal.Holidays.Add(Origin.AddDays(Next(2, horizon - 2)));

        var s = new Scenario
        {
            Config = new ScenarioConfig
            {
                BlocksPerDay = Next(1, 3),
                PilotsPerSortie = 1,
                EngineersPerSortie = Next(1, 2),
                MaxConsecutiveFlyingDays = Next(3, 6),
                LoadBalanceWindowDays = 7,
            },
            Calendar = cal,
            FlyableModel = new RateBasedFlyableDays { Rate = Math.Round(0.6 + rng.NextDouble() * 0.4, 2), PatternLength = Next(1, 5), Anchor = Origin },
            HorizonStart = Origin,
            HorizonEnd = Origin.AddDays(horizon - 1),
        };

        int nPilots = Next(2, 5), nEng = Next(3, 7), nAc = Next(2, 4);
        var tailIds = new List<string>();
        for (int a = 0; a < nAc; a++) tailIds.Add($"TC-{a:00}");

        for (int p = 0; p < nPilots; p++)
        {
            var pilot = new Pilot { Id = $"p{p}", Name = $"P{p}", ShortName = $"P{p}" };
            if (Chance(0.3)) pilot.LeaveWindows.Add(RandomWindow(rng, horizon, "leave"));
            if (Chance(0.25))
                pilot.TypeRatings.AddRange(tailIds.Where(_ => Chance(0.6)).DefaultIfEmpty(tailIds[0]));
            s.Pilots.Add(pilot);
        }
        for (int e = 0; e < nEng; e++)
        {
            var eng = new Engineer { Id = $"e{e}", Name = $"E{e}", ShortName = $"E{e}" };
            if (Chance(0.25)) eng.LeaveWindows.Add(RandomWindow(rng, horizon, "leave"));
            s.Engineers.Add(eng);
        }
        foreach (var id in tailIds)
        {
            var ac = new Aircraft { Id = id, Name = id };
            if (Chance(0.3)) ac.MaintenanceWindows.Add(RandomWindow(rng, horizon, "maint"));
            if (Chance(0.15)) ac.PeriodicMaintenances.Add(new PeriodicMaintenanceRule { IntervalFlightHours = Next(4, 10), DurationDays = Next(1, 3) });
            s.Aircraft.Add(ac);
        }

        string? locId = null;
        if (Chance(0.35))
        {
            locId = "rng";
            s.Locations.Add(new Location
            {
                Id = locId, Name = "Range",
                FerryDays = Next(1, 3), SetupDays = Next(0, 2), TeardownDays = Next(0, 2),
                FerryFlightHours = 2, FerryDutyHours = 6,
                BlocksPerDayCap = Chance(0.3) ? Next(1, s.Config.BlocksPerDay) : null,
                PostDeploymentRestDays = Chance(0.3) ? Next(1, 5) : 0,
            });
            if (Chance(0.5))
                s.Simulators.Add(new Simulator
                {
                    Id = "SIM-LOC", Name = "Range sim", LocationId = locId, MaxBlocksPerDay = Next(0, 2),
                });
        }
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Sim", LocationId = "", MaxBlocksPerDay = Next(0, 2) });

        string? extId = null;
        if (Chance(0.3))
        {
            extId = "EXT";
            s.ExternalSupports.Add(new ExternalSupport
            {
                Id = extId, Name = "External chase",
                AvailabilityModel = Chance(0.6)
                    ? new RateBasedFlyableDays { Rate = 0.5 + Next(0, 4) * 0.1, PatternLength = Next(3, 7), Anchor = Origin }
                    : null,
                MaxSortiesPerDay = Chance(0.4) ? Next(1, 2) : 0,
            });
        }

        int nCamp = Next(2, 5);
        var campIds = new List<string>();
        for (int c = 0; c < nCamp; c++)
        {
            var pilots = s.Pilots.Where(_ => Chance(0.6)).Select(p => p.Id).DefaultIfEmpty(s.Pilots[c % nPilots].Id).Distinct().ToList();
            var engs = s.Engineers.Where(_ => Chance(0.6)).Select(e => e.Id).DefaultIfEmpty(s.Engineers[c % nEng].Id).Distinct().ToList();
            var compat = tailIds.Where(_ => Chance(0.6)).DefaultIfEmpty(tailIds[c % nAc]).Distinct().ToList();
            int bps = Next(1, s.Config.BlocksPerDay);
            var camp = new Campaign
            {
                Id = $"C{c}", Name = $"C{c}",
                TotalTestPoints = Next(3, 24), TestPointsPerSortie = Next(1, 4),
                StartDate = Origin.AddDays(Next(0, 3)),
                TargetEndDate = Origin.AddDays(Next(horizon / 2, horizon - 1)),
                CompatibleAircraftIds = compat,
                AssignedPilotIds = pilots,
                AssignedEngineerIds = engs,
                EngineersPerSortie = Math.Min(engs.Count, Next(1, 2)),
                BlocksPerSortie = bps,
                MinWorkdaysBetweenSorties = Chance(0.3) ? Next(1, 3) : 0,
                MaxSortiesPerDay = Chance(0.3) ? Next(1, 2) : 0,
                PostCompletionReviewWorkdays = Chance(0.25) ? Next(1, 4) : 0,
                RestDaysAfterCompletion = Chance(0.25) ? Next(1, 7) : 0,
                PreferSingleAircraft = Chance(0.2),
                RehearsalRequired = Chance(0.5),
                LocationId = locId is not null && Chance(0.4) ? locId : "",
                ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5, ExpectedRehearsalDutyHours = 2,
            };
            if (Chance(0.2) && bps < s.Config.BlocksPerDay)
                camp.AllowedBlocks.Add(Next(1, s.Config.BlocksPerDay - bps + 1));
            if (c > 0 && Chance(0.25)) camp.PrerequisiteCampaignIds.Add(campIds[Next(0, c - 1)]);
            if (extId is not null && Chance(0.3)) camp.RequiredExternalSupportIds.Add(extId);
            if (camp.LocationId.Length == 0 && Chance(0.15) && nAc >= 2)
            {
                camp.SupportAircraftPerSortie = 1;
                camp.SupportAircraftIds = tailIds.Where(t => !compat.Contains(t)).DefaultIfEmpty(tailIds[(c + 1) % nAc]).Take(1).ToList();
            }
            if (Chance(0.2))
            {
                camp.Venue = CampaignVenue.Simulator;
                camp.CompatibleAircraftIds = new();
                camp.SupportAircraftPerSortie = 0;
                camp.SupportAircraftIds = new();
                camp.PreferSingleAircraft = false;
                camp.RehearsalRequired = false;
                camp.LocationId = "";
                camp.PilotsPerSortie = Chance(0.5) ? 0 : 1;
            }
            s.Campaigns.Add(camp);
            campIds.Add(camp.Id);
        }
        return s;
    }

    private static DateRange RandomWindow(Random rng, int horizon, string note)
    {
        int start = rng.Next(0, horizon - 1);
        int len = rng.Next(1, Math.Min(6, horizon - start));
        return new DateRange { Id = $"{note}-{start}-{len}-{rng.Next(100000)}", Start = Origin.AddDays(start), End = Origin.AddDays(start + len - 1), Note = note };
    }

    private static string Json(Schedule sch)
    {
        foreach (var list in sch.AutoMaintenance.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.UnscheduledRepairs.Values) foreach (var w in list) w.Id = "";
        foreach (var list in sch.SimulatorRepairs.Values) foreach (var w in list) w.Id = "";
        return JsonSerializer.Serialize(sch, ScenarioJson.Options);
    }

    private static (int Incomplete, int Missed, int Lateness, int Makespan) Score(Scenario s, Schedule sch)
    {
        int inc = 0, miss = 0, late = 0;
        foreach (var c in s.Campaigns)
        {
            if (!sch.CampaignCompleted.GetValueOrDefault(c.Id)) { inc++; continue; }
            if (c.TargetEndDate != DateOnly.MaxValue && sch.CampaignEndDates.TryGetValue(c.Id, out var end) && end is { } e && e > c.TargetEndDate)
            { miss++; late += e.DayNumber - c.TargetEndDate.DayNumber; }
        }
        int mk = sch.ProgramFinish is { } pf ? Math.Max(0, pf.DayNumber - s.HorizonStart.DayNumber) : 0;
        return (inc, miss, late, mk);
    }

    private static bool BetterOrEqual((int, int, int, int) a, (int, int, int, int) b) =>
        Comparer<(int, int, int, int)>.Default.Compare(a, b) <= 0;

    private static void AssertSane(int seed, string engine, Scenario s, Schedule sch)
    {
        string Tag(string msg) => $"[seed {seed}/{engine}] {msg}";
        var allLeave = s.Pilots.Cast<Resource>().Concat(s.Engineers).ToDictionary(r => r.Id, r => r.LeaveWindows);
        var rated = s.Pilots.Cast<Resource>().Concat(s.Engineers).ToDictionary(r => r.Id, r => r.TypeRatings);
        var planned = s.Aircraft.ToDictionary(a => a.Id, a => a.MaintenanceWindows);
        var campById = s.Campaigns.ToDictionary(c => c.Id);
        int bpd = Math.Max(1, s.Config.BlocksPerDay);
        int Bps(string campId) => campById.TryGetValue(campId, out var c) ? Math.Clamp(c.BlocksPerSortie, 1, bpd) : 1;

        foreach (var so in sch.Sorties.Where(x => !x.IsActual))
        {
            foreach (var tail in so.SupportAircraftIds.Prepend(so.AircraftId))
            {
                if (planned.TryGetValue(tail, out var pw))
                    Assert.False(pw.Any(w => w.Contains(so.Date)), Tag($"{tail} flew {so.Date:yyyy-MM-dd} in planned maintenance"));
                if (sch.AutoMaintenance.TryGetValue(tail, out var aw))
                    Assert.False(aw.Any(w => w.Contains(so.Date)), Tag($"{tail} flew {so.Date:yyyy-MM-dd} during auto-maintenance"));
                if (sch.UnscheduledRepairs.TryGetValue(tail, out var rw))
                    Assert.False(rw.Any(w => w.Contains(so.Date)), Tag($"{tail} flew {so.Date:yyyy-MM-dd} during an unscheduled repair"));
            }
            foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.SupportPilotIds).Concat(so.EngineerIds))
                if (allLeave.TryGetValue(pid, out var lw))
                    Assert.False(lw.Any(w => w.Contains(so.Date)), Tag($"{pid} flew on leave {so.Date:yyyy-MM-dd}"));

            bool deployed = campById.TryGetValue(so.CampaignId, out var cc) && cc.LocationId.Length > 0;
            Assert.True(s.Calendar.IsWorkday(so.Date), Tag($"{so.CampaignId} flew {so.Date:yyyy-MM-dd}, not a workday"));
            if (!deployed)
                Assert.True(s.IsUsableDay(so.Date), Tag($"main-base {so.CampaignId} flew {so.Date:yyyy-MM-dd}, not usable"));

            foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.EngineerIds))
                if (rated.TryGetValue(pid, out var tr) && tr.Count > 0)
                    Assert.True(tr.Contains(so.AircraftId), Tag($"{pid} not type-rated on {so.AircraftId}"));
        }

        foreach (var grp in sch.Sorties.GroupBy(x => x.CampaignId))
        {
            var nos = grp.Select(x => x.SortieNo).OrderBy(n => n).ToList();
            Assert.Equal(Enumerable.Range(1, nos.Count), nos);
        }

        foreach (var grp in sch.Sorties.GroupBy(x => x.CampaignId))
            if (campById.TryGetValue(grp.Key, out var c) && c.MaxSortiesPerDay > 0)
                foreach (var day in grp.GroupBy(x => x.Date))
                    Assert.True(day.Count() <= c.MaxSortiesPerDay,
                        Tag($"{grp.Key} flew {day.Count()} sorties on {day.Key:yyyy-MM-dd} > cap {c.MaxSortiesPerDay}"));

        var occ = new Dictionary<(string, DateOnly, int), string>();
        void Occupy(string person, DateOnly date, int block, string what)
        {
            if (occ.TryGetValue((person, date, block), out var prev))
                Assert.Fail(Tag($"{person} double-booked block {block} on {date:yyyy-MM-dd}: {prev} + {what}"));
            occ[(person, date, block)] = what;
        }
        foreach (var so in sch.Sorties)
            foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.SupportPilotIds).Concat(so.EngineerIds))
                Occupy(pid, so.Date, so.Block, $"sortie {so.CampaignId}#{so.SortieNo}");
        foreach (var r in sch.Rehearsals)
            foreach (var pid in r.EngineerIds.Prepend(r.PilotId))
                Occupy(pid, r.Date, r.Block, $"rehearsal {r.CampaignId}#{r.SortieNo}");
        foreach (var ss in sch.SimulatorSorties)
        {
            var simCrew = ss.AdditionalPilotIds.Concat(ss.EngineerIds);
            if (ss.PilotId.Length > 0) simCrew = simCrew.Prepend(ss.PilotId);
            foreach (var pid in simCrew)
                for (int b = 0; b < Bps(ss.CampaignId); b++)
                    Occupy(pid, ss.Date, ss.Block + b, $"sim {ss.CampaignId}#{ss.SortieNo}");
        }

        var spans = new Dictionary<(string, DateOnly), List<(int Lo, int Hi, string What)>>();
        void AddSpan(string tail, DateOnly date, int blk, int bps, string what)
        {
            if (!spans.TryGetValue((tail, date), out var list)) spans[(tail, date)] = list = new();
            int lo = blk, hi = blk + bps - 1;
            foreach (var (l, h, w) in list)
                if (lo <= h && l <= hi) Assert.Fail(Tag($"{tail} block overlap on {date:yyyy-MM-dd}: {w} + {what}"));
            list.Add((lo, hi, what));
        }
        foreach (var so in sch.Sorties)
        {
            int bps = Bps(so.CampaignId);
            AddSpan(so.AircraftId, so.Date, so.Block, bps, $"{so.CampaignId}#{so.SortieNo}");
            foreach (var st in so.SupportAircraftIds) AddSpan(st, so.Date, so.Block, bps, $"{so.CampaignId}#{so.SortieNo} support");
        }
        foreach (var ss in sch.SimulatorSorties)
            AddSpan(ss.SimulatorId, ss.Date, ss.Block, Bps(ss.CampaignId), $"sim {ss.CampaignId}#{ss.SortieNo}");

        foreach (var r in sch.Rehearsals.Where(x => !x.IsActual))
        {
            Assert.True(r.Date < r.SortieDate, Tag($"rehearsal {r.Date:yyyy-MM-dd} not before sortie {r.SortieDate:yyyy-MM-dd}"));
            if (s.Config.RehearsalOnWorkdaysOnly)
                Assert.True(s.Calendar.IsWorkday(r.Date), Tag($"rehearsal on non-workday {r.Date:yyyy-MM-dd}"));
            foreach (var pid in r.EngineerIds.Prepend(r.PilotId))
                if (allLeave.TryGetValue(pid, out var lw))
                    Assert.False(lw.Any(w => w.Contains(r.Date)), Tag($"{pid} rehearsed on leave {r.Date:yyyy-MM-dd}"));
        }

        foreach (var grp in sch.Sorties.GroupBy(x => x.CampaignId))
            if (campById.TryGetValue(grp.Key, out var c) && c.MinWorkdaysBetweenSorties > 0)
            {
                var days = grp.Select(x => x.Date).Distinct().OrderBy(d => d).ToList();
                for (int i = 1; i < days.Count; i++)
                {
                    int between = 0;
                    for (var d = days[i - 1].AddDays(1); d < days[i]; d = d.AddDays(1))
                        if (s.Calendar.IsWorkday(d)) between++;
                    Assert.True(between >= c.MinWorkdaysBetweenSorties,
                        Tag($"{grp.Key} flew {days[i - 1]:MM-dd}→{days[i]:MM-dd} with {between} < {c.MinWorkdaysBetweenSorties} review workdays"));
                }
            }

        if (engine.StartsWith("greedy"))
            foreach (var grp in sch.Sorties.Where(x => !x.IsActual).GroupBy(x => x.CampaignId))
                if (campById.TryGetValue(grp.Key, out var c))
                    foreach (var pid in c.PrerequisiteCampaignIds)
                    {
                        var prereq = s.Campaigns.FirstOrDefault(x => x.Id == pid);
                        if (prereq is null || prereq.PostCompletionReviewWorkdays <= 0) continue;
                        if (!sch.CampaignEndDates.TryGetValue(pid, out var pend) || pend is not { } pe) continue;
                        var first = grp.Min(x => x.Date);
                        int between = 0;
                        for (var d = pe.AddDays(1); d < first; d = d.AddDays(1))
                            if (s.Calendar.IsWorkday(d)) between++;
                        Assert.True(first > pe && between >= prereq.PostCompletionReviewWorkdays,
                            Tag($"{grp.Key} flew {first:MM-dd} with {between} < {prereq.PostCompletionReviewWorkdays} post-review workdays after {pid} ended {pe:MM-dd}"));
                    }

        var extById = s.ExternalSupports.ToDictionary(a => a.Id);
        var extUse = new Dictionary<(string Id, DateOnly Day, int Block), int>();
        foreach (var so in sch.Sorties.Where(x => !x.IsActual))
        {
            if (!campById.TryGetValue(so.CampaignId, out var c) || c.Venue == CampaignVenue.Simulator) continue;
            Assert.Equal(
                c.RequiredExternalSupportIds.Distinct().OrderBy(x => x),
                so.ExternalSupportIds.OrderBy(x => x));
            int bps = Bps(so.CampaignId);
            foreach (var id in so.ExternalSupportIds)
            {
                Assert.True(extById.TryGetValue(id, out var asset), Tag($"unknown external asset {id}"));
                Assert.True(asset!.AvailabilityModel?.IsFlyable(so.Date) ?? true,
                    Tag($"{so.CampaignId} flew {so.Date:MM-dd} with external {id} not allocated"));
                for (int b = so.Block; b < so.Block + bps; b++)
                {
                    var key = (id, so.Date, b);
                    int n2 = extUse[key] = extUse.GetValueOrDefault(key) + 1;
                    Assert.True(n2 <= 1, Tag($"external {id} double-booked on {so.Date:MM-dd} block {b}"));
                }
            }
        }
        foreach (var a in s.ExternalSupports.Where(x => x.MaxSortiesPerDay > 0))
            foreach (var grp in sch.Sorties.Where(x => !x.IsActual && x.ExternalSupportIds.Contains(a.Id)).GroupBy(x => x.Date))
                Assert.True(grp.Count() <= a.MaxSortiesPerDay,
                    Tag($"external {a.Id} supported {grp.Count()} > cap {a.MaxSortiesPerDay} sorties on {grp.Key:MM-dd}"));

        var finishDates = sch.Sorties.Select(x => x.Date).Concat(sch.SimulatorSorties.Select(x => x.Date)).ToList();
        var latest = finishDates.Count > 0 ? finishDates.Max() : (DateOnly?)null;
        Assert.Equal(latest, sch.ProgramFinish);

        foreach (var c in s.Campaigns)
        {
            bool done = sch.CampaignCompleted.GetValueOrDefault(c.Id);
            var end = sch.CampaignEndDates.GetValueOrDefault(c.Id);
            var flewDays = sch.Sorties.Where(x => x.CampaignId == c.Id).Select(x => x.Date)
                .Concat(sch.SimulatorSorties.Where(x => x.CampaignId == c.Id).Select(x => x.Date))
                .ToHashSet();
            if (done && flewDays.Count > 0)
            {
                Assert.True(end is not null, Tag($"{c.Id} completed but has no end date"));
                Assert.True(flewDays.Contains(end!.Value), Tag($"{c.Id} end date {end:yyyy-MM-dd} is not a day it flew"));
            }
            if (!done) Assert.True(end is null, Tag($"{c.Id} incomplete but carries end date {end:yyyy-MM-dd}"));
        }

        foreach (var c in s.Campaigns.Where(x => x.RestDaysAfterCompletion > 0))
        {
            if (!sch.CampaignCompleted.GetValueOrDefault(c.Id)) continue;
            if (sch.CampaignEndDates.GetValueOrDefault(c.Id) is not { } end) continue;
            bool InWindow(DateOnly d) =>
                d > end && d.DayNumber - end.DayNumber <= c.RestDaysAfterCompletion;

            var crew = c.AssignedPilotIds.Concat(c.AssignedEngineerIds).ToHashSet();
            var tails = c.CompatibleAircraftIds.Concat(c.SupportAircraftIds).ToHashSet();

            foreach (var so in sch.Sorties.Where(x => !x.IsActual && InWindow(x.Date)))
            {
                foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.SupportPilotIds).Concat(so.EngineerIds))
                    Assert.False(crew.Contains(pid),
                        Tag($"{pid} flew {so.Date:yyyy-MM-dd} inside {c.Id}'s rest window (end {end:yyyy-MM-dd} +{c.RestDaysAfterCompletion}d)"));
                foreach (var tail in so.SupportAircraftIds.Prepend(so.AircraftId))
                    Assert.False(tails.Contains(tail),
                        Tag($"{tail} flew {so.Date:yyyy-MM-dd} inside {c.Id}'s rest window (end {end:yyyy-MM-dd} +{c.RestDaysAfterCompletion}d)"));
            }
            foreach (var r in sch.Rehearsals.Where(x => !x.IsActual && InWindow(x.Date)))
                foreach (var pid in r.EngineerIds.Prepend(r.PilotId))
                    Assert.False(crew.Contains(pid),
                        Tag($"{pid} rehearsed {r.Date:yyyy-MM-dd} inside {c.Id}'s rest window"));
            foreach (var ss in sch.SimulatorSorties.Where(x => !x.IsActual && InWindow(x.Date)))
            {
                var simCrew = ss.AdditionalPilotIds.Concat(ss.EngineerIds);
                if (ss.PilotId.Length > 0) simCrew = simCrew.Prepend(ss.PilotId);
                foreach (var pid in simCrew)
                    Assert.False(crew.Contains(pid),
                        Tag($"{pid} ran a sim session {ss.Date:yyyy-MM-dd} inside {c.Id}'s rest window"));
            }
        }

        var simByDevDay = new Dictionary<(string, DateOnly), int>();
        foreach (var ss in sch.SimulatorSorties.Where(x => !x.IsActual))
        {
            int bps = Bps(ss.CampaignId);
            Assert.True(s.Calendar.IsWorkday(ss.Date), Tag($"sim {ss.CampaignId} ran {ss.Date:yyyy-MM-dd}, not a workday"));
            var simCrew = ss.AdditionalPilotIds.Concat(ss.EngineerIds);
            if (ss.PilotId.Length > 0) simCrew = simCrew.Prepend(ss.PilotId);
            foreach (var pid in simCrew)
                if (allLeave.TryGetValue(pid, out var lw))
                    Assert.False(lw.Any(w => w.Contains(ss.Date)), Tag($"{pid} ran a sim session on leave {ss.Date:yyyy-MM-dd}"));

            var dev = s.Simulators.FirstOrDefault(d => d.Id == ss.SimulatorId);
            Assert.True(dev is not null, Tag($"sim session on unknown device {ss.SimulatorId}"));
            Assert.False(dev!.MaintenanceWindows.Any(w => w.Contains(ss.Date)), Tag($"sim {ss.SimulatorId} ran in maintenance {ss.Date:yyyy-MM-dd}"));
            if (sch.SimulatorRepairs.TryGetValue(ss.SimulatorId, out var srw))
                Assert.False(srw.Any(w => w.Contains(ss.Date)), Tag($"sim {ss.SimulatorId} ran during a repair {ss.Date:yyyy-MM-dd}"));

            int cap = dev.MaxBlocksPerDay <= 0 ? bpd : Math.Min(dev.MaxBlocksPerDay, bpd);
            int used = simByDevDay[(ss.SimulatorId, ss.Date)] = simByDevDay.GetValueOrDefault((ss.SimulatorId, ss.Date)) + bps;
            Assert.True(used <= cap, Tag($"sim {ss.SimulatorId} used {used} > cap {cap} blocks on {ss.Date:yyyy-MM-dd}"));
        }
        foreach (var grp in sch.SimulatorSorties.GroupBy(x => x.CampaignId))
        {
            var nos = grp.Select(x => x.SortieNo).OrderBy(n => n).ToList();
            Assert.Equal(Enumerable.Range(1, nos.Count), nos);
        }

        foreach (var dep in sch.Deployments)
        {
            var end = dep.ReturnEnd ?? s.HorizonEnd;
            var awayP = dep.PersonIds.ToHashSet();
            var awayT = dep.AircraftIds.ToHashSet();
            foreach (var so in sch.Sorties.Where(x => !x.IsActual))
            {
                if (so.Date < dep.Departure || so.Date > end) continue;
                if (campById.TryGetValue(so.CampaignId, out var c) && c.LocationId == dep.LocationId) continue;
                foreach (var pid in so.AdditionalPilotIds.Prepend(so.PilotId).Concat(so.SupportPilotIds).Concat(so.EngineerIds))
                    Assert.False(awayP.Contains(pid), Tag($"{pid} flew {so.CampaignId} on {so.Date:yyyy-MM-dd} while deployed to {dep.LocationName}"));
                foreach (var tail in so.SupportAircraftIds.Prepend(so.AircraftId))
                    Assert.False(awayT.Contains(tail), Tag($"{tail} flew {so.CampaignId} on {so.Date:yyyy-MM-dd} while deployed to {dep.LocationName}"));
            }
        }
    }

    [Theory]
    [InlineData(0, 600)]
    public void Random_scenarios_satisfy_engine_invariants(int from, int count)
    {
        var greedy = new Scheduler();
        var advancedOff = new RevisingScheduler(RevisingOptions.Off);
        var smart = new RevisingScheduler(Smart);
        var advancedAll = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
            GlobalSearchIterations = 600,
        });

        for (int seed = from; seed < from + count; seed++)
        {
            var s = Generate(seed);
            Schedule g, off, sm, all;
            try
            {
                g = greedy.Run(s);
                off = advancedOff.Run(s);
                sm = smart.Run(s);
                all = advancedAll.Run(s);
            }
            catch (Exception ex)
            {
                Assert.Fail($"[seed {seed}] engine threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            AssertSane(seed, "greedy", s, g);
            AssertSane(seed, "smart", s, sm);
            AssertSane(seed, "advanced-all", s, all);

            Assert.True(Json(g) == Json(off), $"[seed {seed}] advanced-OFF diverged from greedy");

            var gs = Score(s, g);
            Assert.True(BetterOrEqual(Score(s, sm), gs), $"[seed {seed}] Smart regressed below greedy");
            Assert.True(BetterOrEqual(Score(s, all), gs), $"[seed {seed}] advanced-all regressed below greedy");
        }
    }

    private static readonly RevisingOptions Smart = new()
    {
        EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
        EnableScarcityCrew = true, EnableBottleneckSteer = true,
    };

    [Theory]
    [InlineData(0, 40)]
    public void Deterministic_engines_are_reproducible(int from, int count)
    {
        var greedy = new Scheduler();
        var smart = new RevisingScheduler(Smart);
        var advanced = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableScarcityCrew = true,
            EnableBottleneckSteer = true, EnableRestStagger = true,
            EnableGlobalSearch = true, GlobalSearchIterations = 400,
        });
        for (int seed = from; seed < from + count; seed++)
        {
            var s = Generate(seed);
            Assert.True(Json(greedy.Run(s)) == Json(greedy.Run(s)), $"[seed {seed}] greedy not reproducible");
            Assert.True(Json(smart.Run(s)) == Json(smart.Run(s)), $"[seed {seed}] smart not reproducible");
            Assert.True(Json(advanced.Run(s)) == Json(advanced.Run(s)), $"[seed {seed}] advanced not reproducible");
        }
    }

    [Theory]
    [InlineData(154)]
    [InlineData(408)]
    public void Advanced_repair_never_flies_a_tail_during_auto_maintenance(int seed)
    {
        var s = Generate(seed);
        var sch = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
            GlobalSearchIterations = 600,
        }).Run(s);

        foreach (var so in sch.Sorties.Where(x => !x.IsActual))
            foreach (var tail in so.SupportAircraftIds.Prepend(so.AircraftId))
                if (sch.AutoMaintenance.TryGetValue(tail, out var ws))
                    Assert.False(ws.Any(w => w.Contains(so.Date)),
                        $"[seed {seed}] {tail} flew {so.Date:yyyy-MM-dd} during auto-maintenance");
    }

    private static Scenario GenerateProbabilistic(int seed, bool advancedMc)
    {
        var s = Generate(seed);
        var rng = new Random(seed ^ 0x515A);
        var rates = new double[12];
        for (int m = 0; m < 12; m++) rates[m] = Math.Round(0.4 + rng.NextDouble() * 0.55, 2);
        s.FlyableModel = new ProbabilisticFlyableDays
        {
            MonthlyRates = rates,
            Persistence = Math.Round(rng.NextDouble() * 0.8, 2),
            Anchor = Origin,
        };
        if (rng.NextDouble() < 0.6)
            foreach (var a in s.Aircraft)
                if (rng.NextDouble() < 0.5)
                {
                    a.FailuresPer100FlightHours = Math.Round(2 + rng.NextDouble() * 8, 1);
                    a.RepairDaysMin = 1;
                    a.RepairDaysMax = 1 + rng.Next(3);
                }
        foreach (var c in s.Campaigns)
            if (rng.NextDouble() < 0.4)
            {
                c.TestEfficiencyEnabled = true;
                c.RetestProbabilityStart = Math.Round(0.15 + rng.NextDouble() * 0.25, 2);
                c.RetestProbabilityFloor = Math.Round(rng.NextDouble() * 0.05, 2);
                c.RetestLearningScale = 5 + rng.Next(20);
                c.RetestCurve = rng.Next(2) == 0 ? RetestModel.Exponential : RetestModel.PowerLaw;
                c.RetestExponent = Math.Round(0.5 + rng.NextDouble() * 1.5, 2);
                c.AbortProbability = Math.Round(rng.NextDouble() * 0.1, 2);
            }
        s.Config.McUseAdvancedEngine = advancedMc;
        return s;
    }

    private static string Fingerprint(MonteCarlo.Result r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(r.PDelivery).Append('|').Append(r.PProgramIncomplete).Append('|')
          .Append(r.FinishP10).Append('|').Append(r.FinishP50).Append('|').Append(r.FinishP90).Append('|')
          .Append(r.SortiesP10).Append('|').Append(r.SortiesP50).Append('|').Append(r.SortiesP90).Append(';');
        foreach (var c in r.Campaigns.OrderBy(c => c.CampaignId))
            sb.Append(c.CampaignId).Append('=').Append(c.PMeet).Append(',').Append(c.PIncomplete).Append(',')
              .Append(c.P10).Append(',').Append(c.P50).Append(',').Append(c.P90).Append(',')
              .Append(c.SortiesP10).Append(',').Append(c.SortiesP50).Append(',').Append(c.SortiesP90).Append(';');
        return sb.ToString();
    }

    private static void AssertMcSane(int seed, string engine, MonteCarlo.Result r)
    {
        Assert.InRange(r.PDelivery, 0.0, 1.0);
        Assert.InRange(r.PProgramIncomplete, 0.0, 1.0);
        if (r.FinishP10 is { } f10 && r.FinishP50 is { } f50 && r.FinishP90 is { } f90)
        {
            Assert.True(f10 <= f50 && f50 <= f90, $"[seed {seed}/{engine}] finish percentiles out of order");
        }
        Assert.True(r.SortiesP10 <= r.SortiesP50 && r.SortiesP50 <= r.SortiesP90,
            $"[seed {seed}/{engine}] sortie percentiles out of order");
        foreach (var c in r.Campaigns)
        {
            Assert.InRange(c.PMeet, 0.0, 1.0);
            Assert.InRange(c.PIncomplete, 0.0, 1.0);
            if (c.P10 is { } p10 && c.P50 is { } p50 && c.P90 is { } p90)
                Assert.True(p10 <= p50 && p50 <= p90, $"[seed {seed}/{engine}] {c.CampaignId} completion percentiles out of order");
            Assert.True(c.SortiesP10 <= c.SortiesP50 && c.SortiesP50 <= c.SortiesP90,
                $"[seed {seed}/{engine}] {c.CampaignId} sortie percentiles out of order");
        }
    }

    [Theory]
    [InlineData(0, 12, false)]
    [InlineData(0, 6, true)]
    public void Monte_carlo_is_reproducible_and_well_ordered(int from, int count, bool advancedMc)
    {
        const int runs = 30;
        const int seed = 4242;
        for (int s0 = from; s0 < from + count; s0++)
        {
            var sc = GenerateProbabilistic(s0, advancedMc);
            var engine = advancedMc ? "advanced-mc" : "greedy-mc";

            MonteCarlo.Result a, b;
            try
            {
                a = MonteCarlo.Analyze(sc, runs, seed);
                b = MonteCarlo.Analyze(sc, runs, seed);
            }
            catch (Exception ex)
            {
                Assert.Fail($"[seed {s0}/{engine}] MC threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            Assert.True(Fingerprint(a) == Fingerprint(b), $"[seed {s0}/{engine}] MC is not reproducible");
            AssertMcSane(s0, engine, a);

            for (int run = 0; run < Math.Min(2, runs); run++)
            {
                var (work, rsch) = MonteCarlo.ReplayRun(sc, run, seed);
                AssertSane(s0, engine + "-replay", work, rsch);
            }
        }
    }

    private static T Clone<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, ScenarioJson.Options), ScenarioJson.Options)!;

    [Theory]
    [InlineData(0, 150)]
    public void Execution_tracking_replay_preserves_history_and_legality(int from, int count)
    {
        var greedy = new Scheduler();
        var advanced = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
            GlobalSearchIterations = 300,
        });

        for (int seed = from; seed < from + count; seed++)
        {
            var s = Generate(seed);
            var baseline = greedy.Run(s);
            if (baseline.Sorties.Count < 3) continue;

            var dates = baseline.Sorties.Select(x => x.Date).OrderBy(d => d).Distinct().ToList();
            if (dates.Count < 2) continue;
            var cutoff = dates[Math.Max(1, dates.Count * 45 / 100)];

            var actualSorties = baseline.Sorties.Where(x => x.Date < cutoff).Select(Clone).ToList();
            if (actualSorties.Count == 0 || actualSorties.Count == baseline.Sorties.Count) continue;
            foreach (var so in actualSorties) so.IsActual = true;

            var frozenKeys = actualSorties.Select(x => (x.CampaignId, x.SortieNo)).ToHashSet();
            var actualRehearsals = baseline.Rehearsals
                .Where(r => frozenKeys.Contains((r.CampaignId, r.SortieNo))).Select(Clone).ToList();
            foreach (var r in actualRehearsals) r.IsActual = true;

            s.FreezeDate = cutoff;
            s.ActualSorties = actualSorties;
            s.ActualRehearsals = actualRehearsals;

            foreach (var (engine, eng) in new (string, ISchedulerService)[] { ("greedy", greedy), ("advanced", advanced) })
            {
                Schedule sch;
                try { sch = eng.Run(s); }
                catch (Exception ex)
                {
                    Assert.Fail($"[seed {seed}/{engine}] replan threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                AssertSane(seed, engine + "-replan", s, sch);

                foreach (var so in sch.Sorties.Where(x => !x.IsActual))
                    Assert.True(so.Date >= cutoff, $"[seed {seed}/{engine}] planned sortie {so.Date:yyyy-MM-dd} < freeze {cutoff:yyyy-MM-dd}");
                foreach (var r in sch.Rehearsals.Where(x => !x.IsActual))
                    Assert.True(r.Date >= cutoff, $"[seed {seed}/{engine}] planned rehearsal {r.Date:yyyy-MM-dd} < freeze {cutoff:yyyy-MM-dd}");

                Assert.True(sch.Sorties.Count(x => x.IsActual) >= actualSorties.Count,
                    $"[seed {seed}/{engine}] history shrank: {sch.Sorties.Count(x => x.IsActual)} < {actualSorties.Count}");
            }
        }
    }

    [Theory]
    [InlineData(0, 8)]
    public void Analysis_pathways_are_robust(int from, int count)
    {
        const int seedN = 4242, runs = 24;
        for (int seed = from; seed < from + count; seed++)
        {
            var sc = GenerateProbabilistic(seed, advancedMc: seed % 2 == 0);

            foreach (var src in new[] { MonteCarlo.UncertaintySources.Weather, MonteCarlo.UncertaintySources.Yield, MonteCarlo.UncertaintySources.All })
            {
                MonteCarlo.Result r;
                try { r = MonteCarlo.Analyze(sc, runs, seedN, sources: src); }
                catch (Exception ex) { Assert.Fail($"[seed {seed}] decomposition {src} threw: {ex.GetType().Name}: {ex.Message}"); return; }
                AssertMcSane(seed, $"decomp-{src}", r);
            }

            try { MonteCarlo.Sensitivity(sc, runs: 12, seed: seedN, sobolSamples: 16); }
            catch (Exception ex) { Assert.Fail($"[seed {seed}] sensitivity threw: {ex.GetType().Name}: {ex.Message}"); return; }

            Optimizer.Result opt;
            try
            {
                opt = Optimizer.Optimize(sc, new Optimizer.Options
                {
                    Levers = Optimizer.Levers.Ordering | Optimizer.Levers.Maintenance | Optimizer.Levers.Leave
                             | Optimizer.Levers.StartDates | Optimizer.Levers.CrewAssignment,
                    Iterations = 50, SearchRuns = 10, FinalRuns = 24, MaxShiftDays = 7,
                });
            }
            catch (Exception ex) { Assert.Fail($"[seed {seed}] optimizer threw: {ex.GetType().Name}: {ex.Message}"); return; }
            Assert.True(opt.OptimizedScore >= opt.BaselineScore - 1e-6,
                $"[seed {seed}] optimizer regressed: optimized {opt.OptimizedScore:0.####} < baseline {opt.BaselineScore:0.####}");
            if (opt.Improved)
                Assert.True(opt.Changes.Count > 0, $"[seed {seed}] optimizer claims Improved but emits no applicable change");

            try { ResourceSuggester.Recommend(sc, ResourceSuggester.RecommenderLevers.AddResources, runs: 10, maxSteps: 2, seed: seedN); }
            catch (Exception ex) { Assert.Fail($"[seed {seed}] recommender threw: {ex.GetType().Name}: {ex.Message}"); return; }

            const int maxFloat = 20;
            foreach (var (label, eval) in new (string, Func<Scenario, Schedule>)[]
            {
                ("greedy", x => new Scheduler().Run(x)),
                ("smart", x => new RevisingScheduler(Smart).Run(x)),
            })
            {
                CriticalityAnalysis.Result cr;
                try { cr = CriticalityAnalysis.Analyze(sc, eval, maxFloat); }
                catch (Exception ex) { Assert.Fail($"[seed {seed}/{label}] criticality threw: {ex.GetType().Name}: {ex.Message}"); return; }
                foreach (var c in cr.Campaigns)
                {
                    if (c.Completed)
                    {
                        Assert.NotNull(c.FloatDays);
                        Assert.InRange(c.FloatDays!.Value, 0, maxFloat);
                        Assert.Equal(c.FloatDays == 0, c.IsCritical);
                    }
                    else
                    {
                        Assert.Null(c.FloatDays);
                        Assert.True(c.IsCritical, $"[seed {seed}/{label}] {c.CampaignId} incomplete but not flagged critical");
                    }
                }
                foreach (var res in cr.Resources)
                    Assert.True(res.DeltaFinishDays >= 0 && res.NewlyIncomplete >= 0,
                        $"[seed {seed}/{label}] {res.ResourceId} negative removal impact");
            }
        }
    }
}
