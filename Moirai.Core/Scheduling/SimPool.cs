using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

internal readonly record struct SimIndex(int Value);

internal sealed class SimPool
{
    private readonly IReadOnlyList<Simulator> _sims;
    private readonly IReadOnlyList<DateOnly> _days;
    private readonly int _bpd;

    private readonly Dictionary<string, SimIndex> _byId = new();
    private readonly Dictionary<string, List<SimIndex>> _byBase = new();
    private readonly bool[][] _maintDown;
    private readonly int[][] _failureDown;
    private readonly bool[,,] _busy;
    private readonly int[,] _dayUse;

    public SimPool(IReadOnlyList<Simulator> sims, IReadOnlyList<DateOnly> days, int blocksPerDay)
    {
        _sims = sims;
        _days = days;
        _bpd = blocksPerDay;

        int n = sims.Count, nDays = days.Count;
        _maintDown = new bool[n][];
        _failureDown = new int[n][];
        _busy = new bool[n, blocksPerDay, nDays];
        _dayUse = new int[n, nDays];

        for (int s = 0; s < n; s++)
        {
            var idx = new SimIndex(s);
            _byId[sims[s].Id] = idx;
            (_byBase.TryGetValue(sims[s].LocationId, out var list)
                ? list
                : _byBase[sims[s].LocationId] = new List<SimIndex>()).Add(idx);
            _maintDown[s] = new bool[nDays];
            _failureDown[s] = new int[nDays];
            for (int d = 0; d < nDays; d++) _maintDown[s][d] = sims[s].IsInMaintenance(days[d]);
        }
    }

    private bool IsDown(SimIndex s, int day) => _maintDown[s.Value][day] || _failureDown[s.Value][day] > 0;

    public HashSet<SimIndex> Resolve(IEnumerable<string> ids)
    {
        var set = new HashSet<SimIndex>();
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var idx)) set.Add(idx);
        return set;
    }

    public bool BaseHasDevices(string baseId) => _byBase.ContainsKey(baseId);

    public SimIndex? FindFree(string baseId, int block, int day, bool anySim, HashSet<SimIndex> compatible)
    {
        if (!_byBase.TryGetValue(baseId, out var sims)) return null;
        foreach (var s in sims)
        {
            if (IsDown(s, day) || _busy[s.Value, block, day]) continue;
            if (!anySim && !compatible.Contains(s)) continue;
            var sim = _sims[s.Value];
            int cap = sim.MaxBlocksPerDay <= 0 ? _bpd : Math.Min(sim.MaxBlocksPerDay, _bpd);
            if (_dayUse[s.Value, day] >= cap) continue;
            return s;
        }
        return null;
    }

    public void Book(SimIndex s, int block, int day)
    {
        _busy[s.Value, block, day] = true;
        _dayUse[s.Value, day]++;
    }

    public void Unbook(SimIndex s, int block, int day)
    {
        _busy[s.Value, block, day] = false;
        _dayUse[s.Value, day] = Math.Max(0, _dayUse[s.Value, day] - 1);
    }

    public SimIndex? FindFreeSpan(string baseId, int startBlock, int bps, int day, bool anySim, HashSet<SimIndex> compatible)
    {
        if (!_byBase.TryGetValue(baseId, out var sims)) return null;
        if (startBlock < 0 || startBlock + bps > _bpd) return null;
        foreach (var s in sims)
        {
            if (IsDown(s, day)) continue;
            if (!anySim && !compatible.Contains(s)) continue;
            var sim = _sims[s.Value];
            int cap = sim.MaxBlocksPerDay <= 0 ? _bpd : Math.Min(sim.MaxBlocksPerDay, _bpd);
            if (_dayUse[s.Value, day] + bps > cap) continue;
            bool spanFree = true;
            for (int b = startBlock; b < startBlock + bps; b++)
                if (_busy[s.Value, b, day]) { spanFree = false; break; }
            if (spanFree) return s;
        }
        return null;
    }

    public void BookSpan(SimIndex s, int startBlock, int bps, int day)
    {
        for (int b = startBlock; b < startBlock + bps; b++) _busy[s.Value, b, day] = true;
        _dayUse[s.Value, day] += bps;
    }

    public void UnbookSpan(SimIndex s, int startBlock, int bps, int day)
    {
        for (int b = startBlock; b < startBlock + bps; b++) _busy[s.Value, b, day] = false;
        _dayUse[s.Value, day] = Math.Max(0, _dayUse[s.Value, day] - bps);
    }

    public DateRange? CheckFailure(SimIndex s, double hours, int day, IFailureModel model)
    {
        var sim = _sims[s.Value];
        if (model.Evaluate(sim.FailuresPer100Hours, sim.RepairDaysMin, sim.RepairDaysMax, hours)
            is not { } downDays) return null;

        int last = Math.Min(_days.Count - 1, day + downDays);
        var grid = _failureDown[s.Value];
        for (int d = day + 1; d <= last; d++) grid[d]++;
        return new DateRange
        {
            Start = _days[day].AddDays(1),
            End = _days[day].AddDays(downDays),
            Note = "unscheduled sim repair",
        };
    }

    public void ShiftFailureDown(SimIndex s, DateRange window, int delta)
    {
        int from = Math.Max(0, window.Start.DayNumber - _days[0].DayNumber);
        int to = Math.Min(_days.Count - 1, window.End.DayNumber - _days[0].DayNumber);
        var grid = _failureDown[s.Value];
        for (int d = from; d <= to; d++) grid[d] = Math.Max(0, grid[d] + delta);
    }

    public string IdOf(SimIndex s) => _sims[s.Value].Id;
}
