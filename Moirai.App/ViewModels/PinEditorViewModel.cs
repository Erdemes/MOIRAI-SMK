using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed record PinChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class PinEditorViewModel : ObservableObject
{
    private readonly Scenario _sc;
    private readonly Campaign _camp;
    private readonly DateOnly _plannedDate;
    private readonly string _originalPilot;
    private readonly int _sortieNo;

    public string Title { get; }
    public string Planned { get; }
    public bool IsAircraftVenue { get; }
    public bool IsSimVenue => !IsAircraftVenue;
    public bool HasSupport { get; }

    public ObservableCollection<PinChoice> AircraftChoices { get; } = new();
    public ObservableCollection<PinChoice> SimulatorChoices { get; } = new();
    public ObservableCollection<SelectableOption> Pilots { get; } = new();
    public ObservableCollection<SelectableOption> Engineers { get; } = new();
    public ObservableCollection<SelectableOption> SupportAircraft { get; } = new();
    public ObservableCollection<SelectableOption> SupportPilots { get; } = new();

    [ObservableProperty] private PinChoice? _selectedAircraft;
    [ObservableProperty] private PinChoice? _selectedSimulator;
    [ObservableProperty] private string _needText = string.Empty;
    [ObservableProperty] private string _warnings = string.Empty;

    [ObservableProperty] private bool _pinDay;
    [ObservableProperty] private DateTime? _pinDate;
    partial void OnPinDayChanged(bool value) => Refresh();
    partial void OnPinDateChanged(DateTime? value) => Refresh();

    private DateOnly CheckDate => PinDay && PinDate is { } d ? DateOnly.FromDateTime(d) : _plannedDate;

    public bool HasWarnings => Warnings.Length > 0;
    partial void OnWarningsChanged(string value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnSelectedAircraftChanged(PinChoice? value) => Refresh();
    partial void OnSelectedSimulatorChanged(PinChoice? value) => Refresh();

    public SortiePin? Result { get; private set; }

    public PinEditorViewModel(Scenario activeScenario, Sortie? sortie, SimulatorSortie? sim, SortiePin? existing)
    {
        _sc = activeScenario;
        string campId = sortie?.CampaignId ?? sim!.CampaignId;
        int no = _sortieNo = sortie?.SortieNo ?? sim!.SortieNo;
        _camp = _sc.Campaigns.First(c => c.Id == campId);
        _plannedDate = sortie?.Date ?? sim!.Date;
        _originalPilot = sortie?.PilotId ?? sim!.PilotId;
        IsAircraftVenue = sim is null;
        HasSupport = IsAircraftVenue && _camp.SupportAircraftPerSortie > 0;
        Title = $"Pin {_camp.Name} · #{no}";

        string Name(string id) =>
            _sc.Pilots.Cast<Resource>().Concat(_sc.Engineers).FirstOrDefault(r => r.Id == id)?.Name ?? id;
        var plannedCrew = sortie is not null
            ? sortie.AdditionalPilotIds.Prepend(sortie.PilotId).Concat(sortie.EngineerIds)
            : sim!.AdditionalPilotIds.Prepend(sim.PilotId).Concat(sim.EngineerIds);
        Planned = $"Planned {_plannedDate:dd.MM ddd} · " +
                  (sortie is not null ? sortie.AircraftId : sim!.SimulatorId) + " · " +
                  string.Join(", ", plannedCrew.Where(id => id.Length > 0).Select(Name));

        var pin = existing ?? (sortie is not null ? SortiePin.From(sortie) : SortiePin.From(sim!));
        PinDay = pin.Date is not null;
        PinDate = (pin.Date ?? _plannedDate).ToDateTime(TimeOnly.MinValue);

        if (IsAircraftVenue)
        {
            AircraftChoices.Add(new PinChoice("", "(any)"));
            foreach (var a in _sc.Aircraft.Where(a => _camp.CompatibleAircraftIds.Contains(a.Id)))
                AircraftChoices.Add(new PinChoice(a.Id, a.Id == a.Name || a.Name.Length == 0 ? a.Id : $"{a.Id} · {a.Name}"));
            SelectedAircraft = AircraftChoices.FirstOrDefault(c => c.Id == (pin.AircraftId ?? "")) ?? AircraftChoices[0];
        }
        else
        {
            SimulatorChoices.Add(new PinChoice("", "(any)"));
            foreach (var s in _sc.Simulators.Where(s => _camp.CompatibleSimulatorIds.Count == 0
                                                       || _camp.CompatibleSimulatorIds.Contains(s.Id)))
                SimulatorChoices.Add(new PinChoice(s.Id, s.Name));
            SelectedSimulator = SimulatorChoices.FirstOrDefault(c => c.Id == (pin.SimulatorId ?? "")) ?? SimulatorChoices[0];
        }

        Fill(Pilots, _sc.Pilots.Where(p => _camp.AssignedPilotIds.Contains(p.Id)), pin.PilotIds);
        Fill(Engineers, _sc.Engineers.Where(e => _camp.AssignedEngineerIds.Contains(e.Id)), pin.EngineerIds);
        if (HasSupport)
        {
            foreach (var a in _sc.Aircraft.Where(a => _camp.SupportAircraftIds.Contains(a.Id)))
            {
                var o = new SelectableOption(a.Id, a.Id, pin.SupportAircraftIds.Contains(a.Id));
                o.PropertyChanged += (_, _) => Refresh();
                SupportAircraft.Add(o);
            }
            Fill(SupportPilots, _sc.Pilots.Where(p => _camp.AssignedPilotIds.Contains(p.Id)), pin.SupportPilotIds);
        }
        Refresh();
    }

    private void Fill(ObservableCollection<SelectableOption> into, IEnumerable<Resource> pool, List<string> selected)
    {
        foreach (var r in pool)
        {
            var o = new SelectableOption(r.Id, r.Name, selected.Contains(r.Id));
            o.PropertyChanged += (_, _) => Refresh();
            into.Add(o);
        }
    }

    private int NeedPilots()
    {
        if (IsSimVenue) return Math.Max(0, _camp.PilotsPerSortie);
        int n = Math.Max(1, _camp.PilotsPerSortie > 0 ? _camp.PilotsPerSortie : _sc.Config.PilotsPerSortie);
        var tail = _sc.Aircraft.FirstOrDefault(a => a.Id == SelectedAircraft?.Id);
        return tail is null ? n : Math.Max(n, Math.Max(1, tail.MinPilots));
    }

    private int NeedEngineers() =>
        _camp.EngineersPerSortie > 0 ? _camp.EngineersPerSortie : _sc.Config.EngineersPerSortie;

    private void Refresh()
    {
        int np = NeedPilots(), ne = NeedEngineers();
        NeedText = $"Needs {np} pilot(s) · {ne} FTE(s)" + (HasSupport ? $" · {_camp.SupportAircraftPerSortie} support ship(s)" : "");

        var w = new List<string>();
        var pilots = Pilots.Where(o => o.IsSelected).ToList();
        var engs = Engineers.Where(o => o.IsSelected).ToList();
        if (pilots.Count < np && pilots.Count > 0) w.Add($"{np - pilots.Count} more pilot(s) chosen by the engine");
        if (engs.Count < ne && engs.Count > 0) w.Add($"{ne - engs.Count} more FTE(s) chosen by the engine");

        var people = _sc.Pilots.Cast<Resource>().Concat(_sc.Engineers).ToDictionary(r => r.Id);
        if (IsAircraftVenue && SelectedAircraft is { Id.Length: > 0 } ac)
            foreach (var o in pilots.Concat(engs))
                if (people.TryGetValue(o.Id, out var r) && r.TypeRatings.Count > 0 && !r.TypeRatings.Contains(ac.Id))
                    w.Add($"{r.Name}: no rating on {ac.Id} · sortie can't fly");
        foreach (var o in pilots.Concat(engs).Concat(SupportPilots.Where(s => s.IsSelected)))
            if (people.TryGetValue(o.Id, out var r) && r.LeaveWindows.FirstOrDefault(x => x.Contains(CheckDate)) is { } lv)
                w.Add($"{r.Name}: leave {lv.Start:dd.MM}–{lv.End:dd.MM} · sortie moves");
        if (IsAircraftVenue && _sc.Aircraft.FirstOrDefault(a => a.Id == SelectedAircraft?.Id) is { } tail
            && tail.MaintenanceWindows.FirstOrDefault(x => x.Contains(CheckDate)) is { } mw)
            w.Add($"{tail.Id}: maintenance {mw.Start:dd.MM}–{mw.End:dd.MM} · sortie moves");
        foreach (var dup in pilots.Select(o => o.Id).Intersect(SupportPilots.Where(s => s.IsSelected).Select(s => s.Id)))
            w.Add($"{people.GetValueOrDefault(dup)?.Name ?? dup}: picked twice");
        if (PinDay && PinDate is not null && !_sc.Calendar.IsWorkday(_camp, CheckDate))
            w.Add($"{CheckDate:dd.MM ddd} is not a working day for the campaign · it flies on the next feasible day");
        Warnings = string.Join("\n", w);
    }

    public string? Apply()
    {
        var pilotIds = Pilots.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (pilotIds.Remove(_originalPilot)) pilotIds.Insert(0, _originalPilot);
        var supportPilotIds = SupportPilots.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (pilotIds.Intersect(supportPilotIds).Any())
            return "A pilot can't be both test and support pilot.";

        var pin = new SortiePin
        {
            CampaignId = _camp.Id,
            SortieNo = _sortieNo,
            AircraftId = IsAircraftVenue && SelectedAircraft is { Id.Length: > 0 } a ? a.Id : null,
            SimulatorId = IsSimVenue && SelectedSimulator is { Id.Length: > 0 } s ? s.Id : null,
            PilotIds = pilotIds,
            EngineerIds = Engineers.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
            SupportAircraftIds = SupportAircraft.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
            SupportPilotIds = supportPilotIds,
            Date = PinDay && PinDate is { } pd ? DateOnly.FromDateTime(pd) : null,
        };
        if (PinDay && PinDate is null) return "Pick the day to pin.";
        if (pin.IsEmpty) return "Pick at least one aircraft, simulator or person, or pin the day.";
        Result = pin;
        return null;
    }
}
