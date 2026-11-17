using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

internal readonly record struct ExtIndex(int Value);

internal sealed class ExternalSupportPool
{
    private readonly IReadOnlyList<ExternalSupport> _assets;
    private readonly Dictionary<string, ExtIndex> _byId = new();
    private readonly bool[][] _available;
    private readonly bool[,,] _busy;
    private readonly int[,] _dayUse;

    public ExternalSupportPool(IReadOnlyList<ExternalSupport> assets, IReadOnlyList<DateOnly> days, int blocksPerDay)
    {
        _assets = assets;
        int n = assets.Count, nDays = days.Count;
        _available = new bool[n][];
        _busy = new bool[n, blocksPerDay, nDays];
        _dayUse = new int[n, nDays];
        for (int e = 0; e < n; e++)
        {
            _byId[assets[e].Id] = new ExtIndex(e);
            _available[e] = new bool[nDays];
            var model = assets[e].AvailabilityModel;
            for (int d = 0; d < nDays; d++) _available[e][d] = model?.IsFlyable(days[d]) ?? true;
        }
    }

    public List<ExtIndex> Resolve(IEnumerable<string> ids)
    {
        var list = new List<ExtIndex>();
        foreach (var id in ids.Distinct())
            if (_byId.TryGetValue(id, out var e)) list.Add(e);
        return list;
    }

    public bool AllFree(List<ExtIndex> required, int block0, int bps, int day)
    {
        foreach (var e in required)
        {
            if (!_available[e.Value][day]) return false;
            var a = _assets[e.Value];
            if (a.MaxSortiesPerDay > 0 && _dayUse[e.Value, day] >= a.MaxSortiesPerDay) return false;
            for (int b = block0; b < block0 + bps; b++)
                if (_busy[e.Value, b, day]) return false;
        }
        return true;
    }

    public void Reserve(List<ExtIndex> required, int block0, int bps, int day)
    {
        foreach (var e in required)
        {
            for (int b = block0; b < block0 + bps; b++) _busy[e.Value, b, day] = true;
            _dayUse[e.Value, day]++;
        }
    }

    public void Release(List<ExtIndex> required, int block0, int bps, int day)
    {
        foreach (var e in required)
        {
            for (int b = block0; b < block0 + bps; b++) _busy[e.Value, b, day] = false;
            _dayUse[e.Value, day] = Math.Max(0, _dayUse[e.Value, day] - 1);
        }
    }

    public string IdOf(ExtIndex e) => _assets[e.Value].Id;
}
