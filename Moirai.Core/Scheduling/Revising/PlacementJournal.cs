using MOIRAI.Domain;

namespace MOIRAI.Scheduling.Revising;

internal sealed class PlacementState
{
    public CrewLedger Crew { get; }
    public FleetState Fleet { get; }
    private readonly DayPlan[] _dayPlans;

    public PlacementState(IReadOnlyList<Resource> people, IReadOnlyList<Aircraft> aircraft,
        IReadOnlyList<DateOnly> days, int blocksPerDay, ScenarioConfig cfg)
    {
        Crew = new CrewLedger(people, days, blocksPerDay, cfg);
        Fleet = new FleetState(aircraft, days);
        _dayPlans = new DayPlan[days.Count];
        for (int d = 0; d < days.Count; d++) _dayPlans[d] = new DayPlan(aircraft.Count, blocksPerDay);
    }

    public DayPlan DayPlanAt(int day) => _dayPlans[day];
}

internal sealed class SortieMove
{
    public required IReadOnlyList<PersonIndex> Crew { get; init; }
    public required TailIndex Tail { get; init; }
    public IReadOnlyList<TailIndex> SupportTails { get; init; } = System.Array.Empty<TailIndex>();

    public required int FlightDay { get; init; }
    public required int FlightBlock { get; init; }
    public required int Bps { get; init; }
    public required int RehearsalDay { get; init; }
    public required int RehearsalBlock { get; init; }

    public required double FlightHours { get; init; }
    public required double DutyHours { get; init; }
    public required double FlightDutyHours { get; init; }
    public required double RehearsalHours { get; init; }

    public void Apply(PlacementState s)
    {
        s.Crew.AddSortie(Crew, FlightDay, FlightHours, DutyHours, FlightDutyHours);
        foreach (var p in Crew) s.Crew.OccupyBlock(p, FlightBlock - 1, FlightDay);

        s.Fleet.RecordSortie(Tail, FlightHours);
        s.DayPlanAt(FlightDay).Occupy(Tail.Value, FlightBlock, Bps);
        foreach (var st in SupportTails)
        {
            s.Fleet.RecordSortie(st, FlightHours);
            s.DayPlanAt(FlightDay).Occupy(st.Value, FlightBlock, Bps);
        }

        s.Crew.AddRehearsal(Crew, RehearsalDay, RehearsalHours);
        foreach (var p in Crew) s.Crew.OccupyBlock(p, RehearsalBlock - 1, RehearsalDay);
    }

    public void Undo(PlacementState s)
    {
        foreach (var p in Crew) s.Crew.FreeBlock(p, RehearsalBlock - 1, RehearsalDay);
        s.Crew.RemoveRehearsal(Crew, RehearsalDay, RehearsalHours);

        foreach (var st in SupportTails)
        {
            s.DayPlanAt(FlightDay).Free(st.Value, FlightBlock, Bps);
            s.Fleet.UnrecordSortie(st, FlightHours);
        }
        s.DayPlanAt(FlightDay).Free(Tail.Value, FlightBlock, Bps);
        s.Fleet.UnrecordSortie(Tail, FlightHours);

        foreach (var p in Crew) s.Crew.FreeBlock(p, FlightBlock - 1, FlightDay);
        s.Crew.RemoveSortie(Crew, FlightDay, FlightHours, DutyHours, FlightDutyHours);
    }
}

internal sealed class PlacementJournal
{
    private readonly PlacementState _state;
    private readonly List<SortieMove> _moves = new();

    public PlacementJournal(PlacementState state) => _state = state;

    public int Count => _moves.Count;
    public IReadOnlyList<SortieMove> Moves => _moves;

    public void Commit(SortieMove move)
    {
        move.Apply(_state);
        _moves.Add(move);
    }

    public SortieMove UndoLast()
    {
        var move = _moves[^1];
        _moves.RemoveAt(_moves.Count - 1);
        move.Undo(_state);
        return move;
    }

    public void UndoTo(int checkpoint)
    {
        while (_moves.Count > checkpoint) UndoLast();
    }
}
