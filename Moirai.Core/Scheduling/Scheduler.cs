using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

public sealed class Scheduler : ISchedulerService
{
    private enum Reason { None, Aircraft, Pilot, Engineer, Rehearsal, Simulator, External, Pinned }

    private static char StopCode(CampState cs, int placedToday, Reason last) =>
        cs.Completed ? DayLogCodes.Finished
        : cs.C.MaxSortiesPerDay > 0 && placedToday >= cs.C.MaxSortiesPerDay ? DayLogCodes.DailyCap
        : last switch
        {
            Reason.Aircraft => DayLogCodes.Aircraft,
            Reason.Pilot => DayLogCodes.Pilot,
            Reason.Engineer => DayLogCodes.Engineer,
            Reason.Rehearsal => DayLogCodes.Rehearsal,
            Reason.Simulator => DayLogCodes.Simulator,
            Reason.External => DayLogCodes.External,
            Reason.Pinned => DayLogCodes.Pinned,
            _ => DayLogCodes.Unknown,
        };

    private sealed class CampState
    {
        public required Campaign C { get; init; }
        public required BottleneckTally Tally { get; init; }
        public int Required;
        public int Done;
        public double RemainingTp;
        public double GoodDone;
        public bool Completed => RemainingTp <= 1e-9;
        public List<PersonIndex> PilotIdx = new();
        public List<PersonIndex> EngIdx = new();
        public List<TailIndex> CompatIdx = new();
        public List<TailIndex> SupportIdx = new();
        public List<ExtIndex> ExtIdx = new();
        public bool ExtUnsatisfiable;
        public HashSet<SimIndex> SimSet = new();
        public bool AnySim = true;
        public TailIndex? LockedTail;
        public DateOnly OpenFrom;
        public DepState? Dep;
        public bool DeploymentDropped;

        public char[] Log = Array.Empty<char>();
        public char[] Stop = Array.Empty<char>();

        public int LastFlyDay = -1;

        public List<Rehearsal> BankedRehearsals = new();

        public List<string> PrereqIds = new();

        public bool[] Work = Array.Empty<bool>();
        public int[] WorkPrefix = Array.Empty<int>();
        public FlyableDayModel? OwnFly;
        public ScenarioConfig Cfg = null!;

        public int EndDay = -1;

        public Dictionary<int, PinnedSortie>? Pins;
        public PinnedSortie? NextPin => Pins?.GetValueOrDefault(Done + 1) is { FixesResources: true } p ? p : null;
        public DateOnly? NextPinDate => Pins?.GetValueOrDefault(Done + 1)?.Date;
        public int OrderDay = -1;
        public int OrderBlock;
        public string OrderKey = "";
    }

    private sealed class DepState
    {
        public required Location L { get; init; }
        public required List<CampState> Camps { get; init; }
        public required int Depart { get; init; }
        public required int FlyFrom { get; init; }
        public int RetFerryStart = int.MaxValue;
        public int RetEnd = int.MaxValue;
        public int RehWindowDepth = 1;
        public HashSet<PersonIndex> Persons = new();
        public HashSet<TailIndex> Tails = new();
        public DeploymentInfo Info = null!;
        public bool AllDone => Camps.All(c => c.Completed);
    }

    private Scenario _sc = null!;
    private ScenarioConfig _cfg = null!;
    private bool _hasDatePins;
    private ISortieYieldModel _yield = null!;
    private IFailureModel _failure = null!;
    private List<DateOnly> _days = null!;
    private int _bpd;

    private List<Resource> _people = null!;
    private CrewLedger _ledger = null!;
    private int[] _workdayPrefix = null!;
    private Dictionary<string, int[]> _workPrefixOf = new();
    private Dictionary<string, int> _prereqReview = new();
    private bool[][] _rated = null!;

    private SimPool _simPool = null!;
    private ExternalSupportPool _extPool = null!;
    private FleetState _fleet = null!;

    private List<CampState>[]? _restCampsOfPerson;
    private List<CampState>[]? _restCampsOfTail;

    private List<DepState> _deps = new();
    private int[] _depOfPerson = null!;
    private int[] _depOfTail = null!;
    private IReadOnlyDictionary<string, DateOnly> _prereqDone =
        new Dictionary<string, DateOnly>();

    private int _planFrom;
    private const int PreDeploymentRehearsalLookback = 30;

    public bool CaptureDecisions { get; init; }

    private bool _capture;
    private List<DecisionEntry> _decisions = new();

    public Schedule Run(Scenario scenario) => Run(scenario, LosslessYieldModel.Instance);

    public Schedule Run(Scenario scenario, ISortieYieldModel yieldModel) =>
        Run(scenario, yieldModel, NoFailuresModel.Instance);

    public Schedule Run(Scenario scenario, ISortieYieldModel yieldModel, IFailureModel failureModel)
    {
        _prereqDone = new Dictionary<string, DateOnly>();
        if (NeedsPrereqAwareDeployment(scenario))
        {
            var est = new Dictionary<string, DateOnly>();
            for (int pass = 0; pass < MaxPrereqPasses; pass++)
            {
                _prereqDone = est;
                if (!MergeLatest(est, CompletionMap(RunCore(scenario, yieldModel, failureModel, capture: false)))) break;
            }
            _prereqDone = est;
        }
        return RunCore(scenario, yieldModel, failureModel, capture: CaptureDecisions);
    }

    private static bool NeedsPrereqAwareDeployment(Scenario sc) =>
        sc.Campaigns.Any(c => c.LocationId.Length > 0
            && CampaignDeadlines.EffectivePrerequisiteCampaignIds(sc, c).Count > 0);

    private static Dictionary<string, DateOnly> CompletionMap(Schedule s) =>
        s.CampaignEndDates.Where(kv => kv.Value.HasValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Value);

    private static bool MergeLatest(Dictionary<string, DateOnly> acc, Dictionary<string, DateOnly> next)
    {
        bool changed = false;
        foreach (var kv in next)
            if (!acc.TryGetValue(kv.Key, out var cur) || kv.Value > cur) { acc[kv.Key] = kv.Value; changed = true; }
        return changed;
    }

    private const int MaxPrereqPasses = 4;

    private Schedule RunCore(Scenario scenario, ISortieYieldModel yieldModel, IFailureModel failureModel, bool capture)
    {
        _sc = scenario;
        _cfg = scenario.Config;
        _hasDatePins = scenario.SortiePins.Any(p => p.Date is not null);
        _yield = yieldModel;
        _failure = failureModel;
        _bpd = Math.Max(1, _cfg.BlocksPerDay);
        _capture = capture;
        _decisions = new List<DecisionEntry>();

        _days = new List<DateOnly>();
        for (var d = scenario.HorizonStart; d <= scenario.HorizonEnd; d = d.AddDays(1))
            _days.Add(d);
        int nDays = _days.Count;

        var schedule = new Schedule();
        var states = BuildStates(scenario, schedule);
        _prereqReview = scenario.Campaigns
            .Where(c => c.PostCompletionReviewWorkdays > 0)
            .ToDictionary(c => c.Id, c => c.PostCompletionReviewWorkdays);
        foreach (var cs in states)
        {
            cs.Log = new char[nDays];
            Array.Fill(cs.Log, DayLogCodes.Idle);
            cs.Stop = new char[nDays];
            Array.Fill(cs.Stop, DayLogCodes.Idle);
        }
        if (nDays == 0)
        {
            Finalize(schedule, states);
            return schedule;
        }

        _people = scenario.Pilots.Cast<Resource>().Concat(scenario.Engineers).ToList();
        int nP = _people.Count;
        var personIdx = new Dictionary<string, PersonIndex>();
        for (int p = 0; p < nP; p++) personIdx[_people[p].Id] = new PersonIndex(p);

        int nAc = scenario.Aircraft.Count;
        _simPool = new SimPool(scenario.Simulators, _days, _bpd);
        _extPool = new ExternalSupportPool(scenario.ExternalSupports, _days, _bpd);
        _fleet = new FleetState(scenario.Aircraft, _days);

        foreach (var cs in states)
        {
            cs.PilotIdx = ResolvePersons(cs.C.AssignedPilotIds, personIdx);
            cs.EngIdx = ResolvePersons(cs.C.AssignedEngineerIds, personIdx);
            cs.CompatIdx = _fleet.Resolve(cs.C.CompatibleAircraftIds);
            cs.SupportIdx = _fleet.Resolve(cs.C.SupportAircraftIds);
            cs.AnySim = cs.C.CompatibleSimulatorIds.Count == 0;
            cs.SimSet = _simPool.Resolve(cs.C.CompatibleSimulatorIds);
            cs.ExtIdx = _extPool.Resolve(cs.C.RequiredExternalSupportIds);
            cs.ExtUnsatisfiable = cs.ExtIdx.Count < cs.C.RequiredExternalSupportIds.Distinct().Count();
            cs.Pins = PinnedSortie.ResolveFor(scenario, cs.C, personIdx, cs.PilotIdx, cs.EngIdx,
                _fleet, cs.CompatIdx, cs.SupportIdx, _simPool, cs.AnySim, cs.SimSet);
        }

        _rated = new bool[nP][];
        for (int p = 0; p < nP; p++)
        {
            _rated[p] = new bool[nAc];
            var ratings = _people[p].TypeRatings;
            if (ratings.Count == 0)
                Array.Fill(_rated[p], true);
            else
                foreach (var id in ratings)
                    if (_fleet.TryIndex(id, out var t)) _rated[p][t.Value] = true;
        }

        BuildRestRouting(states, nP, nAc);

        _workdayPrefix = new int[nDays];
        var globalWork = new bool[nDays];
        for (int i = 0, acc = 0; i < nDays; i++)
        {
            if (globalWork[i] = scenario.Calendar.IsWorkday(_days[i])) acc++;
            _workdayPrefix[i] = acc;
        }
        _workPrefixOf = new Dictionary<string, int[]>();
        foreach (var cs in states)
        {
            if (cs.C.UseGlobalCalendar) { cs.Work = globalWork; cs.WorkPrefix = _workdayPrefix; }
            else
            {
                cs.Work = new bool[nDays];
                cs.WorkPrefix = new int[nDays];
                for (int i = 0, acc = 0; i < nDays; i++)
                {
                    if (cs.Work[i] = scenario.Calendar.IsWorkday(cs.C, _days[i])) acc++;
                    cs.WorkPrefix[i] = acc;
                }
            }
            _workPrefixOf[cs.C.Id] = cs.WorkPrefix;
            cs.OwnFly = cs.C.OwnFlyableModel();
            cs.Cfg = cs.C.EffectiveConfig(_cfg);
        }

        _ledger = new CrewLedger(_people, _days, _bpd, _cfg);

        BuildDeployments(scenario, states, schedule, nP, nAc, nDays);

        _planFrom = scenario.FreezeDate is { } freeze
            ? Math.Clamp(freeze.DayNumber - scenario.HorizonStart.DayNumber, 0, nDays)
            : 0;
        ReplayActuals(states, schedule, personIdx);

        for (int i = 0; i < nDays; i++)
        {
            var day = _days[i];

            bool ferryToday = false;
            foreach (var dep in _deps)
            {
                if (i == dep.Depart) { LogFerry(dep, i, outbound: true, schedule); ferryToday = true; }
                if (i == dep.RetFerryStart) { LogFerry(dep, i, outbound: false, schedule); ferryToday = true; }
            }

            if (i < _planFrom)
            {
                if (ferryToday) PlanPeriodicMaintenance(i, schedule);
                DetectDeploymentReturns(i, nDays);
                continue;
            }

            var completed = states.ToDictionary(s => s.C.Id, s => s.Completed);

            var open = new List<CampState>();
            foreach (var cs in states)
            {
                if (cs.Completed || cs.OpenFrom > day) continue;
                bool prereqOk = cs.PrereqIds
                    .All(pid => completed.TryGetValue(pid, out bool ok) && ok);
                if (!prereqOk) { cs.Tally.Prereq++; cs.Log[i] = DayLogCodes.Prereq; continue; }
                if (PrereqReviewPending(cs, i, schedule))
                {
                    cs.Tally.Review++;
                    cs.Log[i] = DayLogCodes.Review;
                    continue;
                }
                if (cs.DeploymentDropped) { cs.Tally.NotUsable++; cs.Log[i] = DayLogCodes.NotUsable; continue; }
                if (cs.C.Venue == CampaignVenue.Simulator)
                {
                    if (!cs.Work[i])
                    {
                        cs.Tally.NotUsable++;
                        cs.Log[i] = DayLogCodes.NotUsable;
                        continue;
                    }
                }
                else if (cs.Dep is null)
                {
                    if (!cs.Work[i] || !(cs.OwnFly ?? scenario.FlyableModel).IsFlyable(day)) { cs.Tally.NotUsable++; cs.Log[i] = DayLogCodes.NotUsable; continue; }
                }
                else
                {
                    bool flyable = cs.Work[i]
                        && (cs.OwnFly ?? cs.Dep.L.FlyableModel ?? scenario.FlyableModel).IsFlyable(day);
                    if (!flyable) { cs.Tally.NotUsable++; cs.Log[i] = DayLogCodes.NotUsable; continue; }
                    if (cs.Dep.L.RangeModel is { } range && !range.IsFlyable(day))
                    {
                        cs.Tally.Range++;
                        cs.Log[i] = DayLogCodes.Range;
                        continue;
                    }
                }
                if (cs.C.MinWorkdaysBetweenSorties > 0 && cs.LastFlyDay >= 0
                    && WorkdaysBetween(cs.WorkPrefix, cs.LastFlyDay, i) < cs.C.MinWorkdaysBetweenSorties)
                {
                    cs.Tally.Review++;
                    cs.Log[i] = DayLogCodes.Review;
                    continue;
                }
                open.Add(cs);
            }
            if (open.Count == 0)
            {
                if (ferryToday) PlanPeriodicMaintenance(i, schedule);
                DetectDeploymentReturns(i, nDays);
                continue;
            }

            if (_hasDatePins && open.Count > 1)
                open = open.OrderBy(cs => cs.NextPinDate == day ? 0 : 1).ToList();

            var dayPlan = new DayPlan(nAc, _bpd);
            var reason = new Dictionary<string, Reason>();
            var placedToday = new Dictionary<string, int>();
            bool flewToday = false;

            bool placedAny = true;
            while (placedAny)
            {
                placedAny = false;
                foreach (var cs in open)
                {
                    if (cs.Completed) continue;
                    if (cs.C.MaxSortiesPerDay > 0
                        && placedToday.GetValueOrDefault(cs.C.Id) >= cs.C.MaxSortiesPerDay) continue;
                    bool placed = cs.C.Venue == CampaignVenue.Simulator
                        ? TryPlaceSimSortie(cs, i, reason, schedule)
                        : TryPlaceSortie(cs, i, dayPlan, reason, schedule);
                    if (!placed) continue;
                    placedAny = true;
                    flewToday = true;
                    placedToday[cs.C.Id] = placedToday.GetValueOrDefault(cs.C.Id) + 1;
                }
            }

            foreach (var cs in open)
            {
                if (placedToday.GetValueOrDefault(cs.C.Id) > 0)
                {
                    cs.Tally.Placed++;
                    cs.Stop[i] = StopCode(cs, placedToday.GetValueOrDefault(cs.C.Id), reason.GetValueOrDefault(cs.C.Id));
                    cs.Log[i] = DayLogCodes.Placed;
                    continue;
                }
                switch (reason.GetValueOrDefault(cs.C.Id))
                {
                    case Reason.Aircraft: cs.Tally.Aircraft++; cs.Log[i] = DayLogCodes.Aircraft; break;
                    case Reason.Pilot: cs.Tally.Pilot++; cs.Log[i] = DayLogCodes.Pilot; break;
                    case Reason.Engineer: cs.Tally.Engineer++; cs.Log[i] = DayLogCodes.Engineer; break;
                    case Reason.Rehearsal: cs.Tally.Rehearsal++; cs.Log[i] = DayLogCodes.Rehearsal; break;
                    case Reason.Simulator: cs.Tally.Simulator++; cs.Log[i] = DayLogCodes.Simulator; break;
                    case Reason.External: cs.Tally.External++; cs.Log[i] = DayLogCodes.External; break;
                    case Reason.Pinned: cs.Tally.Pinned++; cs.Log[i] = DayLogCodes.Pinned; break;
                    default: cs.Log[i] = DayLogCodes.Unknown; break;
                }
            }

            if (flewToday || ferryToday) PlanPeriodicMaintenance(i, schedule);
            DetectDeploymentReturns(i, nDays);
        }

        RenumberSortiesChronologically(schedule);
        if (_hasDatePins) PinnedSortie.DateDiagnostics(scenario, schedule);
        Finalize(schedule, states);
        return schedule;
    }

    private void BuildDeployments(
        Scenario scenario, List<CampState> states, Schedule schedule, int nP, int nAc, int nDays)
    {
        _deps = new List<DepState>();
        _depOfPerson = new int[nP];
        _depOfTail = new int[nAc];
        Array.Fill(_depOfPerson, -1);
        Array.Fill(_depOfTail, -1);

        var locById = scenario.Locations.ToDictionary(l => l.Id);
        int IdxOf(DateOnly d) => Math.Max(0, d.DayNumber - scenario.HorizonStart.DayNumber);

        foreach (var cs in states)
            cs.DeploymentDropped = cs.C.LocationId.Length > 0 && cs.C.Venue != CampaignVenue.Simulator;

        var groups = states
            .Where(cs => cs.C.LocationId.Length > 0 && locById.ContainsKey(cs.C.LocationId)
                         && cs.C.Venue != CampaignVenue.Simulator)
            .GroupBy(cs => cs.C.LocationId)
            .OrderBy(g => g.Min(cs => cs.OpenFrom.DayNumber))
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var g in groups)
        {
            var loc = locById[g.Key];
            var camps = g.ToList();

            var localIds = camps.Select(cs => cs.C.Id).ToHashSet();
            var prereqs = camps.SelectMany(cs => cs.PrereqIds).Where(id => !localIds.Contains(id)).Distinct().ToList();
            if (prereqs.Any(pid => !_prereqDone.ContainsKey(pid)))
                continue;

            int transit = Math.Max(0, loc.FerryDays) + Math.Max(0, loc.SetupDays);
            int flyFrom = Math.Max(camps.Min(cs => IdxOf(cs.OpenFrom)), transit);
            int prereqReady = prereqs
                .Select(pid => FirstOpenAfterReview(PrefixOf(pid), IdxOf(_prereqDone[pid]), _prereqReview.GetValueOrDefault(pid)))
                .DefaultIfEmpty(0).Max();
            if (prereqReady + transit > flyFrom) flyFrom = prereqReady + transit;
            int depart = flyFrom - transit;

            int rehDepth = 1;
            if (!_simPool.BaseHasDevices(loc.Id))
            {
                int needed = Math.Min(PreDeploymentRehearsalLookback,
                    camps.Where(cs => cs.C.RehearsalRequired).Sum(cs => cs.Required));
                var rehLoad = new Dictionary<string, int>();
                foreach (var cs in camps.Where(c => c.C.RehearsalRequired))
                {
                    var c = cs.C;
                    int pilotsPerSortie = EffPilots(c) + Math.Max(0, c.SupportAircraftPerSortie);
                    int perPilot = (int)Math.Ceiling(
                        (double)cs.Required * pilotsPerSortie / Math.Max(1, c.AssignedPilotIds.Count));
                    int eps = EffEngs(c);
                    int perEng = eps > 0
                        ? (int)Math.Ceiling((double)cs.Required * eps / Math.Max(1, c.AssignedEngineerIds.Count))
                        : 0;
                    foreach (var pid in c.AssignedPilotIds) rehLoad[pid] = rehLoad.GetValueOrDefault(pid) + perPilot;
                    foreach (var eid in c.AssignedEngineerIds) rehLoad[eid] = rehLoad.GetValueOrDefault(eid) + perEng;
                }
                rehDepth = Math.Min(PreDeploymentRehearsalLookback,
                    Math.Max(1, rehLoad.Values.DefaultIfEmpty(0).Max()));
                int workdays = 0, idx = 0;
                while (idx < nDays && workdays < needed)
                {
                    if (!_cfg.RehearsalOnWorkdaysOnly || scenario.Calendar.IsWorkday(_days[idx])) workdays++;
                    idx++;
                }
                if (depart < idx)
                {
                    depart = idx;
                    flyFrom = depart + transit;
                }
            }
            if (flyFrom >= nDays) continue;

            var dep = new DepState
            {
                L = loc,
                Camps = camps,
                Depart = depart,
                FlyFrom = flyFrom,
            };
            dep.RehWindowDepth = rehDepth;
            int di = _deps.Count;
            foreach (var cs in dep.Camps)
            {
                cs.Dep = dep;
                cs.DeploymentDropped = false;
                if (_days[flyFrom] > cs.OpenFrom) cs.OpenFrom = _days[flyFrom];
                foreach (var p in cs.PilotIdx.Concat(cs.EngIdx))
                    if (_depOfPerson[p.Value] < 0) { _depOfPerson[p.Value] = di; dep.Persons.Add(p); }
                foreach (var a in cs.CompatIdx.Concat(cs.SupportIdx))
                    if (_depOfTail[a.Value] < 0) { _depOfTail[a.Value] = di; dep.Tails.Add(a); }
            }

            dep.Info = new DeploymentInfo
            {
                LocationId = loc.Id,
                LocationName = loc.Name,
                Departure = _days[dep.Depart],
                FlyFrom = _days[flyFrom],
                AircraftIds = dep.Tails.Select(a => _fleet.IdOf(a)).ToList(),
                PersonIds = dep.Persons.Select(p => _people[p.Value].Id).ToList(),
            };
            schedule.Deployments.Add(dep.Info);
            _deps.Add(dep);
        }
    }

    private void LogFerry(DepState dep, int i, bool outbound, Schedule schedule)
    {
        var crew = dep.Persons.ToList();
        if (crew.Count > 0)
            _ledger.AddSortie(crew, i, dep.L.FerryFlightHours, dep.L.FerryDutyHours, 0);
        foreach (var a in dep.Tails)
        {
            _fleet.AddFerryHours(a, dep.L.FerryFlightHours);
            RecordFailure(a, dep.L.FerryFlightHours, i, schedule);
        }

        schedule.RelocationFlights.Add(new RelocationFlight
        {
            LocationId = dep.L.Id,
            Date = _days[i],
            Outbound = outbound,
            AircraftIds = dep.Tails.Select(a => _fleet.IdOf(a)).ToList(),
            CrewIds = crew.Select(p => _people[p.Value].Id).ToList(),
        });
    }

    private void DetectDeploymentReturns(int i, int nDays)
    {
        foreach (var dep in _deps)
        {
            if (dep.RetFerryStart != int.MaxValue || i < dep.Depart || !dep.AllDone) continue;
            dep.RetFerryStart = i + 1 + Math.Max(0, dep.L.TeardownDays);
            dep.RetEnd = dep.RetFerryStart + Math.Max(1, dep.L.FerryDays) - 1;
            if (dep.RetEnd < nDays) dep.Info.ReturnEnd = _days[dep.RetEnd];
            int rest = Math.Max(0, dep.L.PostDeploymentRestDays);
            if (rest > 0 && dep.RetEnd + 1 < nDays)
            {
                int restEnd = dep.RetEnd + rest;
                foreach (var p in dep.Persons) _ledger.AddLeave(p, dep.RetEnd + 1, restEnd);
                if (restEnd < nDays) dep.Info.RestEnd = _days[restEnd];
            }
        }
    }

    private bool AwayFromMain(PersonIndex p, int i)
    {
        int d = _depOfPerson[p.Value];
        return d >= 0 && i >= _deps[d].Depart && i <= _deps[d].RetEnd;
    }

    private bool PersonAtCampaignBase(PersonIndex p, CampState cs, int i)
    {
        if (cs.Dep is null) return !AwayFromMain(p, i);
        int d = _depOfPerson[p.Value];
        return d >= 0 && ReferenceEquals(_deps[d], cs.Dep) && i >= cs.Dep.FlyFrom;
    }

    private bool TailAtCampaignBase(TailIndex a, CampState cs, int i)
    {
        int d = _depOfTail[a.Value];
        if (cs.Dep is null) return d < 0 || i < _deps[d].Depart || i > _deps[d].RetEnd;
        return d >= 0 && ReferenceEquals(_deps[d], cs.Dep) && i >= cs.Dep.FlyFrom;
    }

    private static void RenumberSortiesChronologically(Schedule schedule)
    {
        foreach (var byCampaign in schedule.Sorties.GroupBy(s => s.CampaignId))
        {
            var ordered = byCampaign
                .OrderBy(s => s.Date)
                .ThenBy(s => s.Block)
                .ThenBy(s => s.AircraftId, StringComparer.Ordinal)
                .ToList();

            var remap = new Dictionary<int, int>(ordered.Count);
            for (int n = 0; n < ordered.Count; n++)
            {
                remap[ordered[n].SortieNo] = n + 1;
                ordered[n].SortieNo = n + 1;
            }

            foreach (var r in schedule.Rehearsals.Where(r => r.CampaignId == byCampaign.Key))
                if (remap.TryGetValue(r.SortieNo, out int renumbered))
                    r.SortieNo = renumbered;
        }

        foreach (var byCampaign in schedule.SimulatorSorties.GroupBy(s => s.CampaignId))
        {
            var ordered = byCampaign
                .OrderBy(s => s.Date)
                .ThenBy(s => s.Block)
                .ThenBy(s => s.SimulatorId, StringComparer.Ordinal)
                .ToList();
            for (int n = 0; n < ordered.Count; n++) ordered[n].SortieNo = n + 1;
        }
    }

    private static List<CampState> BuildStates(Scenario scenario, Schedule schedule)
    {
        DateOnly DeadlineKey(Campaign c) =>
            CampaignDeadlines.EffectiveTarget(scenario, c) ?? DateOnly.MaxValue;

        double OrderKey(Campaign c) => c.PriorityKey ?? DeadlineKey(c).DayNumber;

        var windowEnd = scenario.Aircraft
            .SelectMany(a => a.MaintenanceWindows)
            .ToDictionary(w => w.Id, w => w.End);

        var states = new List<CampState>();
        foreach (var c in scenario.Campaigns.OrderBy(OrderKey))
        {
            double inflation = scenario.Config.SimulateYieldInNominal
                ? c.ExpectedInflation(scenario.Config.AbortOverrideOrNull)
                : 1.0;
            double neededTp = c.TotalTestPoints * inflation;

            var openFrom = c.StartDate;
            foreach (var id in c.PrerequisiteMaintenanceIds)
                if (windowEnd.TryGetValue(id, out var end) && end.AddDays(1) > openFrom)
                    openFrom = end.AddDays(1);

            var tally = new BottleneckTally();
            schedule.Bottlenecks[c.Id] = tally;
            states.Add(new CampState
            {
                C = c,
                Tally = tally,
                Required = c.TestPointsPerSortie <= 0
                    ? 0
                    : (int)Math.Ceiling(neededTp / c.TestPointsPerSortie),
                RemainingTp = c.TestPointsPerSortie <= 0 ? 0 : neededTp,
                OpenFrom = openFrom,
                PrereqIds = CampaignDeadlines.EffectivePrerequisiteCampaignIds(scenario, c),
            });
        }
        return states;
    }

    private void ReplayActuals(
        List<CampState> states, Schedule schedule, Dictionary<string, PersonIndex> personIdx)
    {
        if (_sc.ActualSorties.Count == 0 && _sc.ActualRehearsals.Count == 0
            && _sc.ActualSimulatorSorties.Count == 0) return;

        var stateById = states.ToDictionary(s => s.C.Id);
        bool replayedAny = false;

        foreach (var a in _sc.ActualSorties.OrderBy(s => s.Date).ThenBy(s => s.Block))
        {
            if (a.Date < _sc.HorizonStart || a.Date > _sc.HorizonEnd) continue;
            if (!stateById.TryGetValue(a.CampaignId, out var cs)) continue;
            int i = a.Date.DayNumber - _sc.HorizonStart.DayNumber;
            var camp = cs.C;

            var crew = ResolveCrew(a.PilotId,
                a.SupportPilotIds.Concat(a.AdditionalPilotIds), a.EngineerIds, personIdx);
            _ledger.AddSortie(crew, i,
                camp.ExpectedSortieFlightHours, camp.ExpectedSortieDutyHours,
                camp.ExpectedSortieFlightDutyHours);
            int blk = Math.Clamp(a.Block, 1, _bpd);
            foreach (var p in crew) _ledger.OccupyBlock(p, blk - 1, i);

            foreach (var tail in a.SupportAircraftIds.Prepend(a.AircraftId))
                if (_fleet.TryIndex(tail, out var t))
                    _fleet.RecordSortie(t, camp.ExpectedSortieFlightHours);
            if (camp.PreferSingleAircraft && cs.LockedTail is null && _fleet.TryIndex(a.AircraftId, out var locked))
                cs.LockedTail = locked;

            if (!a.Aborted && a.TestPointsFlown > 0)
            {
                double tp = Math.Min(a.TestPointsFlown, cs.RemainingTp);
                cs.RemainingTp -= tp;
                cs.GoodDone += tp;
            }
            cs.Done++;
            cs.LastFlyDay = Math.Max(cs.LastFlyDay, i);
            replayedAny = true;

            schedule.Sorties.Add(new Sortie
            {
                CampaignId = a.CampaignId,
                Date = a.Date,
                Block = a.Block,
                SortieNo = a.SortieNo,
                AircraftId = a.AircraftId,
                PilotId = a.PilotId,
                AdditionalPilotIds = new List<string>(a.AdditionalPilotIds),
                EngineerIds = new List<string>(a.EngineerIds),
                TestPointsFlown = a.TestPointsFlown,
                Aborted = a.Aborted,
                IsActual = true,
                RehearsalDate = a.RehearsalDate,
                SupportAircraftIds = new List<string>(a.SupportAircraftIds),
                SupportPilotIds = new List<string>(a.SupportPilotIds),
                ExternalSupportIds = new List<string>(a.ExternalSupportIds),
            });
            if (cs.Completed)
            {
                schedule.CampaignEndDates[camp.Id] = a.Date;
                cs.EndDay = i;
            }
        }

        var flownKeys = new HashSet<(string, int)>(
            _sc.ActualSorties.Select(s => (s.CampaignId, s.SortieNo)));

        foreach (var r in _sc.ActualRehearsals.OrderBy(x => x.Date).Select(r => RehearsalHistory.Copy(r, _sc.ActualSorties)))
        {
            if (r.Date < _sc.HorizonStart || r.Date > _sc.HorizonEnd) continue;
            if (!stateById.TryGetValue(r.CampaignId, out var cs)) continue;
            int k = r.Date.DayNumber - _sc.HorizonStart.DayNumber;

            var crew = ResolveCrew(r.PilotId, r.AdditionalPilotIds.Concat(r.SupportPilotIds), r.EngineerIds, personIdx);
            _ledger.AddRehearsal(crew, k, cs.C.ExpectedRehearsalDutyHours);
            int blk = Math.Clamp(r.Block, 1, _bpd);
            foreach (var p in crew) _ledger.OccupyBlock(p, blk - 1, k);
            if (!flownKeys.Contains((r.CampaignId, r.SortieNo))) cs.BankedRehearsals.Add(r);
            replayedAny = true;

            r.IsActual = true;
            schedule.Rehearsals.Add(r);
        }

        foreach (var a in _sc.ActualSimulatorSorties.OrderBy(s => s.Date).ThenBy(s => s.Block))
        {
            if (a.Date < _sc.HorizonStart || a.Date > _sc.HorizonEnd) continue;
            if (!stateById.TryGetValue(a.CampaignId, out var cs)) continue;
            int i = a.Date.DayNumber - _sc.HorizonStart.DayNumber;
            var camp = cs.C;

            var crew = ResolveCrew(a.PilotId, a.AdditionalPilotIds, a.EngineerIds, personIdx);
            _ledger.AddSimSession(crew, i, camp.ExpectedSortieDutyHours);
            int bps = Math.Clamp(camp.BlocksPerSortie, 1, _bpd);
            int blk0 = Math.Clamp(a.Block, 1, _bpd) - 1;
            foreach (var p in crew)
                for (int b = blk0; b < blk0 + bps && b < _bpd; b++) _ledger.OccupyBlock(p, b, i);

            if (!a.Aborted && a.TestPointsFlown > 0)
            {
                double tp = Math.Min(a.TestPointsFlown, cs.RemainingTp);
                cs.RemainingTp -= tp;
                cs.GoodDone += tp;
            }
            cs.Done++;
            cs.LastFlyDay = Math.Max(cs.LastFlyDay, i);
            replayedAny = true;

            schedule.SimulatorSorties.Add(new SimulatorSortie
            {
                CampaignId = a.CampaignId,
                Date = a.Date,
                Block = a.Block,
                SortieNo = a.SortieNo,
                SimulatorId = a.SimulatorId,
                PilotId = a.PilotId,
                AdditionalPilotIds = new List<string>(a.AdditionalPilotIds),
                EngineerIds = new List<string>(a.EngineerIds),
                TestPointsFlown = a.TestPointsFlown,
                Aborted = a.Aborted,
                IsActual = true,
            });
            if (cs.Completed)
            {
                schedule.CampaignEndDates[camp.Id] = a.Date;
                cs.EndDay = i;
            }
        }

        if (replayedAny && _planFrom > 0)
            PlanPeriodicMaintenance(_planFrom - 1, schedule);
    }

    private static List<PersonIndex> ResolveCrew(
        string pilotId, IEnumerable<string> extraPilotIds, IReadOnlyList<string> engineerIds,
        Dictionary<string, PersonIndex> personIdx)
    {
        var crew = new List<PersonIndex>();
        if (personIdx.TryGetValue(pilotId, out var p)) crew.Add(p);
        foreach (var id in extraPilotIds)
            if (personIdx.TryGetValue(id, out var sp)) crew.Add(sp);
        foreach (var id in engineerIds)
            if (personIdx.TryGetValue(id, out var e)) crew.Add(e);
        return crew;
    }

    private static List<PersonIndex> ResolvePersons(
        IEnumerable<string> ids, Dictionary<string, PersonIndex> index)
    {
        var list = new List<PersonIndex>();
        foreach (var id in ids)
            if (index.TryGetValue(id, out var p)) list.Add(p);
        return list;
    }

    private bool TryPlaceSortie(
        CampState cs, int i, DayPlan dayPlan, Dictionary<string, Reason> reason, Schedule schedule)
    {
        if (cs.NextPinDate is { } pinDate && _days[i] < pinDate) { reason[cs.C.Id] = Reason.Pinned; return false; }
        var camp = cs.C;
        int bps = Math.Clamp(camp.BlocksPerSortie, 1, _bpd);

        int bpdEff = cs.Dep?.L.BlocksPerDayCap is int cap ? Math.Clamp(cap, 1, _bpd) : _bpd;
        var allowed = (camp.AllowedBlocks.Count > 0
                ? camp.AllowedBlocks.Where(b => b >= 1 && b <= _bpd)
                : Enumerable.Range(1, _bpd))
            .Where(b => b + bps - 1 <= bpdEff)
            .ToList();
        if (allowed.Count == 0)
        {
            reason[camp.Id] = Reason.Aircraft;
            return false;
        }

        double fH = camp.ExpectedSortieFlightHours;
        double dH = camp.ExpectedSortieDutyHours;
        double fdH = camp.ExpectedSortieFlightDutyHours;
        double rH = camp.ExpectedRehearsalDutyHours;

        var pin = cs.NextPin;
        if (pin is { Unsatisfiable: true })
        {
            reason[camp.Id] = Reason.Pinned;
            return false;
        }

        var ordered = OrderedAircraft(cs, i);
        (int AircraftIdx, int Block)? slot;
        if (cs.Pins is null)
            slot = dayPlan.FindSlot(ordered.Select(t => t.Value).ToList(), allowed, bps);
        else
        {
            if (pin?.Tail is { } pt) ordered = ordered.Where(t => t.Equals(pt)).ToList();
            slot = null;
            foreach (var t in ordered)
            {
                foreach (int b in allowed)
                    if (dayPlan.SpanFree(t.Value, b, bps) && SameDayOrderOk(cs, i, b, _fleet.IdOf(t))
                        && (pin is null || PinFits(pin, cs, t, b, bps, i, dayPlan, fH, dH, fdH)))
                    {
                        slot = (t.Value, b);
                        break;
                    }
                if (slot is not null) break;
            }
        }
        if (slot is null)
        {
            reason[camp.Id] = pin is null ? Reason.Aircraft : Reason.Pinned;
            return false;
        }
        (int aRaw, int blk) = slot.Value;
        var aIdx = new TailIndex(aRaw);

        int supportN = Math.Max(0, camp.SupportAircraftPerSortie);
        var supportTails = new List<TailIndex>();
        if (supportN > 0)
        {
            var pinTails = pin?.SupportTails ?? NoTails;
            supportTails = pinTails.Concat(cs.SupportIdx
                .Where(a => !pinTails.Contains(a)
                            && !a.Equals(aIdx) && !_fleet.InMaintenance(a, i) && !TailResting(a, i)
                            && TailAtCampaignBase(a, cs, i)
                            && dayPlan.SpanFree(a.Value, blk, bps)
                            && cs.PilotIdx.Any(p => IsRated(p, a)))
                .OrderBy(a => _fleet.Use(a))
                .Take(Math.Max(0, supportN - pinTails.Count)))
                .ToList();
            if (supportTails.Count < supportN)
            {
                reason[camp.Id] = Reason.Aircraft;
                return false;
            }
        }

        if (cs.ExtUnsatisfiable || !_extPool.AllFree(cs.ExtIdx, blk - 1, bps, i))
        {
            reason[camp.Id] = Reason.External;
            return false;
        }

        int needPilots = Math.Max(EffPilots(camp), _fleet.MinPilots(aIdx));
        var pinPilots = pin?.Pilots ?? NoPersons;
        var pilots = pinPilots.Concat(cs.PilotIdx
            .Where(p => (pin is null || !pin.Reserved(p))
                        && IsRated(p, aIdx) && PersonAtCampaignBase(p, cs, i) && EligibleForSortie(p, cs, i, blk, fH, dH, fdH))
            .OrderBy(p => _ledger.RecentDuty(p, i))
            .Take(Math.Max(0, needPilots - pinPilots.Count)))
            .ToList();
        if (pilots.Count < needPilots)
        {
            reason[camp.Id] = Reason.Pilot;
            return false;
        }

        var supportPilots = new List<PersonIndex>();
        if (supportN > 0)
        {
            var used = new HashSet<PersonIndex>(pilots);
            var pinSp = pin?.SupportPilots ?? NoPersons;
            foreach (var st in supportTails)
            {
                int needSp = Math.Max(1, _fleet.MinPilots(st));
                var sp = pinSp
                    .Where(p => !used.Contains(p) && IsRated(p, st))
                    .Take(needSp)
                    .Concat(cs.PilotIdx
                        .Where(p => !used.Contains(p) && (pin is null || !pin.Reserved(p))
                                    && IsRated(p, st) && PersonAtCampaignBase(p, cs, i)
                                    && EligibleForSortie(p, cs, i, blk, fH, dH, fdH))
                        .OrderBy(p => _ledger.RecentDuty(p, i)))
                    .Take(needSp)
                    .ToList();
                if (sp.Count < needSp)
                {
                    reason[camp.Id] = Reason.Pilot;
                    return false;
                }
                supportPilots.AddRange(sp);
                foreach (var p in sp) used.Add(p);
            }
            if (pinSp.Any(p => !used.Contains(p)))
            {
                reason[camp.Id] = Reason.Pinned;
                return false;
            }
        }

        int needEngs = EffEngs(camp);
        var pinEngs = pin?.Engineers ?? NoPersons;
        var engineers = pinEngs.Concat(cs.EngIdx
            .Where(p => (pin is null || !pin.Reserved(p))
                        && IsRated(p, aIdx) && PersonAtCampaignBase(p, cs, i) && EligibleForSortie(p, cs, i, blk, fH, dH, fdH))
            .OrderBy(p => _ledger.RecentDuty(p, i))
            .Take(Math.Max(0, needEngs - pinEngs.Count)))
            .ToList();
        if (engineers.Count < needEngs)
        {
            reason[camp.Id] = Reason.Engineer;
            return false;
        }

        var crew = pilots.Concat(supportPilots).Concat(engineers).ToList();

        _ledger.AddSortie(crew, i, fH, dH, fdH);

        int? k = null;
        int rehBlk = 0;
        SimIndex? rehSim = null;
        Rehearsal? consumedBank = null;
        if (camp.RehearsalRequired)
        {
            if (cs.BankedRehearsals.FirstOrDefault(r => RehearsalHistory.Matches(r,
                crew.Select(p => _people[p.Value].Id), cs.Done + 1, _days[i], d => _sc.Calendar.IsWorkday(cs.C, d), cs.Cfg,
                camp.LocationId, cs.Dep is { } dep && !_simPool.BaseHasDevices(dep.L.Id)
                    ? _sc.HorizonStart.AddDays(dep.Depart) : null, PreDeploymentRehearsalLookback)) is { } held)
            {
                cs.BankedRehearsals.Remove(held);
                consumedBank = held;
            }
            else
            {
                var reh = FindRehearsalSlot(i, crew, rH, cs);
                if (reh is null)
                {
                    _ledger.RemoveSortie(crew, i, fH, dH, fdH);
                    reason[camp.Id] = Reason.Rehearsal;
                    if (_capture)
                        Log(i, _days[i], cs, DecisionKind.Rollback,
                            $"Considered a sortie on {_fleet.IdOf(aIdx)} (block {blk}) but rolled it back: " +
                            $"no rehearsal slot fit within {cs.Cfg.MaxRehearsalGapDays} working day(s) before it (§12).");
                    return false;
                }
                (k, rehBlk, rehSim) = (reh.Value.Day, reh.Value.Block, reh.Value.Sim);
            }
        }

        if (k is { } rehDay) _ledger.AddRehearsal(crew, rehDay, rH);
        dayPlan.Occupy(aIdx.Value, blk, bps);
        foreach (var sa in supportTails) dayPlan.Occupy(sa.Value, blk, bps);
        _extPool.Reserve(cs.ExtIdx, blk - 1, bps, i);
        foreach (var p in crew)
        {
            _ledger.OccupyBlock(p, blk - 1, i);
            if (k is { } rd) _ledger.OccupyBlock(p, rehBlk - 1, rd);
        }
        if (rehSim is { } simDev && k is { } simDay)
        {
            _simPool.Book(simDev, rehBlk - 1, simDay);
            if (_simPool.CheckFailure(simDev, rH, simDay, _failure) is { } repair)
            {
                string id = _simPool.IdOf(simDev);
                if (!schedule.SimulatorRepairs.TryGetValue(id, out var list))
                    schedule.SimulatorRepairs[id] = list = new List<DateRange>();
                list.Add(repair);
            }
        }

        _fleet.RecordSortie(aIdx, fH);
        RecordFailure(aIdx, fH, i, schedule);
        foreach (var sa in supportTails)
        {
            _fleet.RecordSortie(sa, fH);
            RecordFailure(sa, fH, i, schedule);
        }
        if (camp.PreferSingleAircraft && cs.LockedTail is null)
            cs.LockedTail = aIdx;
        if (cs.Pins is not null) (cs.OrderDay, cs.OrderBlock, cs.OrderKey) = (i, blk, _fleet.IdOf(aIdx));

        double attempted = Math.Min(camp.TestPointsPerSortie, cs.RemainingTp);
        var outcome = _yield.Evaluate(camp, attempted, cs.GoodDone);
        double tpFlown = outcome.Aborted ? 0 : Math.Min(outcome.GoodPoints, cs.RemainingTp);
        cs.RemainingTp -= tpFlown;
        cs.GoodDone += tpFlown;
        cs.Done++;
        cs.LastFlyDay = i;

        var pilotId = _people[pilots[0].Value].Id;
        var additionalPilotIds = pilots.Skip(1).Select(p => _people[p.Value].Id).ToList();
        var engineerIds = engineers.Select(p => _people[p.Value].Id).ToList();
        schedule.Sorties.Add(new Sortie
        {
            CampaignId = camp.Id,
            Date = _days[i],
            Block = blk,
            SortieNo = cs.Done,
            AircraftId = _fleet.IdOf(aIdx),
            PilotId = pilotId,
            AdditionalPilotIds = additionalPilotIds,
            EngineerIds = engineerIds,
            TestPointsFlown = tpFlown,
            Aborted = outcome.Aborted,
            RehearsalDate = k is { } rkd ? _days[rkd] : consumedBank?.Date,
            SupportAircraftIds = supportTails.Select(a => _fleet.IdOf(a)).ToList(),
            SupportPilotIds = supportPilots.Select(p => _people[p.Value].Id).ToList(),
            ExternalSupportIds = cs.ExtIdx.Select(e => _extPool.IdOf(e)).ToList(),
        });
        if (k is { } rehK)
            schedule.Rehearsals.Add(new Rehearsal
            {
                CampaignId = camp.Id,
                Date = _days[rehK],
                Block = rehBlk,
                SortieNo = cs.Done,
                SortieDate = _days[i],
                PilotId = pilotId,
                AdditionalPilotIds = new(additionalPilotIds),
                SupportPilotIds = supportPilots.Select(p => _people[p.Value].Id).ToList(),
                SimulatorId = rehSim is { } device ? _simPool.IdOf(device) : "",
                LocationId = cs.Dep is { } rehearsalDep && rehK >= rehearsalDep.FlyFrom ? rehearsalDep.L.Id : "",
                EngineerIds = engineerIds,
            });

        if (cs.Completed)
        {
            schedule.CampaignEndDates[camp.Id] = _days[i];
            cs.EndDay = i;
        }

        if (_capture)
        {
            string blockText = bps > 1 ? $"blocks {blk}-{blk + bps - 1}" : $"block {blk}";
            var parts = new List<string> { $"pilot {_people[pilots[0].Value].Name}" };
            if (additionalPilotIds.Count > 0) parts.Add($"+{additionalPilotIds.Count} deck pilot(s)");
            parts.Add(engineers.Count == 1 ? "1 engineer" : $"{engineers.Count} engineers");
            if (supportTails.Count > 0) parts.Add($"{supportTails.Count} support ship(s)");
            if (cs.ExtIdx.Count > 0) parts.Add($"{cs.ExtIdx.Count} external support");
            string reh = k is { } rk ? $", rehearsal {_days[rk]:yyyy-MM-dd}" : ", no rehearsal";
            string tp = outcome.Aborted ? " · ABORTED (0 points)" : $" · {tpFlown:0.##} test points";
            string loc = cs.Dep is { } dep ? $" at {dep.L.Name}" : "";
            Log(i, _days[i], cs, DecisionKind.Placed,
                $"Placed sortie #{cs.Done} on {_fleet.IdOf(aIdx)}{loc} ({blockText}), " +
                $"{string.Join(", ", parts)}{reh}{tp}.");
        }
        return true;
    }

    private bool TryPlaceSimSortie(CampState cs, int i, Dictionary<string, Reason> reason, Schedule schedule)
    {
        if (cs.NextPinDate is { } pinDate && _days[i] < pinDate) { reason[cs.C.Id] = Reason.Pinned; return false; }
        var camp = cs.C;
        int bps = Math.Clamp(camp.BlocksPerSortie, 1, _bpd);

        var allowed = (camp.AllowedBlocks.Count > 0
                ? camp.AllowedBlocks.Where(b => b >= 1 && b <= _bpd)
                : Enumerable.Range(1, _bpd))
            .Where(b => b + bps - 1 <= _bpd)
            .ToList();
        if (allowed.Count == 0) { reason[camp.Id] = Reason.Simulator; return false; }

        const string baseId = "";
        if (!_simPool.BaseHasDevices(baseId)) { reason[camp.Id] = Reason.Simulator; return false; }

        double dH = camp.ExpectedSortieDutyHours;

        var pin = cs.NextPin;
        if (pin is { Unsatisfiable: true }) { reason[camp.Id] = Reason.Pinned; return false; }
        bool anySim = cs.AnySim;
        var simSet = cs.SimSet;
        if (pin?.Sim is { } pinnedSim) (anySim, simSet) = (false, new HashSet<SimIndex> { pinnedSim });

        SimIndex? dev = null;
        int startBlk = 0;
        foreach (int b in allowed)
            if (_simPool.FindFreeSpan(baseId, b - 1, bps, i, anySim, simSet) is { } s
                && (cs.Pins is null
                    || (SameDayOrderOk(cs, i, b, _simPool.IdOf(s))
                        && (pin is null || pin.Pilots.Concat(pin.Engineers)
                            .All(p => EligibleForSimSession(p, cs, i, b - 1, bps, dH))))))
            {
                dev = s;
                startBlk = b;
                break;
            }
        if (dev is not { } simDev) { reason[camp.Id] = pin is null ? Reason.Simulator : Reason.Pinned; return false; }

        int startBlk0 = startBlk - 1;

        int needPilots = Math.Max(0, camp.PilotsPerSortie);
        var pinPilots = pin?.Pilots ?? NoPersons;
        var pilots = pinPilots.Concat(cs.PilotIdx
            .Where(p => (pin is null || !pin.Reserved(p)) && EligibleForSimSession(p, cs, i, startBlk0, bps, dH))
            .OrderBy(p => _ledger.RecentDuty(p, i))
            .Take(Math.Max(0, needPilots - pinPilots.Count)))
            .ToList();
        if (pilots.Count < needPilots) { reason[camp.Id] = Reason.Pilot; return false; }

        int needEngs = EffEngs(camp);
        var pinEngs = pin?.Engineers ?? NoPersons;
        var engineers = pinEngs.Concat(cs.EngIdx
            .Where(p => (pin is null || !pin.Reserved(p)) && EligibleForSimSession(p, cs, i, startBlk0, bps, dH))
            .OrderBy(p => _ledger.RecentDuty(p, i))
            .Take(Math.Max(0, needEngs - pinEngs.Count)))
            .ToList();
        if (engineers.Count < needEngs) { reason[camp.Id] = Reason.Engineer; return false; }

        var crew = pilots.Concat(engineers).ToList();

        _ledger.AddSimSession(crew, i, dH);
        _simPool.BookSpan(simDev, startBlk0, bps, i);
        if (cs.Pins is not null) (cs.OrderDay, cs.OrderBlock, cs.OrderKey) = (i, startBlk, _simPool.IdOf(simDev));
        foreach (var p in crew)
            for (int b = startBlk0; b < startBlk0 + bps; b++) _ledger.OccupyBlock(p, b, i);
        if (_simPool.CheckFailure(simDev, dH, i, _failure) is { } repair)
        {
            string id = _simPool.IdOf(simDev);
            if (!schedule.SimulatorRepairs.TryGetValue(id, out var list))
                schedule.SimulatorRepairs[id] = list = new List<DateRange>();
            list.Add(repair);
        }

        double attempted = Math.Min(camp.TestPointsPerSortie, cs.RemainingTp);
        var outcome = _yield.Evaluate(camp, attempted, cs.GoodDone);
        double tpFlown = outcome.Aborted ? 0 : Math.Min(outcome.GoodPoints, cs.RemainingTp);
        cs.RemainingTp -= tpFlown;
        cs.GoodDone += tpFlown;
        cs.Done++;
        cs.LastFlyDay = i;

        var pilotId = pilots.Count > 0 ? _people[pilots[0].Value].Id : string.Empty;
        var additionalPilotIds = pilots.Skip(1).Select(p => _people[p.Value].Id).ToList();
        var engineerIds = engineers.Select(p => _people[p.Value].Id).ToList();
        schedule.SimulatorSorties.Add(new SimulatorSortie
        {
            CampaignId = camp.Id,
            Date = _days[i],
            Block = startBlk,
            SortieNo = cs.Done,
            SimulatorId = _simPool.IdOf(simDev),
            PilotId = pilotId,
            AdditionalPilotIds = additionalPilotIds,
            EngineerIds = engineerIds,
            TestPointsFlown = tpFlown,
            Aborted = outcome.Aborted,
        });

        if (cs.Completed)
        {
            schedule.CampaignEndDates[camp.Id] = _days[i];
            cs.EndDay = i;
        }

        if (_capture)
        {
            string blockText = bps > 1 ? $"blocks {startBlk}-{startBlk + bps - 1}" : $"block {startBlk}";
            var parts = new List<string>();
            if (pilots.Count > 0) parts.Add(pilots.Count == 1 ? "1 pilot" : $"{pilots.Count} pilots");
            parts.Add(engineers.Count == 1 ? "1 engineer" : $"{engineers.Count} engineers");
            string tp = outcome.Aborted ? " · ABORTED (0 points)" : $" · {tpFlown:0.##} test points";
            Log(i, _days[i], cs, DecisionKind.Placed,
                $"Placed sim session #{cs.Done} on {_simPool.IdOf(simDev)} ({blockText}), " +
                $"{string.Join(", ", parts)}{tp}.");
        }
        return true;
    }

    private bool EligibleForSimSession(PersonIndex p, CampState cs, int i, int startBlk0, int bps, double dH)
    {
        if (PersonResting(p, i)) return false;
        if (_ledger.IsOnLeave(p, i)) return false;
        if (!PersonAtCampaignBase(p, cs, i)) return false;
        for (int b = startBlk0; b < startBlk0 + bps; b++)
            if (_ledger.IsBlockBusy(p, b, i)) return false;
        return _ledger.CanSimSession(p, i, dH, cs.Cfg);
    }

    private static readonly List<PersonIndex> NoPersons = new();
    private static readonly List<TailIndex> NoTails = new();

    private static bool SameDayOrderOk(CampState cs, int i, int blk, string resourceId) =>
        cs.OrderDay != i || PinnedSortie.SortsAfter(blk, resourceId, cs.OrderBlock, cs.OrderKey);

    private bool PinFits(PinnedSortie pin, CampState cs, TailIndex t, int b, int bps, int i, DayPlan dayPlan,
        double fH, double dH, double fdH)
    {
        foreach (var p in pin.Pilots.Concat(pin.Engineers))
            if (!IsRated(p, t) || !PersonAtCampaignBase(p, cs, i) || !EligibleForSortie(p, cs, i, b, fH, dH, fdH))
                return false;
        foreach (var p in pin.SupportPilots)
            if (!PersonAtCampaignBase(p, cs, i) || !EligibleForSortie(p, cs, i, b, fH, dH, fdH))
                return false;
        foreach (var st in pin.SupportTails)
            if (st.Equals(t) || _fleet.InMaintenance(st, i) || TailResting(st, i)
                || !TailAtCampaignBase(st, cs, i) || !dayPlan.SpanFree(st.Value, b, bps))
                return false;
        return true;
    }

    private void Log(int dayIndex, DateOnly? date, CampState? cs, DecisionKind kind, string message) =>
        _decisions.Add(new DecisionEntry(dayIndex, date, cs?.C.Id, cs?.C.Name, kind, message));

    private bool EligibleForSortie(PersonIndex p, CampState cs, int i, int blk, double fH, double dH, double fdH) =>
        !PersonResting(p, i)
        && !_ledger.IsOnLeave(p, i)
        && !_ledger.IsBlockBusy(p, blk - 1, i)
        && _ledger.CanSortie(p, i, fH, dH, fdH, cs.Cfg);

    private void BuildRestRouting(List<CampState> states, int nP, int nAc)
    {
        _restCampsOfPerson = null;
        _restCampsOfTail = null;
        if (!states.Any(s => s.C.RestDaysAfterCompletion > 0)) return;
        _restCampsOfPerson = new List<CampState>[nP];
        _restCampsOfTail = new List<CampState>[nAc];
        for (int p = 0; p < nP; p++) _restCampsOfPerson[p] = new List<CampState>();
        for (int a = 0; a < nAc; a++) _restCampsOfTail[a] = new List<CampState>();
        foreach (var cs in states)
        {
            if (cs.C.RestDaysAfterCompletion <= 0) continue;
            foreach (var p in cs.PilotIdx.Concat(cs.EngIdx))
                if (!_restCampsOfPerson[p.Value].Contains(cs)) _restCampsOfPerson[p.Value].Add(cs);
            foreach (var t in cs.CompatIdx.Concat(cs.SupportIdx))
                if (!_restCampsOfTail[t.Value].Contains(cs)) _restCampsOfTail[t.Value].Add(cs);
        }
    }

    private bool PersonResting(PersonIndex p, int i)
    {
        if (_restCampsOfPerson is null) return false;
        foreach (var cs in _restCampsOfPerson[p.Value])
            if (cs.Completed && cs.EndDay >= 0 && i > cs.EndDay && i - cs.EndDay <= cs.C.RestDaysAfterCompletion)
                return true;
        return false;
    }

    private bool TailResting(TailIndex t, int i)
    {
        if (_restCampsOfTail is null) return false;
        foreach (var cs in _restCampsOfTail[t.Value])
            if (cs.Completed && cs.EndDay >= 0 && i > cs.EndDay && i - cs.EndDay <= cs.C.RestDaysAfterCompletion)
                return true;
        return false;
    }

    private void PlanPeriodicMaintenance(int i, Schedule schedule)
    {
        foreach (var (tailId, window) in _fleet.PlanPeriodic(i))
        {
            if (!schedule.AutoMaintenance.TryGetValue(tailId, out var list))
                schedule.AutoMaintenance[tailId] = list = new List<DateRange>();
            list.Add(window);
        }
    }

    private void RecordFailure(TailIndex t, double flightHours, int i, Schedule schedule)
    {
        if (_fleet.CheckFailure(t, flightHours, i, _failure) is not { } repair) return;
        string id = _fleet.IdOf(t);
        if (!schedule.UnscheduledRepairs.TryGetValue(id, out var list))
            schedule.UnscheduledRepairs[id] = list = new List<DateRange>();
        list.Add(repair);
    }

    private static int WorkdaysBetween(int[] prefix, int a, int b) =>
        b - 1 <= a ? 0 : prefix[b - 1] - prefix[a];

    private int[] PrefixOf(string id) => _workPrefixOf.GetValueOrDefault(id) ?? _workdayPrefix;

    private int DayIdx(DateOnly d) => Math.Max(0, d.DayNumber - _sc.HorizonStart.DayNumber);

    private bool PrereqReviewPending(CampState cs, int i, Schedule schedule)
    {
        if (_prereqReview.Count == 0) return false;
        foreach (var pid in cs.PrereqIds)
            if (_prereqReview.TryGetValue(pid, out int days)
                && schedule.CampaignEndDates.TryGetValue(pid, out var end) && end is { } e
                && WorkdaysBetween(PrefixOf(pid), DayIdx(e), i) < days)
                return true;
        return false;
    }

    private int FirstOpenAfterReview(int[] prefix, int endIdx, int reviewDays)
    {
        int i = endIdx + 1;
        while (reviewDays > 0 && i < _days.Count && WorkdaysBetween(prefix, endIdx, i) < reviewDays) i++;
        return i;
    }

    private List<TailIndex> OrderedAircraft(CampState cs, int i)
    {
        IEnumerable<TailIndex> avail = cs.CompatIdx
            .Where(a => !_fleet.InMaintenance(a, i) && !TailResting(a, i)
                        && TailAtCampaignBase(a, cs, i) && CrewTypeRatedFor(cs, a));
        if (cs.C.PreferSingleAircraft && cs.LockedTail is { } locked)
            avail = avail.Where(a => a.Equals(locked));
        return avail.OrderBy(a => _fleet.Use(a)).ToList();
    }

    private bool IsRated(PersonIndex p, TailIndex t) => _rated[p.Value][t.Value];

    private int EffPilots(Campaign c) => Math.Max(1, c.PilotsPerSortie > 0 ? c.PilotsPerSortie : _cfg.PilotsPerSortie);
    private int EffEngs(Campaign c) => c.EngineersPerSortie > 0 ? c.EngineersPerSortie : _cfg.EngineersPerSortie;

    private bool CrewTypeRatedFor(CampState cs, TailIndex t)
    {
        int tailNeedP = Math.Max(EffPilots(cs.C), _fleet.MinPilots(t));
        int needP = Math.Min(tailNeedP, cs.PilotIdx.Count);
        int needE = Math.Min(EffEngs(cs.C), cs.EngIdx.Count);
        return cs.PilotIdx.Count(p => IsRated(p, t)) >= needP
            && cs.EngIdx.Count(e => IsRated(e, t)) >= needE;
    }

    private (int Day, int Block, SimIndex? Sim)? FindRehearsalSlot(int i, List<PersonIndex> crew, double rH, CampState cs)
    {
        var dep = cs.Dep;
        int gap = 0;
        for (int k = i - 1; k >= _planFrom; k--)
        {
            bool counts = !cs.Cfg.RehearsalOnWorkdaysOnly || cs.Work[k];
            if (!counts) continue;

            string baseId;
            if (dep is null)
            {
                gap++;
                if (gap > cs.Cfg.MaxRehearsalGapDays) break;
                if (crew.Any(p => AwayFromMain(p, k))) continue;
                baseId = string.Empty;
            }
            else if (k >= dep.FlyFrom)
            {
                if (!_simPool.BaseHasDevices(dep.L.Id)) break;
                gap++;
                if (gap > cs.Cfg.MaxRehearsalGapDays) break;
                baseId = dep.L.Id;
            }
            else
            {
                break;
            }

            if (crew.Any(p => _ledger.IsOnLeave(p, k) || PersonResting(p, k))) continue;
            if (!crew.All(p => _ledger.CanRehearse(p, k, rH, cs.Cfg))) continue;

            if (FindRehearsalBlock(crew, k, baseId, cs) is { } slot) return (k, slot.Block, slot.Sim);
        }
        if (dep is null || _simPool.BaseHasDevices(dep.L.Id)) return null;

        var window = new List<int>();
        for (int k = dep.Depart - 1; k >= _planFrom && window.Count < PreDeploymentRehearsalLookback; k--)
        {
            bool counts = !cs.Cfg.RehearsalOnWorkdaysOnly || cs.Work[k];
            if (counts) window.Add(k);
        }
        window.Reverse();
        int split = Math.Max(0, window.Count - dep.RehWindowDepth);
        foreach (int k in window.Skip(split).Concat(window.Take(split).Reverse()))
        {
            if (crew.Any(p => _ledger.IsOnLeave(p, k) || PersonResting(p, k))) continue;
            if (!crew.All(p => _ledger.CanRehearse(p, k, rH, cs.Cfg))) continue;

            if (FindRehearsalBlock(crew, k, baseId: string.Empty, cs) is { } slot)
                return (k, slot.Block, slot.Sim);
        }
        return null;
    }

    private (int Block, SimIndex? Sim)? FindRehearsalBlock(List<PersonIndex> crew, int k, string baseId, CampState cs)
    {
        for (int b = 0; b < _bpd; b++)
        {
            bool crewFree = true;
            foreach (var p in crew)
                if (_ledger.IsBlockBusy(p, b, k)) { crewFree = false; break; }
            if (!crewFree) continue;

            if (_simPool.FindFree(baseId, b, k, cs.AnySim, cs.SimSet) is { } s) return (b + 1, s);
        }
        return null;
    }

    private void Finalize(Schedule schedule, List<CampState> states)
    {
        var finishDates = schedule.Sorties.Select(s => s.Date)
            .Concat(schedule.SimulatorSorties.Select(s => s.Date));
        schedule.ProgramFinish = finishDates.Any() ? finishDates.Max() : null;

        var completed = states.ToDictionary(s => s.C.Id, s => s.Completed);

        foreach (var cs in states)
        {
            var camp = cs.C;
            bool done = cs.Completed;
            schedule.CampaignCompleted[camp.Id] = done;
            schedule.DayLog[camp.Id] = new string(cs.Log);
            schedule.DayStopLog[camp.Id] = new string(cs.Stop);

            if (!done)
            {
                schedule.CampaignEndDates[camp.Id] = null;
                var unmet = cs.PrereqIds
                    .Where(pid => !completed.GetValueOrDefault(pid))
                    .Select(pid => states.FirstOrDefault(s => s.C.Id == pid)?.C.Name ?? pid)
                    .ToList();

                string msg = $"Campaign '{camp.Name}' could not be completed within the horizon: " +
                             $"{cs.Done}/{cs.Required} planned sorties flown, " +
                             $"{cs.GoodDone:0.##}/{cs.GoodDone + cs.RemainingTp:0.##} CL1 Test points.";
                if (unmet.Count > 0)
                    msg += $" Blocked by unfinished prerequisite(s): {string.Join(", ", unmet)}.";
                if (cs.Dep is not null)
                    msg += $" Deployed to {cs.Dep.L.Name}: flying was possible only from {_days[cs.Dep.FlyFrom]:yyyy-MM-dd} (ferry + setup).";
                else if (cs.OpenFrom > camp.StartDate)
                    msg += $" Opening was held until {cs.OpenFrom:yyyy-MM-dd} by prerequisite maintenance.";
                if (cs.Tally.Dominant() is { } dom)
                    msg += $" Dominant bottleneck: {dom.Reason} ({dom.Days} days).";
                schedule.Diagnostics.Add(msg);
                continue;
            }

            if (schedule.CampaignEndDates.TryGetValue(camp.Id, out var end) &&
                end.HasValue && end.Value > camp.TargetEndDate)
            {
                string msg = $"Campaign '{camp.Name}' overshot its target date: " +
                             $"{end.Value:yyyy-MM-dd} > {camp.TargetEndDate:yyyy-MM-dd}.";
                if (cs.Tally.Dominant() is { } dom)
                    msg += $" Dominant bottleneck: {dom.Reason} ({dom.Days} days).";
                schedule.Diagnostics.Add(msg);
            }
        }

        if (_capture)
        {
            if (_days.Count > 0)
            {
                foreach (var dep in _deps) EmitDeployment(dep);
                foreach (var cs in states) EmitBlockedRuns(cs, states, completed, schedule);
                foreach (var cs in states) EmitOutcome(cs, schedule, states);
            }
            schedule.DecisionLog = _decisions.OrderBy(e => e.DayIndex).ToList();
        }
    }

    private void EmitDeployment(DepState dep)
    {
        int depart = Math.Clamp(dep.Depart, 0, _days.Count - 1);
        int fly = Math.Clamp(dep.FlyFrom, 0, _days.Count - 1);
        string ret = dep.RetEnd != int.MaxValue && dep.RetEnd < _days.Count
            ? $"returned by {_days[dep.RetEnd]:yyyy-MM-dd}"
            : "did not return within the horizon";
        Log(depart, _days[depart], null, DecisionKind.Deployment,
            $"Deployment to {dep.L.Name}: ferry out {_days[depart]:yyyy-MM-dd}, flying from {_days[fly]:yyyy-MM-dd}; {ret}. " +
            $"Campaigns: {string.Join(", ", dep.Camps.Select(c => c.C.Name))}.");
    }

    private void EmitBlockedRuns(CampState cs, List<CampState> states, Dictionary<string, bool> completed, Schedule schedule)
    {
        var log = cs.Log;
        int n = log.Length;
        for (int i = 0; i < n;)
        {
            char c = log[i];
            if (!DayLogCodes.IsBlocked(c)) { i++; continue; }
            int j = i;
            while (j < n && log[j] == c) j++;
            int days = j - i;
            var (kind, text) = DescribeRun(cs, c, i, states, completed, schedule);
            string range = days == 1 ? $"{_days[i]:yyyy-MM-dd}" : $"{_days[i]:yyyy-MM-dd} → {_days[j - 1]:yyyy-MM-dd}";
            Log(i, _days[i], cs, kind, $"{text} for {days} day(s) ({range}).");
            i = j;
        }
    }

    private (DecisionKind Kind, string Text) DescribeRun(
        CampState cs, char code, int dayIdx, List<CampState> states, Dictionary<string, bool> completed, Schedule schedule)
    {
        string at = cs.Dep is { } d ? $" at {d.L.Name}" : "";
        switch (code)
        {
            case DayLogCodes.Prereq:
                var unmet = cs.PrereqIds.Where(pid => !completed.GetValueOrDefault(pid)).ToList();
                if (unmet.Count == 0) unmet = cs.PrereqIds.ToList();
                var names = unmet.Select(pid => states.FirstOrDefault(s => s.C.Id == pid)?.C.Name ?? pid);
                return (DecisionKind.Waiting, $"Waiting on prerequisite(s): {string.Join(", ", names)}");
            case DayLogCodes.Review:
                var held = cs.PrereqIds.Where(pid =>
                    _prereqReview.TryGetValue(pid, out int rd)
                    && schedule.CampaignEndDates.TryGetValue(pid, out var e) && e is { } ee
                    && WorkdaysBetween(PrefixOf(pid), DayIdx(ee), dayIdx) < rd).ToList();
                if (held.Count > 0)
                {
                    var heldNames = held.Select(pid => states.FirstOrDefault(s => s.C.Id == pid)?.C.Name ?? pid);
                    return (DecisionKind.Waiting,
                        $"Waiting out the post-completion data review of prerequisite(s): {string.Join(", ", heldNames)}");
                }
                return (DecisionKind.Waiting,
                    $"Waiting out the inter-sortie data-review gap ({cs.C.MinWorkdaysBetweenSorties} working days)");
            case DayLogCodes.NotUsable:
                return cs.DeploymentDropped
                    ? (DecisionKind.Waiting, $"Deployment to '{cs.C.LocationId}' could not be created · never usable")
                    : (DecisionKind.Blocked, $"No usable day (weather/calendar{at})");
            case DayLogCodes.Range:
                return (DecisionKind.Blocked, $"Range/airspace not available{at}");
            case DayLogCodes.Aircraft:
                return (DecisionKind.Blocked, "No compatible aircraft free (in maintenance or fully booked)");
            case DayLogCodes.Pilot:
                return (DecisionKind.Blocked, "No eligible pilot (on leave, block busy, or over an FTOM cap)");
            case DayLogCodes.Engineer:
                return (DecisionKind.Blocked, "Too few engineers available");
            case DayLogCodes.Rehearsal:
                return (DecisionKind.Blocked, "No rehearsal slot fit within the gap");
            case DayLogCodes.External:
                return (DecisionKind.Blocked,
                    "Required external support (chase/tanker) not allocated or already committed");
            case DayLogCodes.Pinned:
                return (DecisionKind.Waiting,
                    "Next sortie is pinned · its pinned crew/aircraft is not available");
            default:
                return (DecisionKind.Blocked, "Blocked (reason not recorded)");
        }
    }

    private void EmitOutcome(CampState cs, Schedule schedule, List<CampState> states)
    {
        int last = _days.Count - 1;
        if (cs.Completed)
        {
            var end = schedule.CampaignEndDates.GetValueOrDefault(cs.C.Id);
            int di = end is { } e ? Math.Clamp(e.DayNumber - _sc.HorizonStart.DayNumber, 0, last) : last;
            string late = end is { } ee && ee > cs.C.TargetEndDate
                ? $" · overshot target {cs.C.TargetEndDate:yyyy-MM-dd}"
                : "";
            Log(di, end, cs, DecisionKind.Outcome,
                $"Completed: {cs.Done} sortie(s), {cs.GoodDone:0.##} CL1 test points, " +
                $"finished {(end is { } f ? f.ToString("yyyy-MM-dd") : "—")}{late}.");
        }
        else
        {
            string dom = cs.Tally.Dominant() is { } d ? $" Dominant bottleneck: {d.Reason} ({d.Days} days)." : "";
            Log(last, _days[last], cs, DecisionKind.Outcome,
                $"INCOMPLETE: {cs.Done}/{cs.Required} planned sorties, " +
                $"{cs.GoodDone:0.##}/{cs.GoodDone + cs.RemainingTp:0.##} CL1 test points.{dom}");
        }
    }
}
