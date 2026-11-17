namespace MOIRAI.Scheduling;

public sealed class DayPlan
{
    private readonly bool[,] _occupied;
    private readonly int _blocksPerDay;

    public DayPlan(int aircraftCount, int blocksPerDay)
    {
        _blocksPerDay = blocksPerDay;
        _occupied = new bool[aircraftCount, blocksPerDay];
    }

    public (int AircraftIdx, int Block)? FindSlot(
        IReadOnlyList<int> orderedAircraft, IReadOnlyList<int> allowedStartBlocks, int blocksPerSortie)
    {
        if (blocksPerSortie < 1) return null;
        foreach (int a in orderedAircraft)
            foreach (int b in allowedStartBlocks)
            {
                if (b < 1 || b - 1 + blocksPerSortie > _blocksPerDay) continue;
                if (SpanFree(a, b, blocksPerSortie)) return (a, b);
            }
        return null;
    }

    public bool SpanFree(int aircraftIdx, int startBlock, int span)
    {
        for (int k = 0; k < span; k++)
            if (_occupied[aircraftIdx, startBlock - 1 + k]) return false;
        return true;
    }

    public void Occupy(int aircraftIdx, int startBlock, int span)
    {
        for (int k = 0; k < span; k++)
            _occupied[aircraftIdx, startBlock - 1 + k] = true;
    }

    public void Free(int aircraftIdx, int startBlock, int span)
    {
        for (int k = 0; k < span; k++)
            _occupied[aircraftIdx, startBlock - 1 + k] = false;
    }
}
