using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

internal sealed class PinnedSortie
{
    public bool Unsatisfiable;
    public TailIndex? Tail;
    public List<PersonIndex> Pilots = new();
    public List<PersonIndex> Engineers = new();
    public List<TailIndex> SupportTails = new();
    public List<PersonIndex> SupportPilots = new();
    public SimIndex? Sim;
    public DateOnly? Date;

    public bool FixesResources => Tail is not null || Sim is not null || _people.Count > 0 || SupportTails.Count > 0 || Unsatisfiable;

    private readonly HashSet<PersonIndex> _people = new();

    public bool Reserved(PersonIndex p) => _people.Contains(p);

    public bool HasCrew => _people.Count > 0;

    public static Dictionary<int, PinnedSortie>? ResolveFor(
        Scenario sc, Campaign c,
        IReadOnlyDictionary<string, PersonIndex> personIdx,
        IReadOnlyCollection<PersonIndex> pilotPool, IReadOnlyCollection<PersonIndex> engPool,
        FleetState fleet, IReadOnlyCollection<TailIndex> compat, IReadOnlyCollection<TailIndex> support,
        SimPool sims, bool anySim, HashSet<SimIndex> simSet)
    {
        Dictionary<int, PinnedSortie>? map = null;
        foreach (var pin in sc.SortiePins)
        {
            if (pin.CampaignId != c.Id || pin.SortieNo < 1 || pin.IsEmpty) continue;
            var r = new PinnedSortie { Date = pin.Date };
            bool simVenue = c.Venue == CampaignVenue.Simulator;

            if (!simVenue && !string.IsNullOrEmpty(pin.AircraftId))
            {
                if (fleet.TryIndex(pin.AircraftId, out var t) && compat.Contains(t)) r.Tail = t;
                else r.Unsatisfiable = true;
            }
            if (simVenue && !string.IsNullOrEmpty(pin.SimulatorId))
            {
                var set = sims.Resolve(new[] { pin.SimulatorId });
                if (set.Count == 1 && (anySim || simSet.Overlaps(set))) r.Sim = set.First();
                else r.Unsatisfiable = true;
            }

            r.Unsatisfiable |= !ResolveCrew(pin.PilotIds, pilotPool, personIdx, r.Pilots);
            r.Unsatisfiable |= !ResolveCrew(pin.EngineerIds, engPool, personIdx, r.Engineers);
            if (!simVenue)
            {
                r.Unsatisfiable |= !ResolveCrew(pin.SupportPilotIds, pilotPool, personIdx, r.SupportPilots);
                foreach (var id in pin.SupportAircraftIds.Distinct())
                {
                    if (fleet.TryIndex(id, out var st) && support.Contains(st) && !Equals(r.Tail, st))
                        r.SupportTails.Add(st);
                    else r.Unsatisfiable = true;
                }
            }

            foreach (var p in r.Pilots.Concat(r.Engineers).Concat(r.SupportPilots))
                if (!r._people.Add(p)) r.Unsatisfiable = true;

            (map ??= new Dictionary<int, PinnedSortie>())[pin.SortieNo] = r;
        }
        return map;
    }

    public static void DateDiagnostics(Scenario sc, Schedule schedule)
    {
        foreach (var pin in sc.SortiePins.Where(p => p.Date is not null))
        {
            var c = sc.Campaigns.FirstOrDefault(x => x.Id == pin.CampaignId);
            if (c is null) continue;
            DateOnly? flew = c.Venue == CampaignVenue.Simulator
                ? schedule.SimulatorSorties.FirstOrDefault(s => s.CampaignId == c.Id && s.SortieNo == pin.SortieNo)?.Date
                : schedule.Sorties.FirstOrDefault(s => s.CampaignId == c.Id && s.SortieNo == pin.SortieNo)?.Date;
            if (flew == pin.Date) continue;
            schedule.Diagnostics.Add(flew is { } d
                ? $"{c.Name}: sortie #{pin.SortieNo} is pinned to {pin.Date:yyyy-MM-dd} but could only fly on {d:yyyy-MM-dd}."
                : $"{c.Name}: sortie #{pin.SortieNo} is pinned to {pin.Date:yyyy-MM-dd} but was not flown.");
        }
    }

    private static bool ResolveCrew(IEnumerable<string> ids, IReadOnlyCollection<PersonIndex> pool,
        IReadOnlyDictionary<string, PersonIndex> personIdx, List<PersonIndex> into)
    {
        bool ok = true;
        foreach (var id in ids.Where(s => !string.IsNullOrEmpty(s)).Distinct())
        {
            if (personIdx.TryGetValue(id, out var p) && pool.Contains(p)) into.Add(p);
            else ok = false;
        }
        return ok;
    }

    public static bool SortsAfter(int blk, string tailId, int lastBlk, string lastTailId) =>
        blk > lastBlk || (blk == lastBlk && string.CompareOrdinal(tailId, lastTailId) > 0);
}
