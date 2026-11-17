using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Presentation;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed partial class ResultFilterModel : ObservableObject
{
    private readonly Scenario _scenario;
    private readonly Schedule _schedule;
    private bool _suppress;

    public ObservableCollection<SelectableOption> CampaignFilterOptions { get; } = new();
    public ObservableCollection<SelectableOption> AircraftFilterOptions { get; } = new();
    public ObservableCollection<SelectableOption> PilotFilterOptions { get; } = new();
    public ObservableCollection<SelectableOption> FteFilterOptions { get; } = new();

    [ObservableProperty] private GanttData? _gantt;
    [ObservableProperty] private GanttData? _testPointsGantt;
    [ObservableProperty] private bool _isFiltered;
    [ObservableProperty] private string _filterSummary = string.Empty;

    [ObservableProperty] private bool _hideNonFlyableDays;

    [ObservableProperty] private bool _showIdleMarkers;

    private MOIRAI.Reporting.IdleReport? _idle;

    public ResultFilterModel(Scenario scenario, Schedule schedule)
    {
        _scenario = scenario;
        _schedule = schedule;
        BuildOptions();
        Apply();
    }

    private void BuildOptions()
    {
        _suppress = true;
        Fill(CampaignFilterOptions, _scenario.Campaigns.Select(c => (c.Id, c.Name)));
        Fill(AircraftFilterOptions, _scenario.Aircraft.Select(a => (a.Id, a.Name)));
        Fill(PilotFilterOptions, _scenario.Pilots.Select(p => (p.Id, p.Name)));
        Fill(FteFilterOptions, _scenario.Engineers.Select(e => (e.Id, e.Name)));
        _suppress = false;
    }

    private void Fill(ObservableCollection<SelectableOption> col, IEnumerable<(string Id, string Name)> items)
    {
        col.Clear();
        foreach (var (id, name) in items)
        {
            var opt = new SelectableOption(id, name, false);
            opt.PropertyChanged += (_, _) => { if (!_suppress) Apply(); };
            col.Add(opt);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppress = true;
        foreach (var o in CampaignFilterOptions.Concat(AircraftFilterOptions)
                     .Concat(PilotFilterOptions).Concat(FteFilterOptions))
            o.IsSelected = false;
        _suppress = false;
        Apply();
    }

    private void Apply()
    {
        var r = ResultFilter.Apply(_scenario, _schedule,
            Selected(CampaignFilterOptions), Selected(AircraftFilterOptions),
            Selected(PilotFilterOptions), Selected(FteFilterOptions));

        IsFiltered = r.IsFiltered;
        _idle ??= MOIRAI.Reporting.IdleReport.Build(_scenario, _schedule);
        Gantt = new GanttData { Scenario = _scenario, Schedule = r.GanttSchedule, Idle = _idle };

        var camps = Selected(CampaignFilterOptions);
        TestPointsGantt = new GanttData
        {
            Scenario = _scenario,
            Schedule = _schedule,
            CampaignIds = camps.Count > 0 ? camps : null,
        };
        FilterSummary = r.FilterSummary;

        static HashSet<string> Selected(IEnumerable<SelectableOption> options) =>
            options.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();
    }
}
