using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed record IdLabel(string Id, string Label);

public sealed partial class ExecutionRow : ObservableObject
{
    public const int StatusFlown = 0;
    public const int StatusAborted = 1;
    public const int StatusCancelled = 2;

    public Sortie? FlightSource { get; init; }
    public SimulatorSortie? SimSource { get; init; }
    public bool IsSim => SimSource is not null;
    public required string CampaignName { get; init; }

    public DateOnly Date => FlightSource?.Date ?? SimSource!.Date;
    public int SortieNo => FlightSource?.SortieNo ?? SimSource!.SortieNo;
    public int Block => FlightSource?.Block ?? SimSource!.Block;
    public double PlannedTp { get; init; }

    public required IReadOnlyList<IdLabel> AircraftChoices { get; init; }

    [ObservableProperty] private IdLabel? _selectedAircraft;

    public required ObservableCollection<SelectableOption> PilotChoices { get; init; }
    public required ObservableCollection<SelectableOption> EngineerChoices { get; init; }

    public string PilotsSummary => Summarize(PilotChoices);
    public string EngineersSummary => Summarize(EngineerChoices);

    public void RefreshCrewSummaries()
    {
        OnPropertyChanged(nameof(PilotsSummary));
        OnPropertyChanged(nameof(EngineersSummary));
    }

    private static string Summarize(IEnumerable<SelectableOption> opts)
    {
        var sel = opts.Where(o => o.IsSelected).Select(o => o.Label).ToList();
        return sel.Count == 0 ? "- none -" : string.Join(", ", sel);
    }

    [ObservableProperty]
    private DateTime? _actualDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTpEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDateEnabled))]
    private int _statusIndex;

    [ObservableProperty]
    private decimal _actualTp;

    public bool IsTpEnabled => StatusIndex == StatusFlown;

    public bool IsDateEnabled => StatusIndex != StatusCancelled;

    public bool IsHighlighted { get; init; }

    public Avalonia.Media.IBrush RowBorder => IsHighlighted ? HighlightBrush : Avalonia.Media.Brushes.Transparent;

    private static readonly Avalonia.Media.IBrush HighlightBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7C5CFF"));
}

public sealed partial class ExecutionViewModel : ObservableObject
{
    private readonly Scenario _scenario;
    private readonly Schedule _schedule;
    private readonly Dictionary<string, string> _campaignNames;
    private readonly (string CampaignId, int SortieNo)? _focus;

    private readonly List<IdLabel> _aircraftChoices;
    private readonly List<IdLabel> _simulatorChoices;
    private readonly List<(string Id, string Label)> _pilotMaster;
    private readonly List<(string Id, string Label)> _engineerMaster;

    public int HighlightIndex { get; private set; } = -1;

    public ExecutionViewModel(Scenario scenario, Scenario runSnapshot, Schedule schedule,
        DateOnly? defaultAsOf = null, (string CampaignId, int SortieNo)? focus = null)
    {
        _scenario = scenario;
        _schedule = schedule;
        _focus = focus;
        _campaignNames = runSnapshot.Campaigns.ToDictionary(c => c.Id, c => c.Name);

        _aircraftChoices = scenario.Aircraft
            .Select(a => new IdLabel(a.Id, string.IsNullOrWhiteSpace(a.Name) ? a.Id : $"{a.Id} ({a.Name})"))
            .ToList();
        _simulatorChoices = scenario.Simulators
            .Select(s => new IdLabel(s.Id, string.IsNullOrWhiteSpace(s.Name) ? s.Id : $"{s.Id} ({s.Name})"))
            .ToList();
        _pilotMaster = scenario.Pilots.Select(p => (p.Id, Label: Crew(p))).ToList();
        _engineerMaster = scenario.Engineers.Select(e => (e.Id, Label: Crew(e))).ToList();

        static string Crew(Resource r) => string.IsNullOrWhiteSpace(r.ShortName) ? r.Name : r.ShortName;

        var asOf = defaultAsOf
            ?? (scenario.FreezeDate is { } f
                ? f.AddDays(-1)
                : DateOnly.FromDateTime(DateTime.Today));
        if (asOf < scenario.HorizonStart) asOf = scenario.HorizonStart;
        if (asOf > scenario.HorizonEnd) asOf = scenario.HorizonEnd;
        _asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        Rebuild();
    }

    [ObservableProperty]
    private DateTime? _asOfDate;

    public ObservableCollection<ExecutionRow> Rows { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasRows;

    public bool Applied { get; private set; }

    partial void OnAsOfDateChanged(DateTime? value) => Rebuild();

    private void Rebuild()
    {
        Rows.Clear();
        if (AsOfDate is not { } asOfDt)
        {
            HasRows = false;
            Summary = "Pick an as-of date.";
            return;
        }
        var asOf = DateOnly.FromDateTime(asOfDt);

        HighlightIndex = -1;

        var flights = _schedule.Sorties.Where(s => s.Date <= asOf)
            .Select(s => (s.Date, s.Block, Build: (Func<ExecutionRow>)(() => FlightRow(s))));
        var sims = _schedule.SimulatorSorties.Where(s => s.Date <= asOf)
            .Select(s => (s.Date, s.Block, Build: (Func<ExecutionRow>)(() => SimRow(s))));
        foreach (var item in flights.Concat(sims).OrderBy(x => x.Date).ThenBy(x => x.Block))
        {
            var row = item.Build();
            if (row.IsHighlighted) HighlightIndex = Rows.Count;
            Rows.Add(row);
        }
        HasRows = Rows.Count > 0;
        Summary = Rows.Count == 0
            ? $"No sorties on or before {asOf:yyyy-MM-dd} in the displayed run."
            : $"{Rows.Count} sortie(s) up to {asOf:yyyy-MM-dd} · planning resumes {asOf.AddDays(1):yyyy-MM-dd}.";
    }

    private ExecutionRow FlightRow(Sortie s)
    {
        var plannedPilots = new HashSet<string>(s.AdditionalPilotIds) { s.PilotId };
        var (pilotChoices, engineerChoices) = CrewChoices(plannedPilots, new HashSet<string>(s.EngineerIds));
        var row = new ExecutionRow
        {
            FlightSource = s,
            CampaignName = _campaignNames.GetValueOrDefault(s.CampaignId, s.CampaignId),
            PlannedTp = s.TestPointsFlown,
            AircraftChoices = _aircraftChoices,
            SelectedAircraft = _aircraftChoices.FirstOrDefault(a => a.Id == s.AircraftId) ?? _aircraftChoices.FirstOrDefault(),
            PilotChoices = pilotChoices,
            EngineerChoices = engineerChoices,
            StatusIndex = s.Aborted ? ExecutionRow.StatusAborted : ExecutionRow.StatusFlown,
            ActualTp = (decimal)s.TestPointsFlown,
            ActualDate = s.Date.ToDateTime(TimeOnly.MinValue),
            IsHighlighted = _focus is { } f && f.CampaignId == s.CampaignId && f.SortieNo == s.SortieNo,
        };
        HookCrew(row, pilotChoices, engineerChoices);
        return row;
    }

    private ExecutionRow SimRow(SimulatorSortie s)
    {
        var plannedPilots = new HashSet<string>(s.AdditionalPilotIds);
        if (s.PilotId.Length > 0) plannedPilots.Add(s.PilotId);
        var (pilotChoices, engineerChoices) = CrewChoices(plannedPilots, new HashSet<string>(s.EngineerIds));
        var row = new ExecutionRow
        {
            SimSource = s,
            CampaignName = _campaignNames.GetValueOrDefault(s.CampaignId, s.CampaignId),
            PlannedTp = s.TestPointsFlown,
            AircraftChoices = _simulatorChoices,
            SelectedAircraft = _simulatorChoices.FirstOrDefault(a => a.Id == s.SimulatorId) ?? _simulatorChoices.FirstOrDefault(),
            PilotChoices = pilotChoices,
            EngineerChoices = engineerChoices,
            StatusIndex = s.Aborted ? ExecutionRow.StatusAborted : ExecutionRow.StatusFlown,
            ActualTp = (decimal)s.TestPointsFlown,
            ActualDate = s.Date.ToDateTime(TimeOnly.MinValue),
            IsHighlighted = _focus is { } f && f.CampaignId == s.CampaignId && f.SortieNo == s.SortieNo,
        };
        HookCrew(row, pilotChoices, engineerChoices);
        return row;
    }

    private (ObservableCollection<SelectableOption> Pilots, ObservableCollection<SelectableOption> Engineers)
        CrewChoices(HashSet<string> plannedPilots, HashSet<string> plannedEngs) =>
        (new ObservableCollection<SelectableOption>(
                _pilotMaster.Select(p => new SelectableOption(p.Id, p.Label, plannedPilots.Contains(p.Id)))),
         new ObservableCollection<SelectableOption>(
                _engineerMaster.Select(e => new SelectableOption(e.Id, e.Label, plannedEngs.Contains(e.Id)))));

    private static void HookCrew(ExecutionRow row,
        ObservableCollection<SelectableOption> pilots, ObservableCollection<SelectableOption> engineers)
    {
        void Hook(SelectableOption o) => o.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectableOption.IsSelected)) row.RefreshCrewSummaries();
        };
        foreach (var o in pilots) Hook(o);
        foreach (var o in engineers) Hook(o);
    }

    public string? Apply()
    {
        if (AsOfDate is not { } asOfDt) return "Pick an as-of date.";
        var asOf = DateOnly.FromDateTime(asOfDt);

        foreach (var r in Rows.Where(r => r.StatusIndex != ExecutionRow.StatusCancelled))
        {
            if (r.ActualDate is not { } d)
                return $"Sortie #{r.SortieNo} ({r.CampaignName}) has no actual date.";
            var date = DateOnly.FromDateTime(d);
            if (date > asOf)
                return $"Sortie #{r.SortieNo} ({r.CampaignName}) flew on {date:yyyy-MM-dd}, " +
                       $"after the as-of date {asOf:yyyy-MM-dd} · move the as-of date forward.";
            if (date < _scenario.HorizonStart)
                return $"Sortie #{r.SortieNo} ({r.CampaignName}) flew before the horizon start.";
            if (r.SelectedAircraft is null)
                return $"Sortie #{r.SortieNo} ({r.CampaignName}) has no aircraft selected.";
        }

        var sorties = new List<Sortie>();
        var simSorties = new List<SimulatorSortie>();
        var kept = new Dictionary<(string CampaignId, int SortieNo), DateOnly>();
        foreach (var r in Rows.Where(r => r.StatusIndex != ExecutionRow.StatusCancelled))
        {
            bool aborted = r.StatusIndex == ExecutionRow.StatusAborted;

            var pilots = r.PilotChoices.Where(o => o.IsSelected).Select(o => o.Id).ToList();
            var additional = pilots.Skip(1).ToList();
            var engineers = r.EngineerChoices.Where(o => o.IsSelected).Select(o => o.Id).ToList();
            var date = DateOnly.FromDateTime(r.ActualDate!.Value);
            double tp = aborted ? 0 : (double)Math.Round(r.ActualTp);

            if (r.SimSource is { } ss)
            {
                simSorties.Add(new SimulatorSortie
                {
                    CampaignId = ss.CampaignId,
                    Date = date,
                    Block = ss.Block,
                    SortieNo = ss.SortieNo,
                    SimulatorId = r.SelectedAircraft!.Id,
                    PilotId = pilots.FirstOrDefault() ?? string.Empty,
                    AdditionalPilotIds = additional,
                    EngineerIds = engineers,
                    TestPointsFlown = tp,
                    Aborted = aborted,
                    IsActual = true,
                });
                continue;
            }

            var s = r.FlightSource!;
            sorties.Add(new Sortie
            {
                CampaignId = s.CampaignId,
                Date = date,
                Block = s.Block,
                SortieNo = s.SortieNo,
                AircraftId = r.SelectedAircraft!.Id,
                PilotId = pilots.FirstOrDefault() ?? s.PilotId,
                AdditionalPilotIds = additional,
                EngineerIds = engineers,
                TestPointsFlown = tp,
                Aborted = aborted,
                IsActual = true,
                RehearsalDate = s.RehearsalDate,
                SupportAircraftIds = new List<string>(s.SupportAircraftIds),
                SupportPilotIds = new List<string>(s.SupportPilotIds),
                ExternalSupportIds = new(s.ExternalSupportIds),
            });
            kept[(s.CampaignId, s.SortieNo)] = date;
        }

        var rehearsals = _schedule.Rehearsals
            .Where(r => r.Date <= asOf)
            .Select(r => new Rehearsal
            {
                CampaignId = r.CampaignId,
                Date = r.Date,
                Block = r.Block,
                SortieNo = r.SortieNo,
                SortieDate = kept.TryGetValue((r.CampaignId, r.SortieNo), out var d) ? d : r.SortieDate,
                PilotId = r.PilotId,
                AdditionalPilotIds = new(r.AdditionalPilotIds),
                SupportPilotIds = new(r.SupportPilotIds),
                SimulatorId = r.SimulatorId,
                LocationId = r.LocationId,
                EngineerIds = new List<string>(r.EngineerIds),
                IsActual = true,
            })
            .ToList();

        _scenario.ActualSorties = sorties;
        _scenario.ActualSimulatorSorties = simSorties;
        _scenario.ActualRehearsals = rehearsals;
        _scenario.FreezeDate = asOf.AddDays(1);
        Applied = true;
        return null;
    }
}
