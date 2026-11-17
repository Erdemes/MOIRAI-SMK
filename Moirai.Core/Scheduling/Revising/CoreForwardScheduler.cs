using MOIRAI.Domain;

namespace MOIRAI.Scheduling.Revising;

internal sealed class CoreForwardScheduler
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
        public char[] Log = System.Array.Empty<char>();
        public char[] Stop = System.Array.Empty<char>();
        public int LastFlyDay = -1;
        public int MaxPlacedPerDay;

        public int EndDay = -1;

        public int ActualEndDay = -1;
        public int ActualLastFlyDay = -1;
        public DepState? Dep;
        public bool DeploymentDropped;
        public List<Rehearsal> BankedRehearsals = new();
        public List<string> PrereqIds = new();

        public bool[] Work = Array.Empty<bool>();
        public int[] WorkPrefix = Array.Empty<int>();
        public FlyableDayModel? OwnFly;
        public ScenarioConfig Cfg = null!;
        public Dictionary<int, CampState[]>? OtherTypeDemand;

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

    private readonly RevisingOptions _options;

    private Scenario _sc = null!;
    private ScenarioConfig _cfg = null!;
    private bool _hasDatePins;
    private Dictionary<string, CampState> _stateById = new();
    private int _bpd;
    private List<DateOnly> _days = null!;
    private List<Resource> _people = null!;
    private CrewLedger _ledger = null!;
    private FleetState _fleet = null!;
    private SimPool _simPool = null!;
    private ExternalSupportPool _extPool = null!;
    private int _planFrom;
    private bool[][] _rated = null!;
    private int[] _tailDemand = null!;
    private int[] _workdayPrefix = null!;
    private Dictionary<string, int[]> _workPrefixOf = new();
    private Dictionary<string, int> _prereqReview = new();
    private int[][] _coPool = null!;
    private List<CampState>[] _servedBy = null!;
    private int[] _usableRunLen = null!;
    private bool[] _restToday = null!;
    private int _restDay = -1;
    private List<CampState>[]? _restCampsOfPerson;
    private List<CampState>[]? _restCampsOfTail;
    private const int RestStaggerLead = 2;
    private bool _hasPeriodic;
    private bool _relocated;
    private bool _suppressRepair;
    private IProgress<double>? _progress;
    private CancellationToken _ct;
    private int? _chainSeed;
    private int _chainIters;
    private ISortieYieldModel _yield = LosslessYieldModel.Instance;
    private IFailureModel _failure = NoFailuresModel.Instance;
    private bool _stochastic;

    public bool CaptureDecisions { get; init; }
    private bool _captureDay;
    private List<DecisionEntry> _decisions = new();
    private readonly List<DecisionEntry> _searchLog = new();

    private List<DepState> _deps = new();
    private int[] _depOfPerson = null!;
    private int[] _depOfTail = null!;
    private const int PreDeploymentRehearsalLookback = 30;
    private IReadOnlyDictionary<string, DateOnly> _prereqDone =
        new Dictionary<string, DateOnly>();

    private DayPlan[] _dayPlans = null!;
    private double[][] _ferryHours = null!;
    private readonly List<MoveRecord> _journal = new();
    private readonly Dictionary<CampState, int> _rank = new();
    private int _repairBudget;
    private int[][] _usableSuffix = null!;

    private sealed class MoveRecord
    {
        public required CampState Cs { get; init; }
        public required List<PersonIndex> Crew { get; init; }
        public required TailIndex Tail { get; init; }
        public required int Day { get; init; }
        public required int Block { get; init; }
        public required int Bps { get; init; }
        public int? RehDay { get; init; }
        public int RehBlock { get; init; }
        public required double Fh { get; init; }
        public required double Dh { get; init; }
        public required double Fdh { get; init; }
        public required double Rh { get; init; }
        public required double TpDelta { get; init; }
        public required int PrevLastFlyDay { get; init; }
        public required TailIndex? PrevLockedTail { get; init; }
        public required bool SetLock { get; init; }
        public required bool CompletedHere { get; init; }
        public required Sortie SortieRecord { get; init; }
        public Rehearsal? RehearsalRecord { get; init; }
        public SimIndex? RehSim { get; init; }
        public (SimIndex Sim, DateRange Window)? SimFailure { get; init; }
        public required List<TailIndex> SupportTails { get; init; }
        public required List<ExtIndex> ExtSupports { get; init; }
        public required Rehearsal? ConsumedBank { get; init; }
        public List<(TailIndex Tail, DateRange Window)> Failures { get; } = new();
    }

    public CoreForwardScheduler(RevisingOptions options) => _options = options;

    public bool FlooredToGreedy { get; private set; }

    public Schedule Run(Scenario scenario) =>
        Run(scenario, LosslessYieldModel.Instance, NoFailuresModel.Instance, null, CancellationToken.None);

    public Schedule Run(Scenario scenario, IProgress<double>? progress, CancellationToken ct) =>
        Run(scenario, LosslessYieldModel.Instance, NoFailuresModel.Instance, progress, ct);

    public Schedule Run(Scenario scenario, ISortieYieldModel yield, IFailureModel failure) =>
        Run(scenario, yield, failure, null, CancellationToken.None);

    private Schedule Run(Scenario scenario, ISortieYieldModel yield, IFailureModel failure,
        IProgress<double>? progress, CancellationToken ct)
    {
        _yield = yield;
        _failure = failure;
        _stochastic = yield is not LosslessYieldModel || failure is not NoFailuresModel;
        FlooredToGreedy = false;
        _progress = progress;
        _ct = ct;
        _sc = scenario;
        _cfg = scenario.Config;
        _hasDatePins = scenario.SortiePins.Any(p => p.Date is not null);
        _bpd = Math.Max(1, _cfg.BlocksPerDay);
        _prereqReview = scenario.Campaigns
            .Where(c => c.PostCompletionReviewWorkdays > 0)
            .ToDictionary(c => c.Id, c => c.PostCompletionReviewWorkdays);

        _days = new List<DateOnly>();
        for (var d = scenario.HorizonStart; d <= scenario.HorizonEnd; d = d.AddDays(1)) _days.Add(d);

        var baseOrder = DefaultOrder();

        _prereqDone = new Dictionary<string, DateOnly>();
        if (NeedsPrereqAwareDeployment(scenario) && _days.Count > 0)
        {
            var est = new Dictionary<string, DateOnly>();
            for (int pass = 0; pass < MaxPrereqPasses; pass++)
            {
                _prereqDone = est;
                if (!MergeLatest(est, CompletionMap(ConstructSchedule(baseOrder)))) break;
            }
            _prereqDone = est;
        }

        if (!_stochastic && _options.EnableGlobalSearch && baseOrder.Count > 1 && _days.Count > 0)
            return _chainSeed is { } cs
                ? GlobalSearch(baseOrder, cs, _chainIters)
                : CaptureDecisions
                    ? CapturingMultiStart(scenario, baseOrder)
                    : ParallelGlobalSearch(scenario, baseOrder);
        if (CaptureDecisions) _captureDay = true;
        var single = ConstructSchedule(baseOrder);
        _captureDay = false;

        if (!_stochastic && _options.HasSteering && _days.Count > 0)
        {
            var greedy = CaptureDecisions ? new Scheduler { CaptureDecisions = true }.Run(_sc)
                                          : new Scheduler().Run(_sc);
            if (ScheduleObjective.Of(_sc, greedy).CompareTo(ScheduleObjective.Of(_sc, single)) < 0)
            {
                FlooredToGreedy = true;
                greedy.PlannedOrder ??= baseOrder.Select(c => c.Id).ToList();
                return greedy;
            }
        }
        return single;
    }

    private Schedule CapturingMultiStart(Scenario scenario, List<Campaign> baseOrder)
    {
        int iters = Math.Max(0, _options.GlobalSearchIterations);
        int k = Math.Clamp(iters / MinChainIterations, 1, MaxSearchChains);
        if (k <= 1) return GlobalSearch(baseOrder, _options.SearchSeed, iters);

        int perChain = (iters + k - 1) / k;
        var results = new Schedule[k];
        var po = new ParallelOptions { CancellationToken = _ct };
        if (_options.SearchParallelism > 0) po.MaxDegreeOfParallelism = _options.SearchParallelism;
        int done = 0;
        Parallel.For(0, k, po, ci =>
        {
            var eng = new CoreForwardScheduler(_options) { _chainSeed = _options.SearchSeed + ci, _chainIters = perChain };
            results[ci] = eng.Run(scenario, _yield, _failure);
            _progress?.Report((double)Interlocked.Increment(ref done) / (k + 1));
        });

        int best = 0;
        var bestScore = ScheduleObjective.Of(scenario, results[0]);
        for (int i = 1; i < k; i++)
        {
            var sc = ScheduleObjective.Of(scenario, results[i]);
            if (sc.CompareTo(bestScore) < 0) { bestScore = sc; best = i; }
        }

        var capEng = new CoreForwardScheduler(_options)
        {
            _chainSeed = _options.SearchSeed + best, _chainIters = perChain, CaptureDecisions = true,
            _progress = _progress, _ct = _ct,
        };
        var winner = capEng.Run(scenario, _yield, _failure);
        _progress?.Report(1.0);
        return winner;
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

    private const int MinChainIterations = 100;
    private const int MaxSearchChains = 16;

    private Schedule ConstructSchedule(List<Campaign> order)
    {
        _suppressRepair = false;
        var schedule = ConstructOnce(order);
        if (_options.EnableLocalRepair && _hasPeriodic && HasPeriodicConflict(schedule))
        {
            _suppressRepair = true;
            schedule = ConstructOnce(order);
        }
        return schedule;
    }

    private static bool HasPeriodicConflict(Schedule schedule)
    {
        foreach (var so in schedule.Sorties)
        {
            if (so.IsActual) continue;
            foreach (var tail in so.SupportAircraftIds.Prepend(so.AircraftId))
                if (schedule.AutoMaintenance.TryGetValue(tail, out var ws) && ws.Any(w => w.Contains(so.Date)))
                    return true;
        }
        return false;
    }

    private Schedule ConstructOnce(List<Campaign> order)
    {
        var scenario = _sc;
        int nDays = _days.Count;
        _hasPeriodic = scenario.Aircraft.Any(a => a.PeriodicMaintenances.Count > 0);
        _relocated = false;
        if (_captureDay) _decisions = new List<DecisionEntry>();

        var schedule = new Schedule { PlannedOrder = order.Select(c => c.Id).ToList() };
        var states = BuildStates(order, schedule);
        foreach (var cs in states)
        {
            cs.Log = new char[nDays];
            System.Array.Fill(cs.Log, DayLogCodes.Idle);
            cs.Stop = new char[nDays];
            System.Array.Fill(cs.Stop, DayLogCodes.Idle);
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
        _fleet = new FleetState(scenario.Aircraft, _days);
        _simPool = new SimPool(scenario.Simulators, _days, _bpd);
        _extPool = new ExternalSupportPool(scenario.ExternalSupports, _days, _bpd);

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

        _tailDemand = new int[nAc];
        foreach (var cs in states)
            foreach (var t in cs.CompatIdx)
                _tailDemand[t.Value]++;

        _rated = new bool[nP][];
        for (int p = 0; p < nP; p++)
        {
            _rated[p] = new bool[nAc];
            var ratings = _people[p].TypeRatings;
            if (ratings.Count == 0) System.Array.Fill(_rated[p], true);
            else
                foreach (var id in ratings)
                    if (_fleet.TryIndex(id, out var t)) _rated[p][t.Value] = true;
        }
        if (_options.EnableScarcityCrew) BuildOtherTypeDemand(states);

        BuildRestRouting(states, nP, nAc);

        _restDay = -1;
        if (_options.EnableRestStagger)
        {
            var co = new HashSet<int>[nP];
            _servedBy = new List<CampState>[nP];
            for (int p = 0; p < nP; p++)
            {
                co[p] = new HashSet<int>();
                _servedBy[p] = new List<CampState>();
            }
            void Link(CampState cs, List<PersonIndex> pool)
            {
                foreach (var m in pool)
                    if (!_servedBy[m.Value].Contains(cs)) _servedBy[m.Value].Add(cs);
                for (int a = 0; a < pool.Count; a++)
                    for (int b = a + 1; b < pool.Count; b++)
                    {
                        co[pool[a].Value].Add(pool[b].Value);
                        co[pool[b].Value].Add(pool[a].Value);
                    }
            }
            foreach (var cs in states)
            {
                Link(cs, cs.PilotIdx);
                Link(cs, cs.EngIdx);
            }
            _coPool = new int[nP][];
            for (int p = 0; p < nP; p++)
            {
                _coPool[p] = co[p].ToArray();
                System.Array.Sort(_coPool[p]);
            }
            _usableRunLen = new int[nDays + 1];
            for (int k = nDays - 1; k >= 0; k--)
                _usableRunLen[k] = scenario.IsUsableDay(_days[k]) ? _usableRunLen[k + 1] + 1 : 0;
            _restToday = new bool[nP];
        }

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
        _dayPlans = new DayPlan[nDays];
        for (int d = 0; d < nDays; d++) _dayPlans[d] = new DayPlan(nAc, _bpd);
        _ferryHours = new double[nAc][];
        for (int a = 0; a < nAc; a++) _ferryHours[a] = new double[nDays];
        _journal.Clear();
        _rank.Clear();
        for (int r = 0; r < states.Count; r++) _rank[states[r]] = r;
        _repairBudget = _options.EnableLocalRepair ? Math.Max(0, _options.MaxRepairsPerRun) : 0;

        BuildDeployments(scenario, states, schedule, nP, nAc, nDays);

        _planFrom = scenario.FreezeDate is { } freeze
            ? Math.Clamp(freeze.DayNumber - scenario.HorizonStart.DayNumber, 0, nDays)
            : 0;
        ReplayActuals(states, schedule, personIdx);

        _usableSuffix = new int[states.Count][];
        if (_options.EnableTightnessOrder)
            for (int c = 0; c < states.Count; c++)
            {
                var cs = states[c];
                int deadline = DeadlineIndex(cs.C, nDays);
                var suffix = new int[nDays + 1];
                for (int k = nDays - 1; k >= 0; k--)
                    suffix[k] = suffix[k + 1] + (k <= deadline && UsableForCampaign(cs, k) ? 1 : 0);
                _usableSuffix[c] = suffix;
            }

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
                        cs.Tally.Range++; cs.Log[i] = DayLogCodes.Range; continue;
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

            if (_options.EnableTightnessOrder && open.Count > 1)
                open = open.OrderByDescending(cs => Tightness(cs, i)).ToList();

            if (_hasDatePins && open.Count > 1)
                open = open.OrderBy(cs => cs.NextPinDate == day ? 0 : 1).ToList();

            if (_options.EnableRestStagger) ComputeVoluntaryRest(i, schedule);

            var dayPlan = _dayPlans[i];
            var reason = new Dictionary<string, Reason>();
            var placedToday = new Dictionary<string, int>();
            foreach (var m in _journal)
                if (m.Day == i) placedToday[m.Cs.C.Id] = placedToday.GetValueOrDefault(m.Cs.C.Id) + 1;

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
                        ? PlaceSimSession(cs, i, reason, schedule)
                        : TryPlaceSortie(cs, i, dayPlan, reason, schedule);
                    if (!placed) continue;
                    placedAny = true;
                    placedToday[cs.C.Id] = placedToday.GetValueOrDefault(cs.C.Id) + 1;
                }
            }

            if (_options.EnableLocalRepair && !_suppressRepair && _repairBudget > 0)
            {
                int perDay = Math.Max(0, _options.MaxRepairsPerDay);
                foreach (var cs in open)
                {
                    if (perDay <= 0 || _repairBudget <= 0) break;
                    if (placedToday.GetValueOrDefault(cs.C.Id) > 0) continue;
                    if (cs.C.Venue == CampaignVenue.Simulator) continue;
                    var r = reason.GetValueOrDefault(cs.C.Id);
                    if (r is not (Reason.Pilot or Reason.Engineer or Reason.Pinned)) continue;
                    if (TryCrossDayRepair(cs, i, schedule))
                    {
                        placedToday[cs.C.Id] = placedToday.GetValueOrDefault(cs.C.Id) + 1;
                        _repairBudget--;
                        perDay--;
                    }
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

            if (_options.EnableRestStagger)
                foreach (var cs in open)
                    cs.MaxPlacedPerDay = Math.Max(cs.MaxPlacedPerDay, placedToday.GetValueOrDefault(cs.C.Id));

            if (placedToday.Count > 0 || ferryToday) PlanPeriodicMaintenance(i, schedule);
            DetectDeploymentReturns(i, nDays);
        }

        RenumberSortiesChronologically(schedule);
        if (_hasDatePins) PinnedSortie.DateDiagnostics(scenario, schedule);
        Finalize(schedule, states);
        return schedule;
    }

    private Schedule ParallelGlobalSearch(Scenario scenario, List<Campaign> baseOrder)
    {
        int iters = Math.Max(0, _options.GlobalSearchIterations);
        int k = Math.Clamp(iters / MinChainIterations, 1, MaxSearchChains);
        if (k <= 1) return GlobalSearch(baseOrder, _options.SearchSeed, iters);

        int perChain = (iters + k - 1) / k;
        var results = new Schedule[k];
        var po = new ParallelOptions { CancellationToken = _ct };
        if (_options.SearchParallelism > 0) po.MaxDegreeOfParallelism = _options.SearchParallelism;
        int done = 0;
        Parallel.For(0, k, po, ci =>
        {
            var eng = new CoreForwardScheduler(_options) { _chainSeed = _options.SearchSeed + ci, _chainIters = perChain };
            results[ci] = eng.Run(scenario, _yield, _failure);
            _progress?.Report((double)Interlocked.Increment(ref done) / k);
        });
        _progress?.Report(1.0);

        int best = 0;
        var bestScore = ScheduleObjective.Of(scenario, results[0]);
        for (int i = 1; i < k; i++)
        {
            var sc = ScheduleObjective.Of(scenario, results[i]);
            if (sc.CompareTo(bestScore) < 0) { bestScore = sc; best = i; }
        }
        return results[best];
    }

    private Schedule GlobalSearch(List<Campaign> baseOrder, int seed, int iters)
    {
        var rng = new Random(seed);

        var bestOrder = baseOrder;
        var bestSchedule = ConstructSchedule(baseOrder);
        var bestScore = ScheduleObjective.Of(_sc, bestSchedule);

        var curOrder = baseOrder;
        var curScore = bestScore;

        iters = Math.Max(0, iters);
        double t0 = Math.Max(1e-6, _options.GlobalSearchStartTemp);
        const double tEnd = 0.05;
        double alpha = iters > 1 ? Math.Pow(tEnd / t0, 1.0 / iters) : 1.0;
        double t = t0;
        int sinceImprove = 0;
        int worseAccepts = 0, reheats = 0;
        if (CaptureDecisions)
            LogSearch($"LNS start: baseline ({FmtScore(bestScore)}) over {iters} iteration(s).");

        for (int it = 0; it < iters; it++)
        {
            _ct.ThrowIfCancellationRequested();
            if ((it & 63) == 0) _progress?.Report((double)it / iters);

            var candOrder = ScheduleObjective.Ruin(curOrder, _options.MaxOrderRuin, rng);
            var candSchedule = ConstructSchedule(candOrder);
            var candScore = ScheduleObjective.Of(_sc, candSchedule);

            bool accepted = ScheduleObjective.Accept(candScore, curScore, t, rng);
            if (accepted)
            {
                if (CaptureDecisions && candScore.CompareTo(curScore) > 0) worseAccepts++;
                curOrder = candOrder; curScore = candScore;
            }

            if (candScore.CompareTo(bestScore) < 0)
            {
                if (CaptureDecisions)
                    LogSearch($"Iteration {it}: improved to ({FmtScore(candScore)}) · reordered {MovedNames(bestOrder, candOrder)}.");
                bestScore = candScore; bestSchedule = candSchedule; bestOrder = candOrder;
                sinceImprove = 0;
            }
            else sinceImprove++;

            t *= alpha;
            if (_options.GlobalSearchStall > 0 && sinceImprove >= _options.GlobalSearchStall)
            {
                curOrder = bestOrder; curScore = bestScore; t = t0; sinceImprove = 0;
                if (CaptureDecisions) { reheats++; LogSearch($"Iteration {it}: stalled · reheated and resumed from the best plan."); }
            }
        }
        _progress?.Report(1.0);
        if (CaptureDecisions)
            LogSearch($"LNS done: best ({FmtScore(bestScore)}); {worseAccepts} worse move(s) accepted, {reheats} reheat(s).");

        var greedy = new Scheduler().Run(_sc);
        if (ScheduleObjective.Of(_sc, greedy).CompareTo(bestScore) < 0)
        {
            if (CaptureDecisions)
            {
                LogSearch("Floored to greedy: the search did not beat the plain greedy plan · showing the greedy plan's day-by-day narrative.");
                var g = new Scheduler { CaptureDecisions = true }.Run(_sc);
                g.PlannedOrder ??= baseOrder.Select(c => c.Id).ToList();
                g.DecisionLog = _searchLog.Concat(g.DecisionLog).OrderBy(e => e.DayIndex).ToList();
                return g;
            }
            greedy.PlannedOrder ??= baseOrder.Select(c => c.Id).ToList();
            return greedy;
        }
        if (CaptureDecisions)
        {
            _captureDay = true;
            var w = ConstructSchedule(bestOrder);
            _captureDay = false;
            w.DecisionLog = _searchLog.Concat(w.DecisionLog).OrderBy(e => e.DayIndex).ToList();
            return w;
        }
        return bestSchedule;
    }

    private static string FmtScore(ScheduleObjective.Score s) =>
        $"{s.Incomplete} incomplete · {s.Missed} missed · {s.Lateness} late-days · finish day {s.Makespan}";

    private static string MovedNames(List<Campaign> prev, List<Campaign> next)
    {
        var moved = next.Where((c, idx) => prev.IndexOf(c) != idx).Select(c => c.Name).Take(4).ToList();
        return moved.Count > 0 ? string.Join(", ", moved) : "the campaign order";
    }

    private List<Campaign> DefaultOrder()
    {
        DateOnly DeadlineKey(Campaign c) =>
            CampaignDeadlines.EffectiveTarget(_sc, c) ?? DateOnly.MaxValue;
        double OrderKey(Campaign c) => c.PriorityKey ?? DeadlineKey(c).DayNumber;
        return _sc.Campaigns.OrderBy(OrderKey).ToList();
    }

    private List<CampState> BuildStates(List<Campaign> order, Schedule schedule)
    {
        var scenario = _sc;
        var windowEnd = scenario.Aircraft
            .SelectMany(a => a.MaintenanceWindows)
            .ToDictionary(w => w.Id, w => w.End);

        var states = new List<CampState>();
        foreach (var c in order)
        {
            double neededTp = c.TotalTestPoints;
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
                Required = c.TestPointsPerSortie <= 0 ? 0 : (int)Math.Ceiling(neededTp / c.TestPointsPerSortie),
                RemainingTp = c.TestPointsPerSortie <= 0 ? 0 : neededTp,
                OpenFrom = openFrom,
                PrereqIds = CampaignDeadlines.EffectivePrerequisiteCampaignIds(scenario, c),
            });
        }
        return states;
    }

    private static List<PersonIndex> ResolvePersons(IEnumerable<string> ids, Dictionary<string, PersonIndex> index)
    {
        var list = new List<PersonIndex>();
        foreach (var id in ids)
            if (index.TryGetValue(id, out var p)) list.Add(p);
        return list;
    }

    private void ReplayActuals(List<CampState> states, Schedule schedule, Dictionary<string, PersonIndex> personIdx)
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

            var crew = ResolveCrew(a.PilotId, a.SupportPilotIds.Concat(a.AdditionalPilotIds), a.EngineerIds, personIdx);
            _ledger.AddSortie(crew, i, camp.ExpectedSortieFlightHours, camp.ExpectedSortieDutyHours, camp.ExpectedSortieFlightDutyHours);
            int blk = Math.Clamp(a.Block, 1, _bpd);
            foreach (var p in crew) _ledger.OccupyBlock(p, blk - 1, i);

            foreach (var tail in a.SupportAircraftIds.Prepend(a.AircraftId))
                if (_fleet.TryIndex(tail, out var t))
                {
                    _fleet.RecordSortie(t, camp.ExpectedSortieFlightHours);
                    _ferryHours[t.Value][i] += camp.ExpectedSortieFlightHours;
                }
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
            cs.ActualLastFlyDay = Math.Max(cs.ActualLastFlyDay, i);
            replayedAny = true;

            schedule.Sorties.Add(new Sortie
            {
                CampaignId = a.CampaignId, Date = a.Date, Block = a.Block, SortieNo = a.SortieNo,
                AircraftId = a.AircraftId, PilotId = a.PilotId,
                AdditionalPilotIds = new List<string>(a.AdditionalPilotIds),
                EngineerIds = new List<string>(a.EngineerIds),
                TestPointsFlown = a.TestPointsFlown, Aborted = a.Aborted, IsActual = true,
                RehearsalDate = a.RehearsalDate,
                SupportAircraftIds = new List<string>(a.SupportAircraftIds),
                SupportPilotIds = new List<string>(a.SupportPilotIds),
                ExternalSupportIds = new List<string>(a.ExternalSupportIds),
            });
            if (cs.Completed)
            {
                schedule.CampaignEndDates[camp.Id] = a.Date;
                cs.EndDay = i;
                cs.ActualEndDay = i;
            }
        }

        var flownKeys = new HashSet<(string, int)>(_sc.ActualSorties.Select(s => (s.CampaignId, s.SortieNo)));
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
            cs.ActualLastFlyDay = Math.Max(cs.ActualLastFlyDay, i);
            replayedAny = true;

            schedule.SimulatorSorties.Add(new SimulatorSortie
            {
                CampaignId = a.CampaignId, Date = a.Date, Block = a.Block, SortieNo = a.SortieNo,
                SimulatorId = a.SimulatorId, PilotId = a.PilotId,
                AdditionalPilotIds = new List<string>(a.AdditionalPilotIds),
                EngineerIds = new List<string>(a.EngineerIds),
                TestPointsFlown = a.TestPointsFlown, Aborted = a.Aborted, IsActual = true,
            });
            if (cs.Completed)
            {
                schedule.CampaignEndDates[camp.Id] = a.Date;
                cs.EndDay = i;
                cs.ActualEndDay = i;
            }
        }

        if (replayedAny && _planFrom > 0) PlanPeriodicMaintenance(_planFrom - 1, schedule);
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

    private bool TryPlaceSortie(CampState cs, int i, DayPlan dayPlan, Dictionary<string, Reason> reason, Schedule schedule)
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
        if (allowed.Count == 0) { reason[camp.Id] = Reason.Aircraft; return false; }

        var ordered = OrderedAircraft(cs, i);
        if (_options.EnableScarcityCrew && ordered.Count > 1) ordered = CrewAwareTailOrder(cs, i, ordered);

        if (cs.Pins is not null)
        {
            var pin = cs.NextPin;
            if (pin is { Unsatisfiable: true }) { reason[camp.Id] = Reason.Pinned; return false; }
            if (pin?.Tail is { } pt) ordered = ordered.Where(t => t.Equals(pt)).ToList();
            Reason lastFail = Reason.None;
            bool anyFit = false;
            foreach (var t in ordered)
            {
                foreach (int b in allowed)
                {
                    if (!dayPlan.SpanFree(t.Value, b, bps) || !SameDayOrderOk(cs, i, b, _fleet.IdOf(t))) continue;
                    if (pin is not null && !PinFits(pin, cs, t, b, bps, i, dayPlan, camp)) continue;
                    anyFit = true;
                    if (PlaceAt(cs, i, t, b, bps, dayPlan, schedule, out var fr)) return true;
                    lastFail = fr;
                    if (!_options.EnableLocalRepair) break;
                }
                if (anyFit && !_options.EnableLocalRepair) break;
            }
            reason[camp.Id] = anyFit ? lastFail : (pin is null ? Reason.Aircraft : Reason.Pinned);
            return false;
        }

        if (_options.EnableLocalRepair)
        {
            Reason last = Reason.Aircraft;
            bool anyFree = false;
            foreach (var t in ordered)
                foreach (int b in allowed)
                {
                    if (!dayPlan.SpanFree(t.Value, b, bps)) continue;
                    anyFree = true;
                    if (PlaceAt(cs, i, t, b, bps, dayPlan, schedule, out var fr)) return true;
                    last = fr;
                }
            reason[camp.Id] = anyFree ? last : Reason.Aircraft;
            return false;
        }

        var slot = dayPlan.FindSlot(ordered.Select(t => t.Value).ToList(), allowed, bps);
        if (slot is null) { reason[camp.Id] = Reason.Aircraft; return false; }
        if (PlaceAt(cs, i, new TailIndex(slot.Value.AircraftIdx), slot.Value.Block, bps, dayPlan, schedule, out var r))
            return true;
        reason[camp.Id] = r;
        return false;
    }

    private bool PlaceAt(CampState cs, int i, TailIndex aIdx, int blk, int bps,
        DayPlan dayPlan, Schedule schedule, out Reason fail)
    {
        var camp = cs.C;
        fail = Reason.None;

        double fH = camp.ExpectedSortieFlightHours;
        double dH = camp.ExpectedSortieDutyHours;
        double fdH = camp.ExpectedSortieFlightDutyHours;
        double rH = camp.ExpectedRehearsalDutyHours;

        var pin = cs.NextPin;

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
            if (supportTails.Count < supportN) { fail = Reason.Aircraft; return false; }
        }

        if (cs.ExtUnsatisfiable || !_extPool.AllFree(cs.ExtIdx, blk - 1, bps, i))
        {
            fail = Reason.External;
            return false;
        }

        int needPilots = Math.Max(EffPilots(camp), _fleet.MinPilots(aIdx));
        var pinPilots = pin?.Pilots ?? NoPersons;
        var pilotPool = OrderCrew(
                cs.PilotIdx.Where(p => (pin is null || !pin.Reserved(p))
                    && IsRated(p, aIdx) && PersonAtCampaignBase(p, cs, i)
                    && EligibleForSortie(p, cs, i, blk, fH, dH, fdH)), i, cs).ToList();
        var pilots = pinPilots.Concat(pilotPool.Take(Math.Max(0, needPilots - pinPilots.Count))).ToList();
        if (pilots.Count < needPilots) { fail = Reason.Pilot; return false; }

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
                if (sp.Count < needSp) { fail = Reason.Pilot; return false; }
                supportPilots.AddRange(sp);
                foreach (var p in sp) used.Add(p);
            }
            if (pinSp.Any(p => !used.Contains(p))) { fail = Reason.Pinned; return false; }
        }

        int needEngs = EffEngs(camp);
        var pinEngs = pin?.Engineers ?? NoPersons;
        var engPool = OrderCrew(
                cs.EngIdx.Where(p => (pin is null || !pin.Reserved(p))
                    && IsRated(p, aIdx) && PersonAtCampaignBase(p, cs, i)
                    && EligibleForSortie(p, cs, i, blk, fH, dH, fdH)), i, cs).ToList();
        var engineers = pinEngs.Concat(engPool.Take(Math.Max(0, needEngs - pinEngs.Count))).ToList();
        if (engineers.Count < needEngs) { fail = Reason.Engineer; return false; }

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
                if (reh is null && _options.EnableLocalRepair)
                    foreach (var (altP, altE) in AlternativeCrews(pilots, pinPilots.Count, pilotPool,
                                 engineers, pinEngs.Count, engPool, supportPilots))
                    {
                        var altCrew = altP.Concat(supportPilots).Concat(altE).ToList();
                        _ledger.RemoveSortie(crew, i, fH, dH, fdH);
                        _ledger.AddSortie(altCrew, i, fH, dH, fdH);
                        crew = altCrew;
                        reh = FindRehearsalSlot(i, crew, rH, cs);
                        if (reh is not null) { pilots = altP; engineers = altE; break; }
                    }
                if (reh is null)
                {
                    _ledger.RemoveSortie(crew, i, fH, dH, fdH);
                    fail = Reason.Rehearsal;
                    return false;
                }
                (k, rehBlk, rehSim) = (reh.Value.Day, reh.Value.Block, reh.Value.Sim);
            }
        }

        int prevLastFlyDay = cs.LastFlyDay;
        var prevLocked = cs.LockedTail;
        bool wasCompleted = cs.Completed;

        if (k is { } rehDay) _ledger.AddRehearsal(crew, rehDay, rH);
        dayPlan.Occupy(aIdx.Value, blk, bps);
        foreach (var sa in supportTails) dayPlan.Occupy(sa.Value, blk, bps);
        _extPool.Reserve(cs.ExtIdx, blk - 1, bps, i);
        foreach (var p in crew)
        {
            _ledger.OccupyBlock(p, blk - 1, i);
            if (k is { } rd) _ledger.OccupyBlock(p, rehBlk - 1, rd);
        }
        (SimIndex Sim, DateRange Window)? simFailure = null;
        if (rehSim is { } simDev && k is { } simDay)
        {
            _simPool.Book(simDev, rehBlk - 1, simDay);
            if (RecordSimFailure(simDev, rH, simDay, schedule) is { } sf) simFailure = (simDev, sf);
        }

        var failures = new List<(TailIndex, DateRange)>();
        _fleet.RecordSortie(aIdx, fH);
        if (RecordFailure(aIdx, fH, i, schedule) is { } f0) failures.Add((aIdx, f0));
        foreach (var sa in supportTails)
        {
            _fleet.RecordSortie(sa, fH);
            if (RecordFailure(sa, fH, i, schedule) is { } fs) failures.Add((sa, fs));
        }
        bool setLock = camp.PreferSingleAircraft && cs.LockedTail is null;
        if (setLock) cs.LockedTail = aIdx;
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
        var sortie = new Sortie
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
        };
        schedule.Sorties.Add(sortie);
        Rehearsal? rehearsal = null;
        if (k is { } rehK)
        {
            rehearsal = new Rehearsal
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
            };
            schedule.Rehearsals.Add(rehearsal);
        }

        bool completedHere = !wasCompleted && cs.Completed;

        var move = new MoveRecord
        {
            Cs = cs, Crew = crew, Tail = aIdx, Day = i, Block = blk, Bps = bps,
            RehDay = k, RehBlock = rehBlk, Fh = fH, Dh = dH, Fdh = fdH, Rh = rH,
            TpDelta = tpFlown, PrevLastFlyDay = prevLastFlyDay, PrevLockedTail = prevLocked,
            SetLock = setLock, CompletedHere = completedHere,
            SortieRecord = sortie, RehearsalRecord = rehearsal,
            RehSim = rehSim, SimFailure = simFailure, SupportTails = supportTails,
            ExtSupports = cs.ExtIdx, ConsumedBank = consumedBank,
        };
        move.Failures.AddRange(failures);
        _journal.Add(move);
        if (completedHere) RederiveCompletion(cs, schedule);
        if (completedHere && !RestWindowClear(cs))
        {
            UndoMove(move, schedule);
            fail = Reason.Pilot;
            return false;
        }
        return true;
    }

    private bool RestWindowClear(CampState cs)
    {
        if (!_relocated || cs.C.RestDaysAfterCompletion <= 0 || !cs.Completed || cs.EndDay < 0) return true;
        int from = cs.EndDay + 1, to = cs.EndDay + cs.C.RestDaysAfterCompletion;
        var crew = new HashSet<PersonIndex>(cs.PilotIdx.Concat(cs.EngIdx));
        var tails = new HashSet<TailIndex>(cs.CompatIdx.Concat(cs.SupportIdx));
        foreach (var m in _journal)
        {
            if (ReferenceEquals(m.Cs, cs)) continue;
            if (m.Day >= from && m.Day <= to
                && (m.Crew.Any(crew.Contains) || tails.Contains(m.Tail) || m.SupportTails.Any(tails.Contains)))
                return false;
            if (m.RehDay is int r && r >= from && r <= to && m.Crew.Any(crew.Contains)) return false;
        }
        return true;
    }

    private bool PlaceSimSession(CampState cs, int i, Dictionary<string, Reason> reason, Schedule schedule)
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
        var pilots = pinPilots.Concat(OrderCrew(cs.PilotIdx.Where(p => (pin is null || !pin.Reserved(p))
                && EligibleForSimSession(p, cs, i, startBlk0, bps, dH)), i, cs)
            .Take(Math.Max(0, needPilots - pinPilots.Count)))
            .ToList();
        if (pilots.Count < needPilots) { reason[camp.Id] = Reason.Pilot; return false; }

        int needEngs = EffEngs(camp);
        var pinEngs = pin?.Engineers ?? NoPersons;
        var engineers = pinEngs.Concat(OrderCrew(cs.EngIdx.Where(p => (pin is null || !pin.Reserved(p))
                && EligibleForSimSession(p, cs, i, startBlk0, bps, dH)), i, cs)
            .Take(Math.Max(0, needEngs - pinEngs.Count)))
            .ToList();
        if (engineers.Count < needEngs) { reason[camp.Id] = Reason.Engineer; return false; }

        var crew = pilots.Concat(engineers).ToList();

        _ledger.AddSimSession(crew, i, dH);
        _simPool.BookSpan(simDev, startBlk0, bps, i);
        if (cs.Pins is not null) (cs.OrderDay, cs.OrderBlock, cs.OrderKey) = (i, startBlk, _simPool.IdOf(simDev));
        foreach (var p in crew)
            for (int b = startBlk0; b < startBlk0 + bps; b++) _ledger.OccupyBlock(p, b, i);
        RecordSimFailure(simDev, dH, i, schedule);

        double attempted = Math.Min(camp.TestPointsPerSortie, cs.RemainingTp);
        var outcome = _yield.Evaluate(camp, attempted, cs.GoodDone);
        double tpFlown = outcome.Aborted ? 0 : Math.Min(outcome.GoodPoints, cs.RemainingTp);
        cs.RemainingTp -= tpFlown;
        cs.GoodDone += tpFlown;
        cs.Done++;
        cs.LastFlyDay = i;

        schedule.SimulatorSorties.Add(new SimulatorSortie
        {
            CampaignId = camp.Id,
            Date = _days[i],
            Block = startBlk,
            SortieNo = cs.Done,
            SimulatorId = _simPool.IdOf(simDev),
            PilotId = pilots.Count > 0 ? _people[pilots[0].Value].Id : string.Empty,
            AdditionalPilotIds = pilots.Skip(1).Select(p => _people[p.Value].Id).ToList(),
            EngineerIds = engineers.Select(p => _people[p.Value].Id).ToList(),
            TestPointsFlown = tpFlown,
            Aborted = outcome.Aborted,
        });

        if (cs.Completed)
        {
            schedule.CampaignEndDates[camp.Id] = _days[i];
            cs.EndDay = i;
        }
        return true;
    }

    private static readonly List<PersonIndex> NoPersons = new();
    private static readonly List<TailIndex> NoTails = new();

    private static bool SameDayOrderOk(CampState cs, int i, int blk, string resourceId) =>
        cs.OrderDay != i || PinnedSortie.SortsAfter(blk, resourceId, cs.OrderBlock, cs.OrderKey);

    private bool PinFits(PinnedSortie pin, CampState cs, TailIndex t, int b, int bps, int i, DayPlan dayPlan, Campaign camp)
    {
        double fH = camp.ExpectedSortieFlightHours;
        double dH = camp.ExpectedSortieDutyHours;
        double fdH = camp.ExpectedSortieFlightDutyHours;
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

    private void RecomputeOrder(CampState cs)
    {
        if (cs.Pins is null) return;
        (cs.OrderDay, cs.OrderBlock, cs.OrderKey) = (-1, 0, "");
        foreach (var m in _journal)
        {
            if (!ReferenceEquals(m.Cs, cs)) continue;
            string key = _fleet.IdOf(m.Tail);
            if (m.Day > cs.OrderDay
                || (m.Day == cs.OrderDay && PinnedSortie.SortsAfter(m.Block, key, cs.OrderBlock, cs.OrderKey)))
                (cs.OrderDay, cs.OrderBlock, cs.OrderKey) = (m.Day, m.Block, key);
        }
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

    private void UndoMove(MoveRecord m, Schedule schedule)
    {
        schedule.Sorties.Remove(m.SortieRecord);
        if (m.RehearsalRecord is { } reh) schedule.Rehearsals.Remove(reh);

        var cs = m.Cs;
        bool wasComplete = cs.Completed;
        cs.RemainingTp += m.TpDelta;
        cs.GoodDone -= m.TpDelta;
        cs.Done--;
        if (m.SetLock) cs.LockedTail = m.PrevLockedTail;

        foreach (var p in m.Crew)
        {
            _ledger.FreeBlock(p, m.Block - 1, m.Day);
            if (m.RehDay is { } rd) _ledger.FreeBlock(p, m.RehBlock - 1, rd);
        }
        if (m.RehDay is { } rehDay) _ledger.RemoveRehearsal(m.Crew, rehDay, m.Rh);
        if (m.RehSim is { } sim && m.RehDay is { } simDay) _simPool.Unbook(sim, m.RehBlock - 1, simDay);
        if (m.SimFailure is { } sf)
        {
            _simPool.ShiftFailureDown(sf.Sim, sf.Window, -1);
            if (schedule.SimulatorRepairs.TryGetValue(_simPool.IdOf(sf.Sim), out var sl)) sl.Remove(sf.Window);
        }
        _dayPlans[m.Day].Free(m.Tail.Value, m.Block, m.Bps);
        _fleet.UnrecordSortie(m.Tail, m.Fh);
        foreach (var sa in m.SupportTails)
        {
            _dayPlans[m.Day].Free(sa.Value, m.Block, m.Bps);
            _fleet.UnrecordSortie(sa, m.Fh);
        }
        _extPool.Release(m.ExtSupports, m.Block - 1, m.Bps, m.Day);
        _ledger.RemoveSortie(m.Crew, m.Day, m.Fh, m.Dh, m.Fdh);
        if (m.ConsumedBank is { } held) cs.BankedRehearsals.Add(held);
        foreach (var (t, w) in m.Failures)
        {
            _fleet.ShiftFailureDown(t, w, -1);
            if (schedule.UnscheduledRepairs.TryGetValue(_fleet.IdOf(t), out var fl)) fl.Remove(w);
        }

        _journal.Remove(m);
        RecomputeLastFlyDay(cs);
        RecomputeOrder(cs);
        if (m.CompletedHere || wasComplete != cs.Completed) RederiveCompletion(cs, schedule);
    }

    private void RecomputeLastFlyDay(CampState cs) =>
        cs.LastFlyDay = Math.Max(
            cs.ActualLastFlyDay,
            _journal.Where(m => ReferenceEquals(m.Cs, cs)).Select(m => m.Day).DefaultIfEmpty(-1).Max());

    private void ReapplyMove(MoveRecord m, Schedule schedule)
    {
        bool wasComplete = m.Cs.Completed;
        _ledger.AddSortie(m.Crew, m.Day, m.Fh, m.Dh, m.Fdh);
        if (m.RehDay is { } rd) _ledger.AddRehearsal(m.Crew, rd, m.Rh);
        if (m.RehSim is { } sim && m.RehDay is { } simDay) _simPool.Book(sim, m.RehBlock - 1, simDay);
        if (m.SimFailure is { } sf)
        {
            _simPool.ShiftFailureDown(sf.Sim, sf.Window, +1);
            var id = _simPool.IdOf(sf.Sim);
            if (!schedule.SimulatorRepairs.TryGetValue(id, out var sl))
                schedule.SimulatorRepairs[id] = sl = new List<DateRange>();
            sl.Add(sf.Window);
        }
        _dayPlans[m.Day].Occupy(m.Tail.Value, m.Block, m.Bps);
        foreach (var sa in m.SupportTails) _dayPlans[m.Day].Occupy(sa.Value, m.Block, m.Bps);
        _extPool.Reserve(m.ExtSupports, m.Block - 1, m.Bps, m.Day);
        foreach (var p in m.Crew)
        {
            _ledger.OccupyBlock(p, m.Block - 1, m.Day);
            if (m.RehDay is { } r2) _ledger.OccupyBlock(p, m.RehBlock - 1, r2);
        }
        _fleet.RecordSortie(m.Tail, m.Fh);
        foreach (var sa in m.SupportTails) _fleet.RecordSortie(sa, m.Fh);
        foreach (var (t, w) in m.Failures)
        {
            _fleet.ShiftFailureDown(t, w, +1);
            var id = _fleet.IdOf(t);
            if (!schedule.UnscheduledRepairs.TryGetValue(id, out var fl))
                schedule.UnscheduledRepairs[id] = fl = new List<DateRange>();
            fl.Add(w);
        }
        if (m.SetLock) m.Cs.LockedTail = m.Tail;
        if (m.ConsumedBank is { } held) m.Cs.BankedRehearsals.Remove(held);

        m.Cs.RemainingTp -= m.TpDelta;
        m.Cs.GoodDone += m.TpDelta;
        m.Cs.Done++;

        schedule.Sorties.Add(m.SortieRecord);
        if (m.RehearsalRecord is { } reh) schedule.Rehearsals.Add(reh);
        _journal.Add(m);
        RecomputeLastFlyDay(m.Cs);
        RecomputeOrder(m.Cs);
        if (m.CompletedHere || wasComplete != m.Cs.Completed) RederiveCompletion(m.Cs, schedule);
    }

    private void RederiveCompletion(CampState cs, Schedule schedule)
    {
        if (!cs.Completed)
        {
            cs.EndDay = -1;
            schedule.CampaignEndDates.Remove(cs.C.Id);
            return;
        }
        int end = cs.ActualEndDay;
        foreach (var mv in _journal)
            if (ReferenceEquals(mv.Cs, cs) && mv.TpDelta > 0 && mv.Day > end)
                end = mv.Day;
        if (end < 0) return;
        cs.EndDay = end;
        schedule.CampaignEndDates[cs.C.Id] = _days[end];
    }

    private bool TryCrossDayRepair(CampState h, int i, Schedule schedule)
    {
        var hCrew = new HashSet<PersonIndex>(h.PilotIdx);
        hCrew.UnionWith(h.EngIdx);
        int hRank = _rank[h];

        var candidates = _journal
            .Where(m => _rank[m.Cs] > hRank
                && m.Cs.Pins is null
                && m.Crew.Any(p => hCrew.Contains(p)))
            .OrderByDescending(m => _rank[m.Cs])
            .ThenByDescending(m => m.Day)
            .ToList();

        var ignore = new Dictionary<string, Reason>();
        foreach (var m in candidates)
        {
            var l = m.Cs;
            UndoMove(m, schedule);
            if (TryPlaceSortie(h, i, _dayPlans[i], ignore, schedule))
            {
                var hMove = _journal[^1];
                if (TryRelocate(l, i, schedule))
                {
                    var lMove = _journal[^1];
                    if (!_hasPeriodic || TryCommitPeriodic(m, hMove, lMove, i, schedule))
                    {
                        _relocated = true;
                        if (_captureDay)
                            LogDay(i, _days[i], h, DecisionKind.Repair,
                                $"Modify-behind repair: relocated a {l.C.Name} sortie from " +
                                $"{_days[m.Day]:yyyy-MM-dd} to {_days[lMove.Day]:yyyy-MM-dd} so {h.C.Name} " +
                                $"could fly today (freed shared crew).");
                        return true;
                    }
                    UndoMove(lMove, schedule);
                }
                UndoMove(hMove, schedule);
            }
            ReapplyMove(m, schedule);
        }
        return false;
    }

    private double[] HoursByDay(TailIndex t)
    {
        var h = (double[])_ferryHours[t.Value].Clone();
        foreach (var m in _journal)
        {
            if (m.Tail.Value == t.Value) h[m.Day] += m.Fh;
            else foreach (var sa in m.SupportTails) if (sa.Value == t.Value) { h[m.Day] += m.Fh; break; }
        }
        return h;
    }

    private bool TryCommitPeriodic(MoveRecord lOld, MoveRecord hMove, MoveRecord lNew, int i, Schedule schedule)
    {
        var tails = new HashSet<int>();
        void Collect(MoveRecord m)
        {
            if (_fleet.HasPeriodicRules(m.Tail)) tails.Add(m.Tail.Value);
            foreach (var sa in m.SupportTails) if (_fleet.HasPeriodicRules(sa)) tails.Add(sa.Value);
        }
        Collect(lOld); Collect(hMove); Collect(lNew);
        if (tails.Count == 0) return true;

        var snaps = new Dictionary<int, (double[] Rh, int[] PeriodicDown, List<DateRange>? Auto)>();
        foreach (int ti in tails)
        {
            var t = new TailIndex(ti);
            var (rh, periodicDown) = _fleet.SnapshotPeriodic(t);
            schedule.AutoMaintenance.TryGetValue(_fleet.IdOf(t), out var auto);
            snaps[ti] = (rh, periodicDown, auto is null ? null : new List<DateRange>(auto));
        }

        bool ok = true;
        foreach (int ti in tails)
        {
            var t = new TailIndex(ti);
            var windows = _fleet.ReplanPeriodic(t, HoursByDay(t), throughDay: i);
            schedule.AutoMaintenance[_fleet.IdOf(t)] = windows.Select(w => w.Window).ToList();
        }
        foreach (var m in _journal)
        {
            if (tails.Contains(m.Tail.Value) && _fleet.InMaintenance(m.Tail, m.Day)) { ok = false; break; }
            if (ok)
                foreach (var sa in m.SupportTails)
                    if (tails.Contains(sa.Value) && _fleet.InMaintenance(sa, m.Day)) { ok = false; break; }
            if (!ok) break;
        }
        if (ok) return true;

        foreach (var (ti, snap) in snaps)
        {
            var t = new TailIndex(ti);
            _fleet.RestorePeriodic(t, snap.Rh, snap.PeriodicDown);
            if (snap.Auto is null) schedule.AutoMaintenance.Remove(_fleet.IdOf(t));
            else schedule.AutoMaintenance[_fleet.IdOf(t)] = snap.Auto;
        }
        return false;
    }

    private bool TryRelocate(CampState l, int fromDay, Schedule schedule)
    {
        int openIdx = Math.Max(fromDay, Math.Max(0, l.OpenFrom.DayNumber - _sc.HorizonStart.DayNumber));
        int lastDay = RestWindowCouldOpenFor(l) ? Math.Min(fromDay, _days.Count - 1) : _days.Count - 1;
        var ignore = new Dictionary<string, Reason>();
        for (int k = openIdx; k <= lastDay; k++)
        {
            if (!DayFlyableForCampaign(l, k)) continue;
            if (!PrereqsCompleteBefore(l, k, schedule)) continue;
            if (!ReviewGapOk(l, k)) continue;
            if (l.C.MaxSortiesPerDay > 0
                && _journal.Count(m => ReferenceEquals(m.Cs, l) && m.Day == k) >= l.C.MaxSortiesPerDay)
                continue;
            if (TryPlaceSortie(l, k, _dayPlans[k], ignore, schedule))
            {
                RecomputeLastFlyDay(l);
                return true;
            }
        }
        return false;
    }

    private bool ReviewGapOk(CampState l, int k)
    {
        int gap = l.C.MinWorkdaysBetweenSorties;
        if (gap <= 0) return true;

        int below = -1, above = int.MaxValue;
        bool sameDay = false;
        foreach (var m in _journal)
        {
            if (!ReferenceEquals(m.Cs, l)) continue;
            if (m.Day == k) { sameDay = true; break; }
            if (m.Day < k && m.Day > below) below = m.Day;
            if (m.Day > k && m.Day < above) above = m.Day;
        }
        int act = l.ActualLastFlyDay;
        if (act == k) sameDay = true;
        else if (act >= 0 && act < k && act > below) below = act;
        else if (act > k && act < above) above = act;
        if (sameDay) return false;
        if (below >= 0 && WorkdaysBetween(l.WorkPrefix, below, k) < gap) return false;
        if (above != int.MaxValue && WorkdaysBetween(l.WorkPrefix, k, above) < gap) return false;
        return true;
    }

    private bool DayFlyableForCampaign(CampState cs, int k)
    {
        if (cs.C.Venue == CampaignVenue.Simulator) return cs.Work[k];
        if (cs.Dep is not { } dep) return cs.Work[k] && (cs.OwnFly ?? _sc.FlyableModel).IsFlyable(_days[k]);
        if (k < dep.FlyFrom || k > dep.RetEnd) return false;
        var day = _days[k];
        if (!cs.Work[k] || !(cs.OwnFly ?? dep.L.FlyableModel ?? _sc.FlyableModel).IsFlyable(day)) return false;
        if (dep.L.RangeModel is { } range && !range.IsFlyable(day)) return false;
        return true;
    }

    private bool PrereqsCompleteBefore(CampState l, int k, Schedule schedule) =>
        l.PrereqIds.All(pid =>
            schedule.CampaignEndDates.TryGetValue(pid, out var end) && end is { } e
            && DayIdx(e) < k
            && WorkdaysBetween(PrefixOf(pid), DayIdx(e), k) >= _prereqReview.GetValueOrDefault(pid));

    private IEnumerable<PersonIndex> OrderCrew(IEnumerable<PersonIndex> eligible, int i, CampState cs)
    {
        if (!_options.EnableScarcityCrew || cs.OtherTypeDemand is not { Count: > 0 } demand)
            return eligible.OrderBy(p => _ledger.RecentDuty(p, i));
        return eligible
            .OrderBy(p => demand.TryGetValue(p.Value, out var needers) && needers.Any(o => NeedsSoon(o, i)) ? 1 : 0)
            .ThenBy(p => _ledger.RecentDuty(p, i));
    }

    private bool NeedsSoon(CampState o, int i)
    {
        if (o.Completed) return false;
        int w = Math.Max(1, _cfg.LoadBalanceWindowDays);
        if (o.OpenFrom.DayNumber - _sc.HorizonStart.DayNumber > i + w) return false;
        foreach (var pid in o.PrereqIds)
            if (_stateById.TryGetValue(pid, out var pre) && !pre.Completed) return false;
        return true;
    }

    private List<TailIndex> CrewAwareTailOrder(CampState cs, int i, List<TailIndex> ordered)
    {
        var c = cs.C;
        double fH = c.ExpectedSortieFlightHours, dH = c.ExpectedSortieDutyHours, fdH = c.ExpectedSortieFlightDutyHours;
        bool Available(PersonIndex p) =>
            !(i == _restDay && _restToday[p.Value]) && !PersonResting(p, i) && !_ledger.IsOnLeave(p, i)
            && PersonAtCampaignBase(p, cs, i) && _ledger.CanSortie(p, i, fH, dH, fdH, cs.Cfg)
            && ordered.Any(t => IsRated(p, t));
        var pin = cs.NextPin;
        PersonIndex? lp = OrderCrew(cs.PilotIdx.Where(p => (pin is null || !pin.Reserved(p)) && Available(p)), i, cs)
            .Select(p => (PersonIndex?)p).FirstOrDefault();
        PersonIndex? le = EffEngs(c) > 0
            ? OrderCrew(cs.EngIdx.Where(p => (pin is null || !pin.Reserved(p)) && Available(p)), i, cs)
                .Select(p => (PersonIndex?)p).FirstOrDefault()
            : null;
        if (lp is null && le is null) return ordered;
        return ordered
            .OrderBy(t => (lp is { } a && !IsRated(a, t) ? 1 : 0) + (le is { } e && !IsRated(e, t) ? 2 : 0))
            .ToList();
    }

    private static IEnumerable<(List<PersonIndex> Pilots, List<PersonIndex> Engineers)> AlternativeCrews(
        List<PersonIndex> pilots, int pinnedPilots, List<PersonIndex> pilotPool,
        List<PersonIndex> engineers, int pinnedEngs, List<PersonIndex> engPool, List<PersonIndex> support)
    {
        int produced = 0;
        for (int seat = pinnedPilots; seat < pilots.Count; seat++)
            foreach (var cand in pilotPool.Where(p => !pilots.Contains(p) && !support.Contains(p)))
            {
                if (produced++ >= MaxRehearsalCrewAlternatives) yield break;
                var alt = new List<PersonIndex>(pilots);
                alt[seat] = cand;
                yield return (alt, engineers);
            }
        for (int seat = pinnedEngs; seat < engineers.Count; seat++)
            foreach (var cand in engPool.Where(p => !engineers.Contains(p)))
            {
                if (produced++ >= MaxRehearsalCrewAlternatives) yield break;
                var alt = new List<PersonIndex>(engineers);
                alt[seat] = cand;
                yield return (pilots, alt);
            }
    }

    private const int MaxRehearsalCrewAlternatives = 6;

    private void BuildOtherTypeDemand(List<CampState> states)
    {
        _stateById = states.ToDictionary(s => s.C.Id);
        foreach (var cs in states)
        {
            var own = new HashSet<TailIndex>(cs.CompatIdx.Concat(cs.SupportIdx));
            Dictionary<int, CampState[]>? map = null;
            foreach (var p in cs.PilotIdx.Concat(cs.EngIdx).Distinct())
            {
                var needers = states.Where(o => !ReferenceEquals(o, cs) && o.C.Venue == CampaignVenue.Aircraft
                        && (o.PilotIdx.Contains(p) || o.EngIdx.Contains(p))
                        && o.CompatIdx.Concat(o.SupportIdx).Any(t => !own.Contains(t) && _rated[p.Value][t.Value]))
                    .ToArray();
                if (needers.Length > 0) (map ??= new())[p.Value] = needers;
            }
            cs.OtherTypeDemand = map;
        }
    }

    private bool EligibleForSortie(PersonIndex p, CampState cs, int i, int blk, double fH, double dH, double fdH) =>
        !(i == _restDay && _restToday[p.Value])
        && !PersonResting(p, i)
        && !_ledger.IsOnLeave(p, i) && !_ledger.IsBlockBusy(p, blk - 1, i) && _ledger.CanSortie(p, i, fH, dH, fdH, cs.Cfg);

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

    private bool RestWindowCouldOpenFor(CampState l)
    {
        if (_restCampsOfPerson is null || _restCampsOfTail is null) return false;
        foreach (var p in l.PilotIdx)
            foreach (var cs in _restCampsOfPerson[p.Value])
                if (!cs.Completed) return true;
        foreach (var p in l.EngIdx)
            foreach (var cs in _restCampsOfPerson[p.Value])
                if (!cs.Completed) return true;
        foreach (var t in l.CompatIdx)
            foreach (var cs in _restCampsOfTail[t.Value])
                if (!cs.Completed) return true;
        foreach (var t in l.SupportIdx)
            foreach (var cs in _restCampsOfTail[t.Value])
                if (!cs.Completed) return true;
        return false;
    }

    private void ComputeVoluntaryRest(int i, Schedule schedule)
    {
        _restDay = i;
        System.Array.Clear(_restToday, 0, _restToday.Length);
        int limit = _cfg.MaxConsecutiveFlyingDays;
        int run = _usableRunLen[i];
        if (limit <= 1 || run == 0) return;
        int trigger = Math.Max(1, limit - RestStaggerLead);

        List<(int Streak, int P)>? cands = null;
        for (int p = 0; p < _restToday.Length; p++)
        {
            if (_coPool[p].Length == 0) continue;
            var pi = new PersonIndex(p);
            if (_ledger.IsOnLeave(pi, i)) continue;
            if (_servedBy[p].Any(cs => !cs.Completed && cs.NextPin is { } np && np.Reserved(pi)))
                continue;
            int s = _ledger.ConsecutiveFlyingDays(pi, i);
            if (s < trigger || s >= limit) continue;
            if (s + run <= limit) continue;

            int daysToWall = limit - s, remaining = 0, cap = 0;
            foreach (var cs in _servedBy[p])
            {
                if (cs.Completed || cs.OpenFrom > _days[i]) continue;
                remaining += (int)Math.Ceiling(cs.RemainingTp / Math.Max(1e-9, cs.C.TestPointsPerSortie));
                cap += Math.Max(1, cs.MaxPlacedPerDay);
            }
            if (remaining == 0) continue;
            if ((remaining + cap - 1) / cap <= daysToWall) continue;

            (cands ??= new()).Add((s, p));
        }
        if (cands is null) return;

        foreach (var (streak, p) in cands.OrderByDescending(c => c.Streak).ThenBy(c => c.P))
        {
            bool poolTaken = false, cover = false;
            foreach (int q in _coPool[p])
            {
                if (_restToday[q]) { poolTaken = true; break; }
                if (!cover && !_ledger.IsOnLeave(new PersonIndex(q), i)
                    && _ledger.ConsecutiveFlyingDays(new PersonIndex(q), i) < limit)
                    cover = true;
            }
            if (poolTaken || !cover) continue;
            _restToday[p] = true;
            string pid = _people[p].Id;
            if (!schedule.VoluntaryRest.TryGetValue(pid, out var restDays))
                schedule.VoluntaryRest[pid] = restDays = new List<DateOnly>();
            restDays.Add(_days[i]);
            if (_captureDay)
                LogDay(i, _days[i], (string?)null, null, DecisionKind.Waiting,
                    $"Rest stagger: {_people[p].Name} rests today ({streak} consecutive flying days, cap {limit}) so the crew's forced rule-19 rest days rotate instead of landing on the same day.");
        }
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
        if (_options.EnableBottleneckSteer)
            return avail.OrderBy(a => _tailDemand[a.Value]).ThenBy(a => _fleet.Use(a)).ToList();
        return avail.OrderBy(a => _fleet.Use(a)).ToList();
    }

    private bool IsRated(PersonIndex p, TailIndex t) => _rated[p.Value][t.Value];
    private int EffPilots(Campaign c) => Math.Max(1, c.PilotsPerSortie > 0 ? c.PilotsPerSortie : _cfg.PilotsPerSortie);
    private int EffEngs(Campaign c) => c.EngineersPerSortie > 0 ? c.EngineersPerSortie : _cfg.EngineersPerSortie;

    private int DeadlineIndex(Campaign c, int nDays)
    {
        if (CampaignDeadlines.EffectiveMilestone(_sc, c) is not { } target) return nDays - 1;
        int idx = target.DayNumber - _sc.HorizonStart.DayNumber;
        return Math.Clamp(idx, 0, nDays - 1);
    }

    private bool UsableForCampaign(CampState cs, int k)
    {
        if (cs.C.Venue == CampaignVenue.Simulator)
        {
            if (!cs.Work[k]) return false;
            if (cs.PilotIdx.Count(p => !_ledger.IsOnLeave(p, k)) < Math.Max(0, cs.C.PilotsPerSortie)) return false;
            if (cs.EngIdx.Count(p => !_ledger.IsOnLeave(p, k)) < EffEngs(cs.C)) return false;
            return true;
        }
        if (cs.Dep is { } dep)
        {
            if (k < dep.FlyFrom) return false;
            if (!cs.Work[k]
                || !(cs.OwnFly ?? dep.L.FlyableModel ?? _sc.FlyableModel).IsFlyable(_days[k])) return false;
            if (dep.L.RangeModel is { } range && !range.IsFlyable(_days[k])) return false;
        }
        else if (!cs.Work[k] || !(cs.OwnFly ?? _sc.FlyableModel).IsFlyable(_days[k])) return false;
        if (!cs.CompatIdx.Any(t => !_fleet.InMaintenance(t, k))) return false;
        if (cs.PilotIdx.Count(p => !_ledger.IsOnLeave(p, k)) < EffPilots(cs.C)) return false;
        if (cs.EngIdx.Count(p => !_ledger.IsOnLeave(p, k)) < EffEngs(cs.C)) return false;
        return true;
    }

    private double Tightness(CampState cs, int i)
    {
        double perSortie = cs.C.TestPointsPerSortie;
        int remaining = perSortie <= 0 ? 0 : (int)Math.Ceiling(cs.RemainingTp / perSortie);
        int usable = _usableSuffix[_rank[cs]][i];
        return (double)remaining / Math.Max(1, usable);
    }

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
            else break;

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
            bool free = true;
            foreach (var p in crew)
                if (_ledger.IsBlockBusy(p, b, k)) { free = false; break; }
            if (!free) continue;
            if (_simPool.FindFree(baseId, b, k, cs.AnySim, cs.SimSet) is { } s) return (b + 1, s);
        }
        return null;
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

    private void BuildDeployments(Scenario scenario, List<CampState> states, Schedule schedule, int nP, int nAc, int nDays)
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
                if (depart < idx) { depart = idx; flyFrom = depart + transit; }
            }
            if (flyFrom >= nDays) continue;

            var dep = new DepState { L = loc, Camps = camps, Depart = depart, FlyFrom = flyFrom, RehWindowDepth = rehDepth };
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
            _ferryHours[a.Value][i] += dep.L.FerryFlightHours;
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

    private DateRange? RecordFailure(TailIndex t, double flightHours, int i, Schedule schedule)
    {
        if (_fleet.CheckFailure(t, flightHours, i, _failure) is not { } repair) return null;
        string id = _fleet.IdOf(t);
        if (!schedule.UnscheduledRepairs.TryGetValue(id, out var list))
            schedule.UnscheduledRepairs[id] = list = new List<DateRange>();
        list.Add(repair);
        return repair;
    }

    private DateRange? RecordSimFailure(SimIndex s, double hours, int day, Schedule schedule)
    {
        if (_simPool.CheckFailure(s, hours, day, _failure) is not { } repair) return null;
        string id = _simPool.IdOf(s);
        if (!schedule.SimulatorRepairs.TryGetValue(id, out var list))
            schedule.SimulatorRepairs[id] = list = new List<DateRange>();
        list.Add(repair);
        return repair;
    }

    private void PlanPeriodicMaintenance(int i, Schedule schedule)
    {
        if (_relocated && _hasPeriodic)
        {
            foreach (var t in _fleet.PeriodicTails())
            {
                var windows = _fleet.ReplanPeriodic(t, HoursByDay(t), throughDay: i + 1);
                var id = _fleet.IdOf(t);
                if (windows.Count > 0) schedule.AutoMaintenance[id] = windows.Select(w => w.Window).ToList();
                else schedule.AutoMaintenance.Remove(id);
            }
            return;
        }
        foreach (var (tailId, window) in _fleet.PlanPeriodic(i))
        {
            if (!schedule.AutoMaintenance.TryGetValue(tailId, out var list))
                schedule.AutoMaintenance[tailId] = list = new List<DateRange>();
            list.Add(window);
        }
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

        if (_captureDay)
        {
            if (_days.Count > 0)
            {
                foreach (var dep in _deps) EmitDeployment(dep);
                EmitPlacedFromSorties(schedule, states);
                foreach (var cs in states) EmitBlockedRuns(cs, states, completed, schedule);
                foreach (var cs in states) EmitOutcome(cs, schedule);
            }
            schedule.DecisionLog = _decisions.OrderBy(e => e.DayIndex).ToList();
        }
    }

    private void LogDay(int dayIndex, DateOnly? date, string? campId, string? campName, DecisionKind kind, string message) =>
        _decisions.Add(new DecisionEntry(dayIndex, date, campId, campName, kind, message));

    private void LogDay(int dayIndex, DateOnly? date, CampState? cs, DecisionKind kind, string message) =>
        LogDay(dayIndex, date, cs?.C.Id, cs?.C.Name, kind, message);

    private void LogSearch(string message) =>
        _searchLog.Add(new DecisionEntry(-1, null, null, null, DecisionKind.Search, message));

    private void EmitDeployment(DepState dep)
    {
        int depart = Math.Clamp(dep.Depart, 0, _days.Count - 1);
        int fly = Math.Clamp(dep.FlyFrom, 0, _days.Count - 1);
        string ret = dep.RetEnd != int.MaxValue && dep.RetEnd < _days.Count
            ? $"returned by {_days[dep.RetEnd]:yyyy-MM-dd}"
            : "did not return within the horizon";
        LogDay(depart, _days[depart], (string?)null, null, DecisionKind.Deployment,
            $"Deployment to {dep.L.Name}: ferry out {_days[depart]:yyyy-MM-dd}, flying from {_days[fly]:yyyy-MM-dd}; {ret}. " +
            $"Campaigns: {string.Join(", ", dep.Camps.Select(c => c.C.Name))}.");
    }

    private void EmitPlacedFromSorties(Schedule schedule, List<CampState> states)
    {
        var nameById = _people.ToDictionary(p => p.Id, p => p.Name);
        var csById = states.ToDictionary(s => s.C.Id);
        string NameOf(string id) => nameById.TryGetValue(id, out var n) ? n : id;

        foreach (var so in schedule.Sorties)
        {
            if (so.IsActual) continue;
            int di = Math.Clamp(so.Date.DayNumber - _sc.HorizonStart.DayNumber, 0, _days.Count - 1);
            csById.TryGetValue(so.CampaignId, out var cs);
            int bps = cs is { } c ? Math.Clamp(c.C.BlocksPerSortie, 1, _bpd) : 1;
            string blockText = bps > 1 ? $"blocks {so.Block}-{so.Block + bps - 1}" : $"block {so.Block}";
            var parts = new List<string> { $"pilot {NameOf(so.PilotId)}" };
            if (so.AdditionalPilotIds.Count > 0) parts.Add($"+{so.AdditionalPilotIds.Count} deck pilot(s)");
            parts.Add(so.EngineerIds.Count == 1 ? "1 engineer" : $"{so.EngineerIds.Count} engineers");
            if (so.SupportAircraftIds.Count > 0) parts.Add($"{so.SupportAircraftIds.Count} support ship(s)");
            if (so.ExternalSupportIds.Count > 0) parts.Add($"{so.ExternalSupportIds.Count} external support");
            string reh = so.RehearsalDate is { } rd ? $", rehearsal {rd:yyyy-MM-dd}" : ", no rehearsal";
            string tp = so.Aborted ? " · ABORTED (0 points)" : $" · {so.TestPointsFlown:0.##} test points";
            string loc = cs?.Dep is { } dep ? $" at {dep.L.Name}" : "";
            LogDay(di, so.Date, so.CampaignId, cs?.C.Name ?? so.CampaignId, DecisionKind.Placed,
                $"Placed sortie #{so.SortieNo} on {so.AircraftId}{loc} ({blockText}), " +
                $"{string.Join(", ", parts)}{reh}{tp}.");
        }

        foreach (var ss in schedule.SimulatorSorties)
        {
            if (ss.IsActual) continue;
            int di = Math.Clamp(ss.Date.DayNumber - _sc.HorizonStart.DayNumber, 0, _days.Count - 1);
            csById.TryGetValue(ss.CampaignId, out var cs);
            int bps = cs is { } c ? Math.Clamp(c.C.BlocksPerSortie, 1, _bpd) : 1;
            string blockText = bps > 1 ? $"blocks {ss.Block}-{ss.Block + bps - 1}" : $"block {ss.Block}";
            var parts = new List<string>();
            if (ss.PilotId.Length > 0)
                parts.Add($"pilot {NameOf(ss.PilotId)}" + (ss.AdditionalPilotIds.Count > 0 ? $" +{ss.AdditionalPilotIds.Count}" : ""));
            parts.Add(ss.EngineerIds.Count == 1 ? "1 engineer" : $"{ss.EngineerIds.Count} engineers");
            string tp = ss.Aborted ? " · ABORTED (0 points)" : $" · {ss.TestPointsFlown:0.##} test points";
            LogDay(di, ss.Date, ss.CampaignId, cs?.C.Name ?? ss.CampaignId, DecisionKind.Placed,
                $"Placed sim session #{ss.SortieNo} on {ss.SimulatorId} ({blockText}), {string.Join(", ", parts)}{tp}.");
        }
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
            LogDay(i, _days[i], cs, kind, $"{text} for {days} day(s) ({range}).");
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

    private void EmitOutcome(CampState cs, Schedule schedule)
    {
        int last = _days.Count - 1;
        if (cs.Completed)
        {
            var end = schedule.CampaignEndDates.GetValueOrDefault(cs.C.Id);
            int di = end is { } e ? Math.Clamp(e.DayNumber - _sc.HorizonStart.DayNumber, 0, last) : last;
            string late = end is { } ee && ee > cs.C.TargetEndDate
                ? $" · overshot target {cs.C.TargetEndDate:yyyy-MM-dd}"
                : "";
            LogDay(di, end, cs, DecisionKind.Outcome,
                $"Completed: {cs.Done} sortie(s), {cs.GoodDone:0.##} CL1 test points, " +
                $"finished {(end is { } f ? f.ToString("yyyy-MM-dd") : "—")}{late}.");
        }
        else
        {
            string dom = cs.Tally.Dominant() is { } d ? $" Dominant bottleneck: {d.Reason} ({d.Days} days)." : "";
            LogDay(last, _days[last], cs, DecisionKind.Outcome,
                $"INCOMPLETE: {cs.Done}/{cs.Required} planned sorties, " +
                $"{cs.GoodDone:0.##}/{cs.GoodDone + cs.RemainingTp:0.##} CL1 test points.{dom}");
        }
    }
}
