using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Reporting;

public enum IdleCategory
{
    None,
    Unavailable,
    NoWork,
    Limits,
    EngineChoice,
}

public static class IdleCodes
{
    public const char Worked = '.';
    public const char NotRelevant = '-';
    public const char Leave = 'l';
    public const char Rest = 'z';
    public const char Away = 'y';
    public const char Maintenance = 'm';
    public const char Rating = 't';
    public const char Blocked = 'b';
    public const char NoWork = 'n';
    public const char Ftom = 'f';
    public const char VoluntaryRest = 'v';
    public const char NotNeeded = 'u';

    public static IdleCategory Category(char c) => c switch
    {
        Leave or Rest or Away or Maintenance or Rating or Blocked => IdleCategory.Unavailable,
        NoWork => IdleCategory.NoWork,
        Ftom => IdleCategory.Limits,
        VoluntaryRest or NotNeeded => IdleCategory.EngineChoice,
        _ => IdleCategory.None,
    };

    public static string Label(char c) => c switch
    {
        Leave => "leave",
        Rest => "rest",
        Away => "deployed",
        Maintenance => "maint.",
        Rating => "no rating",
        Blocked => "blocked",
        NoWork => "no work",
        Ftom => "FTOM",
        VoluntaryRest => "rest rotation",
        NotNeeded => "not needed",
        _ => "",
    };

    public static string CategoryLabel(IdleCategory c) => c switch
    {
        IdleCategory.Unavailable => "unavailable",
        IdleCategory.NoWork => "no work",
        IdleCategory.Limits => "FTOM",
        IdleCategory.EngineChoice => "engine",
        _ => "",
    };
}

public sealed record IdleExplanation(char Code, string Headline, IReadOnlyList<string> Details)
{
    public IdleCategory Category => IdleCodes.Category(Code);
}

public sealed class IdleReport
{
    private readonly Dictionary<(string Id, DateOnly Day), IdleExplanation> _explain = new();

    public IReadOnlyList<DateOnly> Days { get; }

    public IReadOnlyDictionary<string, string> PersonLog { get; }

    public IReadOnlyDictionary<string, string> AircraftLog { get; }

    private IdleReport(List<DateOnly> days, Dictionary<string, string> people, Dictionary<string, string> aircraft)
    {
        Days = days;
        PersonLog = people;
        AircraftLog = aircraft;
    }

    public IdleExplanation? Explain(string resourceId, DateOnly day) =>
        _explain.GetValueOrDefault((resourceId, day));

    public Dictionary<char, int> Counts(string resourceId)
    {
        var log = PersonLog.GetValueOrDefault(resourceId) ?? AircraftLog.GetValueOrDefault(resourceId) ?? "";
        return log.Where(c => c != IdleCodes.Worked && c != IdleCodes.NotRelevant)
            .GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
    }

    public int IdleDays(string resourceId) => Counts(resourceId).Values.Sum();

    public string Breakdown(string resourceId) => string.Join(" · ",
        Counts(resourceId).OrderByDescending(kv => kv.Value)
            .Select(kv => $"{IdleCodes.Label(kv.Key)} {kv.Value}d"));

    public static IdleReport Build(Scenario sc, Schedule sch) => new Builder(sc, sch).Run();

    private sealed class Builder
    {
        private readonly Scenario _sc;
        private readonly Schedule _sch;
        private readonly List<DateOnly> _days = new();
        private readonly Dictionary<DateOnly, int> _idx = new();
        private readonly Dictionary<string, Campaign> _camp;
        private readonly List<Resource> _people;
        private readonly Dictionary<string, PersonIndex> _pIdx = new();
        private readonly Dictionary<string, string> _name = new();
        private CrewLedger _ledger = null!;
        private IdleReport _report = null!;

        private readonly Dictionary<DateOnly, HashSet<string>> _flewPeople = new();
        private readonly Dictionary<DateOnly, HashSet<string>> _simPeople = new();
        private readonly Dictionary<DateOnly, List<Rehearsal>> _rehOn = new();
        private readonly Dictionary<(string Tail, DateOnly Day), int> _tailBlocks = new();
        private readonly Dictionary<(string Camp, DateOnly Day), List<Sortie>> _sortiesOn = new();
        private readonly Dictionary<(string Camp, DateOnly Day), List<SimulatorSortie>> _simOn = new();
        private readonly HashSet<(string Id, DateOnly Day)> _ferry = new();
        private readonly HashSet<(string Id, DateOnly Day)> _volRest = new();

        public Builder(Scenario sc, Schedule sch)
        {
            _sc = sc;
            _sch = sch;
            for (var d = sc.HorizonStart; d <= sc.HorizonEnd; d = d.AddDays(1)) { _idx[d] = _days.Count; _days.Add(d); }
            _camp = sc.Campaigns.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            _people = sc.Pilots.Cast<Resource>().Concat(sc.Engineers).ToList();
            for (int p = 0; p < _people.Count; p++)
            {
                _pIdx.TryAdd(_people[p].Id, new PersonIndex(p));
                _name.TryAdd(_people[p].Id, _people[p].Name);
            }
            foreach (var a in sc.Aircraft) _name.TryAdd(a.Id, a.Id);
        }

        private string N(string id) => _name.GetValueOrDefault(id, id);

        public IdleReport Run()
        {
            var people = new Dictionary<string, string>();
            var aircraft = new Dictionary<string, string>();
            _report = new IdleReport(_days, people, aircraft);
            if (_days.Count == 0) return _report;

            Replay();
            foreach (var p in _people)
            {
                if (people.ContainsKey(p.Id)) continue;
                var codes = new char[_days.Count];
                for (int i = 0; i < _days.Count; i++) codes[i] = PersonDay(p, i);
                people[p.Id] = new string(codes);
            }
            foreach (var a in _sc.Aircraft)
            {
                if (aircraft.ContainsKey(a.Id)) continue;
                var codes = new char[_days.Count];
                for (int i = 0; i < _days.Count; i++) codes[i] = AircraftDay(a, i);
                aircraft[a.Id] = new string(codes);
            }
            return _report;
        }

        private void Replay()
        {
            _ledger = new CrewLedger(_people, _days, Math.Max(1, _sc.Config.BlocksPerDay), _sc.Config);
            int bpd = Math.Max(1, _sc.Config.BlocksPerDay);

            foreach (var s in _sch.Sorties)
            {
                if (!_idx.TryGetValue(s.Date, out int i) || !_camp.TryGetValue(s.CampaignId, out var c)) continue;
                var crew = new[] { s.PilotId }.Concat(s.AdditionalPilotIds).Concat(s.SupportPilotIds)
                    .Concat(s.EngineerIds).Where(id => id.Length > 0).Distinct().ToList();
                _ledger.AddSortie(Persons(crew), i, c.ExpectedSortieFlightHours, c.ExpectedSortieDutyHours,
                    c.ExpectedSortieFlightDutyHours);
                Set(_flewPeople, s.Date).UnionWith(crew);
                Add(_sortiesOn, (s.CampaignId, s.Date), s);
                int span = Math.Clamp(c.BlocksPerSortie, 1, bpd);
                foreach (var t in s.SupportAircraftIds.Prepend(s.AircraftId))
                    _tailBlocks[(t, s.Date)] = _tailBlocks.GetValueOrDefault((t, s.Date)) + span;
            }
            foreach (var r in _sch.Rehearsals)
            {
                if (!_idx.TryGetValue(r.Date, out int i) || !_camp.TryGetValue(r.CampaignId, out var c)) continue;
                _ledger.AddRehearsal(Persons(r.CrewIds), i, c.ExpectedRehearsalDutyHours);
                Set(_simPeople, r.Date).UnionWith(r.CrewIds);
                (_rehOn.TryGetValue(r.Date, out var l) ? l : _rehOn[r.Date] = new()).Add(r);
            }
            foreach (var s in _sch.SimulatorSorties)
            {
                if (!_idx.TryGetValue(s.Date, out int i) || !_camp.TryGetValue(s.CampaignId, out var c)) continue;
                var crew = new[] { s.PilotId }.Concat(s.AdditionalPilotIds).Concat(s.EngineerIds)
                    .Where(id => id.Length > 0).Distinct().ToList();
                _ledger.AddSimSession(Persons(crew), i, c.ExpectedSortieDutyHours);
                Set(_flewPeople, s.Date).UnionWith(crew);
                Add(_simOn, (s.CampaignId, s.Date), s);
            }
            var locById = _sc.Locations.ToDictionary(l => l.Id);
            foreach (var f in _sch.RelocationFlights)
            {
                if (!_idx.TryGetValue(f.Date, out int i) || !locById.TryGetValue(f.LocationId, out var loc)) continue;
                if (f.CrewIds.Count > 0)
                    _ledger.AddSortie(Persons(f.CrewIds), i, loc.FerryFlightHours, loc.FerryDutyHours, 0);
                foreach (var id in f.CrewIds.Concat(f.AircraftIds)) _ferry.Add((id, f.Date));
            }
            foreach (var (pid, days) in _sch.VoluntaryRest)
                foreach (var d in days) _volRest.Add((pid, d));
        }

        private List<PersonIndex> Persons(IEnumerable<string> ids) =>
            ids.Where(_pIdx.ContainsKey).Select(id => _pIdx[id]).ToList();

        private static HashSet<string> Set(Dictionary<DateOnly, HashSet<string>> m, DateOnly d) =>
            m.TryGetValue(d, out var s) ? s : m[d] = new HashSet<string>();

        private static void Add<T>(Dictionary<(string, DateOnly), List<T>> m, (string, DateOnly) k, T v) =>
            (m.TryGetValue(k, out var l) ? l : m[k] = new List<T>()).Add(v);

        private bool BeforeFreeze(DateOnly d) => _sc.FreezeDate is { } f && d < f;

        private DeploymentInfo? DeploymentOf(string id, DateOnly d, bool person) =>
            _sch.Deployments.FirstOrDefault(dep =>
                (person ? dep.PersonIds : dep.AircraftIds).Contains(id)
                && d >= dep.Departure && d <= (dep.ReturnEnd ?? _sc.HorizonEnd));

        private bool WorksOn(IEnumerable<Campaign> camps, DateOnly d) =>
            _sc.Calendar.IsWorkday(d) || camps.Any(c => !c.UseGlobalCalendar && _sc.Calendar.IsWorkday(c, d));

        private bool FlyableAt(DateOnly d, DeploymentInfo? dep, IEnumerable<Campaign> camps)
        {
            var baseModel = dep is null
                ? _sc.FlyableModel
                : _sc.Locations.FirstOrDefault(l => l.Id == dep.LocationId)?.FlyableModel ?? _sc.FlyableModel;
            if (!camps.Any(c => c.OwnFlyableModel() is not null)) return baseModel.IsFlyable(d);
            return camps.Any(c => (c.OwnFlyableModel() ?? baseModel).IsFlyable(d));
        }

        private char PersonDay(Resource p, int i)
        {
            char code = PersonDayCore(p, i);
            var d = _days[i];
            if (IdleCodes.Category(code) == IdleCategory.None
                || !_rehOn.TryGetValue(d, out var rehs) || !rehs.Any(r => r.CrewIds.Contains(p.Id)))
                return code;
            var e = _report._explain[(p.Id, d)];
            _report._explain[(p.Id, d)] = e with { Code = IdleCodes.Worked, Headline = $"Rehearsal only · {e.Headline}" };
            return IdleCodes.Worked;
        }

        private char PersonDayCore(Resource p, int i)
        {
            var d = _days[i];
            if (BeforeFreeze(d)) return IdleCodes.NotRelevant;
            bool isPilot = p is Pilot;
            var assigned = _sc.Campaigns.Where(c => isPilot
                ? c.AssignedPilotIds.Contains(p.Id)
                : c.AssignedEngineerIds.Contains(p.Id)).ToList();
            if (!WorksOn(assigned, d)) return IdleCodes.NotRelevant;
            if (_flewPeople.TryGetValue(d, out var flew) && flew.Contains(p.Id)) return IdleCodes.Worked;

            if (p.LeaveWindows.FirstOrDefault(w => w.Contains(d)) is { } leave)
                return Note(p.Id, d, IdleCodes.Leave, "Leave"
                    + (string.IsNullOrWhiteSpace(leave.Note) ? "" : $" ({leave.Note})"),
                    Span(leave.Start, leave.End));
            if (_sch.Deployments.FirstOrDefault(dep => dep.PersonIds.Contains(p.Id)
                    && dep.ReturnEnd is { } re && dep.RestEnd is { } rs && d > re && d <= rs) is { } restDep)
                return Note(p.Id, d, IdleCodes.Rest, $"Rest after deployment ({restDep.LocationName})",
                    $"until {D(restDep.RestEnd!.Value)}");
            if (RestAfterCompletion(assigned, d) is { } restCamp)
                return Note(p.Id, d, IdleCodes.Rest, $"Rest after {restCamp.Name}",
                    $"{restCamp.RestDaysAfterCompletion} days off");

            var dep = DeploymentOf(p.Id, d, person: true);
            if (dep is not null)
            {
                if (_ferry.Contains((p.Id, d)))
                    return Note(p.Id, d, IdleCodes.Away, $"Ferry → {dep.LocationName}");
                if (d < dep.FlyFrom)
                    return Note(p.Id, d, IdleCodes.Away, $"{dep.LocationName}: setup",
                        $"flying from {D(dep.FlyFrom)}");
                assigned = assigned.Where(c => c.LocationId == dep.LocationId).ToList();
            }
            else
                assigned = assigned.Where(c => c.LocationId.Length == 0 || c.Venue == CampaignVenue.Simulator).ToList();
            if (assigned.Count > 0)
            {
                var working = assigned.Where(c => _sc.Calendar.IsWorkday(c, d)).ToList();
                if (working.Count == 0) return IdleCodes.NotRelevant;
                assigned = working;
            }

            bool flyable = FlyableAt(d, dep, assigned);
            var relevant = flyable ? assigned : assigned.Where(c => c.Venue == CampaignVenue.Simulator).ToList();
            if (relevant.Count == 0 && !flyable) return IdleCodes.NotRelevant;
            if (dep is not null && relevant.Count > 0 && relevant.All(c => Completed(c, d)))
                return Note(p.Id, d, IdleCodes.Away, $"{dep.LocationName}: teardown");

            if (assigned.Count == 0)
                return Note(p.Id, d, IdleCodes.NoWork, "No campaign assigned");

            var lines = new List<string>();
            var open = new List<Campaign>();
            bool anyFlew = false;
            foreach (var c in relevant)
            {
                var (state, text) = CampaignState(c, d, i, p.Id);
                lines.Add(text);
                if (state == CampState.Open) open.Add(c);
                else if (state == CampState.Flew) { open.Add(c); anyFlew = true; }
            }
            if (_rehOn.TryGetValue(d, out var rehs))
                foreach (var r in rehs.Where(r => r.CrewIds.Contains(p.Id)))
                    lines.Insert(0, $"Rehearsal R#{r.SortieNo} ({Name(r.CampaignId)})");

            if (open.Count == 0)
                return Note(p.Id, d, IdleCodes.NoWork, "No open work", lines.ToArray());

            if (_volRest.Contains((p.Id, d)))
                return Note(p.Id, d, IdleCodes.VoluntaryRest, "Rest rotation", lines.ToArray());

            var flightCamp = open.FirstOrDefault(c => c.Venue == CampaignVenue.Aircraft);
            if (flightCamp is not null && _pIdx.TryGetValue(p.Id, out var pi)
                && _ledger.ExplainSortie(pi, i, flightCamp.ExpectedSortieFlightHours,
                    flightCamp.ExpectedSortieDutyHours, flightCamp.ExpectedSortieFlightDutyHours,
                    flightCamp.EffectiveConfig(_sc.Config)) is { } rule)
                return Note(p.Id, d, IdleCodes.Ftom, $"FTOM: {rule}", lines.ToArray());

            if (open.Where(c => c.Venue == CampaignVenue.Aircraft).All(c =>
                    p.TypeRatings.Count > 0 && !c.CompatibleAircraftIds.Any(p.TypeRatings.Contains))
                && open.All(c => c.Venue == CampaignVenue.Aircraft))
                return Note(p.Id, d, IdleCodes.Rating, "No type rating", lines.ToArray());

            if (anyFlew)
                return Note(p.Id, d, IdleCodes.NotNeeded, "Not needed", lines.ToArray());
            return Note(p.Id, d, IdleCodes.Blocked, "Campaign blocked", lines.ToArray());
        }

        private Campaign? RestAfterCompletion(IEnumerable<Campaign> camps, DateOnly d) =>
            camps.FirstOrDefault(c => c.RestDaysAfterCompletion > 0
                && _sch.CampaignEndDates.GetValueOrDefault(c.Id) is { } end
                && d > end && d <= end.AddDays(c.RestDaysAfterCompletion));

        private bool Completed(Campaign c, DateOnly d) =>
            _sch.CampaignEndDates.GetValueOrDefault(c.Id) is { } end && end < d;

        private string Name(string campaignId) => _camp.TryGetValue(campaignId, out var c) ? c.Name : campaignId;

        private enum CampState { NotOpen, Open, Flew }

        private (CampState, string) CampaignState(Campaign c, DateOnly d, int i, string exceptId)
        {
            char code = _sch.DayLog.TryGetValue(c.Id, out var log) && i < log.Length ? log[i] : DayLogCodes.Idle;
            switch (code)
            {
                case DayLogCodes.Placed:
                {
                    char stop = _sch.DayStopLog.TryGetValue(c.Id, out var sl) && i < sl.Length ? sl[i] : DayLogCodes.Unknown;
                    int n = (_sortiesOn.GetValueOrDefault((c.Id, d))?.Count ?? 0) + (_simOn.GetValueOrDefault((c.Id, d))?.Count ?? 0);
                    var crew = (_sortiesOn.GetValueOrDefault((c.Id, d)) ?? new())
                        .SelectMany(s => s.AdditionalPilotIds.Prepend(s.PilotId).Concat(s.SupportPilotIds).Concat(s.EngineerIds))
                        .Concat((_simOn.GetValueOrDefault((c.Id, d)) ?? new())
                            .SelectMany(s => s.AdditionalPilotIds.Prepend(s.PilotId).Concat(s.EngineerIds)))
                        .Where(x => x.Length > 0 && x != exceptId).Distinct().Select(N);
                    return (CampState.Flew,
                        $"{c.Name}: flew {n} ({string.Join(", ", crew)}), {StopText(c, stop)}");
                }
                case DayLogCodes.Idle:
                    if (_sch.CampaignEndDates.GetValueOrDefault(c.Id) is { } end && end < d)
                        return (CampState.NotOpen, $"{c.Name}: done {D(end)}");
                    if (d < c.StartDate)
                        return (CampState.NotOpen, $"{c.Name}: opens {D(c.StartDate)}");
                    return (CampState.NotOpen, $"{c.Name}: not open");
                case DayLogCodes.Prereq:
                case DayLogCodes.Review:
                case DayLogCodes.NotUsable:
                    return (CampState.NotOpen, $"{c.Name}: {Short(code)}");
                default:
                    return (CampState.Open, $"{c.Name}: {Short(code)}");
            }
        }

        private static string StopText(Campaign c, char stop) => stop switch
        {
            DayLogCodes.Finished => "completed",
            DayLogCodes.DailyCap => "daily cap",
            DayLogCodes.Aircraft => "no free slot",
            DayLogCodes.Pilot => "no pilot",
            DayLogCodes.Engineer => "no FTE",
            DayLogCodes.Rehearsal => "no rehearsal slot",
            DayLogCodes.Simulator => "no sim slot",
            DayLogCodes.External => "no external support",
            DayLogCodes.Pinned => "next sortie pinned",
            _ => "reason unknown (re-run)",
        };

        private static string Short(char code) => code switch
        {
            DayLogCodes.Prereq => "waiting prereq",
            DayLogCodes.Review => "data review",
            DayLogCodes.NotUsable => "not flyable",
            DayLogCodes.Range => "no range",
            DayLogCodes.Aircraft => "no aircraft",
            DayLogCodes.Pilot => "no pilot",
            DayLogCodes.Engineer => "no FTE",
            DayLogCodes.Rehearsal => "no rehearsal slot",
            DayLogCodes.Simulator => "no sim slot",
            DayLogCodes.External => "no external support",
            DayLogCodes.Pinned => "next sortie pinned",
            _ => "blocked",
        };

        private static string D(DateOnly d) => d.ToString("dd.MM");
        private static string Span(DateOnly a, DateOnly b) => $"{D(a)}–{D(b)}";

        private char Note(string id, DateOnly d, char code, string headline, params string[] details)
        {
            _report._explain[(id, d)] = new IdleExplanation(code, headline, details);
            return code;
        }

        private char AircraftDay(Aircraft a, int i)
        {
            var d = _days[i];
            if (BeforeFreeze(d) || !WorksOn(_sc.Campaigns.Where(c =>
                    c.CompatibleAircraftIds.Contains(a.Id) || c.SupportAircraftIds.Contains(a.Id)), d))
                return IdleCodes.NotRelevant;
            int bpd = Math.Max(1, _sc.Config.BlocksPerDay);
            int used = _tailBlocks.GetValueOrDefault((a.Id, d));

            if (used == 0)
            {
                if (a.MaintenanceWindows.FirstOrDefault(w => w.Contains(d)) is { } mw)
                    return Note(a.Id, d, IdleCodes.Maintenance, "Maintenance"
                        + (string.IsNullOrWhiteSpace(mw.Note) ? "" : $" ({mw.Note})"), Span(mw.Start, mw.End));
                if (_sch.AutoMaintenance.GetValueOrDefault(a.Id)?.FirstOrDefault(w => w.Contains(d)) is { } pm)
                    return Note(a.Id, d, IdleCodes.Maintenance, "Periodic check"
                        + (string.IsNullOrWhiteSpace(pm.Note) ? "" : $" ({pm.Note})"), Span(pm.Start, pm.End));
                if (_sch.UnscheduledRepairs.GetValueOrDefault(a.Id)?.FirstOrDefault(w => w.Contains(d)) is { } ur)
                    return Note(a.Id, d, IdleCodes.Maintenance, "Repair", Span(ur.Start, ur.End));
            }

            var dep = DeploymentOf(a.Id, d, person: false);
            if (used == 0 && dep is not null)
            {
                if (_ferry.Contains((a.Id, d))) return Note(a.Id, d, IdleCodes.Away, $"Ferry → {dep.LocationName}");
                if (d < dep.FlyFrom) return Note(a.Id, d, IdleCodes.Away, $"{dep.LocationName}: setup",
                    $"flying from {D(dep.FlyFrom)}");
            }
            if (!FlyableAt(d, dep, _sc.Campaigns.Where(c =>
                    c.CompatibleAircraftIds.Contains(a.Id) || c.SupportAircraftIds.Contains(a.Id)))) return used > 0 ? IdleCodes.Worked : IdleCodes.NotRelevant;

            var camps = _sc.Campaigns.Where(c => c.Venue == CampaignVenue.Aircraft
                && (c.CompatibleAircraftIds.Contains(a.Id) || c.SupportAircraftIds.Contains(a.Id))
                && (dep is null ? c.LocationId.Length == 0 : c.LocationId == dep.LocationId)).ToList();
            if (camps.Count > 0)
            {
                var working = camps.Where(c => _sc.Calendar.IsWorkday(c, d)).ToList();
                if (working.Count == 0) return used > 0 ? IdleCodes.Worked : IdleCodes.NotRelevant;
                camps = working;
            }

            if (used == 0 && RestAfterCompletion(_sc.Campaigns.Where(c =>
                    c.CompatibleAircraftIds.Contains(a.Id) || c.SupportAircraftIds.Contains(a.Id)), d) is { } rc)
                return Note(a.Id, d, IdleCodes.Rest, $"Rest after {rc.Name}",
                    $"{rc.RestDaysAfterCompletion} days off");

            var lines = new List<string>();
            bool anyOpen = false, anyFlew = false;
            foreach (var c in camps)
            {
                var (state, text) = CampaignState(c, d, i, "");
                if (state == CampState.Flew)
                {
                    anyFlew = true;
                    var tails = (_sortiesOn.GetValueOrDefault((c.Id, d)) ?? new())
                        .SelectMany(s => s.SupportAircraftIds.Prepend(s.AircraftId)).Distinct().ToList();
                    char stop = _sch.DayStopLog.TryGetValue(c.Id, out var sl) && i < sl.Length ? sl[i] : DayLogCodes.Unknown;
                    text = $"{c.Name}: flew on {string.Join(", ", tails)}, {StopText(c, stop)}";
                    if (c.PreferSingleAircraft && !tails.Contains(a.Id) && tails.Count > 0)
                        text += $" (locked to {tails[0]})";
                }
                else if (state == CampState.Open) anyOpen = true;
                lines.Add(text);
            }

            if (used > 0)
            {
                if (used >= bpd) return IdleCodes.Worked;
                Note(a.Id, d, IdleCodes.Worked, $"{bpd - used}/{bpd} blocks free", lines.ToArray());
                return IdleCodes.Worked;
            }
            if (camps.Count == 0)
                return Note(a.Id, d, IdleCodes.NoWork, "Not in any campaign");
            if (!anyOpen && !anyFlew)
            {
                if (dep is not null && camps.All(c => Completed(c, d)))
                    return Note(a.Id, d, IdleCodes.Away, $"{dep.LocationName}: teardown");
                return Note(a.Id, d, IdleCodes.NoWork, "No open work", lines.ToArray());
            }
            if (anyFlew)
                return Note(a.Id, d, IdleCodes.NotNeeded, "Not needed", lines.ToArray());
            return Note(a.Id, d, IdleCodes.Blocked, "Campaign blocked", lines.ToArray());
        }
    }
}
