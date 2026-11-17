using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

internal readonly record struct TailIndex(int Value);

internal sealed class FleetState
{
    private readonly IReadOnlyList<Aircraft> _aircraft;
    private readonly IReadOnlyList<DateOnly> _days;
    private readonly Dictionary<string, TailIndex> _byId = new();
    private readonly bool[][] _plannedDown;
    private readonly int[][] _periodicDown;
    private readonly int[][] _failureDown;
    private readonly int[] _use;
    private readonly double[][] _ruleHours;

    public FleetState(IReadOnlyList<Aircraft> aircraft, IReadOnlyList<DateOnly> days)
    {
        _aircraft = aircraft;
        _days = days;
        int n = aircraft.Count, nDays = days.Count;
        _plannedDown = new bool[n][];
        _periodicDown = new int[n][];
        _failureDown = new int[n][];
        _use = new int[n];
        _ruleHours = new double[n][];
        for (int a = 0; a < n; a++)
        {
            _byId[aircraft[a].Id] = new TailIndex(a);
            _plannedDown[a] = new bool[nDays];
            _periodicDown[a] = new int[nDays];
            _failureDown[a] = new int[nDays];
            for (int d = 0; d < nDays; d++) _plannedDown[a][d] = aircraft[a].IsInMaintenance(days[d]);
            _ruleHours[a] = new double[aircraft[a].PeriodicMaintenances.Count];
        }
    }

    public int Count => _aircraft.Count;

    public bool TryIndex(string id, out TailIndex t) => _byId.TryGetValue(id, out t);

    public List<TailIndex> Resolve(IEnumerable<string> ids)
    {
        var list = new List<TailIndex>();
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var t)) list.Add(t);
        return list;
    }

    public string IdOf(TailIndex t) => _aircraft[t.Value].Id;
    public bool InMaintenance(TailIndex t, int day) =>
        _plannedDown[t.Value][day] || _periodicDown[t.Value][day] > 0 || _failureDown[t.Value][day] > 0;
    public int Use(TailIndex t) => _use[t.Value];

    public int MinPilots(TailIndex t) => _aircraft[t.Value].MinPilots;

    public void RecordSortie(TailIndex t, double flightHours)
    {
        _use[t.Value]++;
        AddRuleHours(t, flightHours);
    }

    public void AddFerryHours(TailIndex t, double flightHours) => AddRuleHours(t, flightHours);

    public void UnrecordSortie(TailIndex t, double flightHours)
    {
        _use[t.Value]--;
        AddRuleHours(t, -flightHours);
    }

    private void AddRuleHours(TailIndex t, double hours)
    {
        var rh = _ruleHours[t.Value];
        for (int r = 0; r < rh.Length; r++) rh[r] += hours;
    }

    public IEnumerable<(string TailId, DateRange Window)> PlanPeriodic(int day)
    {
        for (int a = 0; a < _aircraft.Count; a++)
        {
            var rules = _aircraft[a].PeriodicMaintenances;
            for (int r = 0; r < rules.Count; r++)
            {
                var rule = rules[r];
                if (rule.IntervalFlightHours <= 0 || rule.DurationDays <= 0) continue;
                if (_ruleHours[a][r] < rule.IntervalFlightHours) continue;

                _ruleHours[a][r] %= rule.IntervalFlightHours;
                MarkDown(_periodicDown[a], day, rule.DurationDays, +1);
                yield return (_aircraft[a].Id, new DateRange
                {
                    Start = _days[day].AddDays(1),
                    End = _days[day].AddDays(rule.DurationDays),
                    Note = $"{rule.IntervalFlightHours:0.#}h check",
                });
            }
        }
    }

    public bool HasPeriodicRules(TailIndex t) => _aircraft[t.Value].PeriodicMaintenances.Count > 0;

    public IEnumerable<TailIndex> PeriodicTails()
    {
        for (int a = 0; a < _aircraft.Count; a++)
            if (_aircraft[a].PeriodicMaintenances.Count > 0) yield return new TailIndex(a);
    }

    public (double[] RuleHours, int[] PeriodicDown) SnapshotPeriodic(TailIndex t) =>
        ((double[])_ruleHours[t.Value].Clone(), (int[])_periodicDown[t.Value].Clone());

    public void RestorePeriodic(TailIndex t, double[] ruleHours, int[] periodicDown)
    {
        System.Array.Copy(ruleHours, _ruleHours[t.Value], ruleHours.Length);
        System.Array.Copy(periodicDown, _periodicDown[t.Value], periodicDown.Length);
    }

    public List<(int Day, DateRange Window)> ReplanPeriodic(TailIndex t, double[] hoursByDay, int throughDay)
    {
        int a = t.Value;
        var rules = _aircraft[a].PeriodicMaintenances;
        int nDays = _days.Count;

        System.Array.Clear(_periodicDown[a]);
        System.Array.Clear(_ruleHours[a]);

        var windows = new List<(int, DateRange)>();
        for (int d = 0; d < nDays; d++)
        {
            if (hoursByDay[d] != 0) AddRuleHours(t, hoursByDay[d]);
            if (d >= throughDay) continue;
            for (int r = 0; r < rules.Count; r++)
            {
                var rule = rules[r];
                if (rule.IntervalFlightHours <= 0 || rule.DurationDays <= 0) continue;
                if (_ruleHours[a][r] < rule.IntervalFlightHours) continue;

                _ruleHours[a][r] %= rule.IntervalFlightHours;
                MarkDown(_periodicDown[a], d, rule.DurationDays, +1);
                windows.Add((d, new DateRange
                {
                    Start = _days[d].AddDays(1),
                    End = _days[d].AddDays(rule.DurationDays),
                    Note = $"{rule.IntervalFlightHours:0.#}h check",
                }));
            }
        }
        return windows;
    }

    public DateRange? CheckFailure(TailIndex t, double flightHours, int day, IFailureModel model)
    {
        var ac = _aircraft[t.Value];
        if (model.Evaluate(ac.FailuresPer100FlightHours, ac.RepairDaysMin, ac.RepairDaysMax, flightHours)
            is not { } downDays) return null;

        MarkDown(_failureDown[t.Value], day, downDays, +1);
        return new DateRange
        {
            Start = _days[day].AddDays(1),
            End = _days[day].AddDays(downDays),
            Note = "unscheduled repair",
        };
    }

    public void ShiftFailureDown(TailIndex t, DateRange window, int delta)
    {
        int s = Math.Max(0, window.Start.DayNumber - _days[0].DayNumber);
        int e = Math.Min(_days.Count - 1, window.End.DayNumber - _days[0].DayNumber);
        var grid = _failureDown[t.Value];
        for (int d = s; d <= e; d++) grid[d] = Math.Max(0, grid[d] + delta);
    }

    private void MarkDown(int[] grid, int day, int durationDays, int delta)
    {
        int last = Math.Min(_days.Count - 1, day + durationDays);
        for (int d = day + 1; d <= last; d++) grid[d] = Math.Max(0, grid[d] + delta);
    }
}
