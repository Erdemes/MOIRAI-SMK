using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Mapping;
using MOIRAI.Persistence;
using MOIRAI.Presentation;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public partial class PlanViewModel : ViewModelBase
{
    private readonly IRepository _repo;
    private readonly IDialogService _dialogs;
    private readonly AnalysisHandoff _handoff;
    private readonly ScenarioListStore _scenarioList;

    public PlanViewModel(IRepository repo, IDialogService dialogs, AnalysisHandoff handoff,
        ScenarioListStore scenarioList)
    {
        _repo = repo;
        _dialogs = dialogs;
        _handoff = handoff;
        _scenarioList = scenarioList;
    }

    public PlanSettingsForm Form { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccess))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private bool _hasResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccess))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private bool _meetsAllDeadlines;

    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasDiagnostics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunStatus))]
    private string _runStatusMessage = string.Empty;

    public bool HasRunStatus => !string.IsNullOrWhiteSpace(RunStatusMessage);

    [ObservableProperty] private GanttData? _gantt;

    [ObservableProperty] private GanttData? _testPointsGantt;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowSuccess => HasResult && MeetsAllDeadlines;
    public bool ShowWarning => HasResult && !MeetsAllDeadlines;

    public ObservableCollection<SelectableOption> CampaignOptions { get; } = new();

    public ObservableCollection<ScopeGroupNode> ScopeGroups { get; } = new();

    public ObservableCollection<SelectableOption> UngroupedScope { get; } = new();

    [ObservableProperty] private bool _hasCampaigns;

    public ObservableCollection<ValidationRow> ValidationIssues { get; } = new();
    [ObservableProperty] private bool _hasValidationIssues;
    [ObservableProperty] private string _validationSummary = string.Empty;

    public ObservableCollection<ScheduleRow> Sorties { get; } = new();
    public ObservableCollection<WorkloadRow> Workload { get; } = new();
    public ObservableCollection<AircraftUtilizationRow> AircraftUtilization { get; } = new();
    public ObservableCollection<CampaignDetailRow> CampaignDetails { get; } = new();

    public ObservableCollection<CampaignResultGroup> ResultGroups { get; } = new();
    public ObservableCollection<string> Diagnostics { get; } = new();

    public ObservableCollection<GanttLegendItem> LegendItems { get; } = new();

    private Scenario? _displayScenario;
    private Schedule? _displaySchedule;
    private IdleReport? _idle;
    private bool _suppressFilters;

    public ObservableCollection<SelectableOption> CampaignFilterOptions { get; } = new();

    public ObservableCollection<ScopeGroupNode> CampaignFilterGroups { get; } = new();
    public ObservableCollection<SelectableOption> UngroupedFilter { get; } = new();

    public ObservableCollection<SelectableOption> AircraftFilterOptions { get; } = new();
    public ObservableCollection<SelectableOption> PilotFilterOptions { get; } = new();
    public ObservableCollection<SelectableOption> FteFilterOptions { get; } = new();

    [ObservableProperty] private string _filterSummary = string.Empty;
    [ObservableProperty] private bool _isFiltered;

    [ObservableProperty] private bool _hideNonFlyableDays;

    [ObservableProperty] private bool _showIdleMarkers;

    public string ActiveScenarioName => _repo.GetScenario().Name;

    public bool CanAnalyze => _repo.GetScenario().RunSchedule is not null;

    [ObservableProperty] private string _currentScenarioLabel = string.Empty;

    public override void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(ActiveScenarioName));
        OnPropertyChanged(nameof(CanAnalyze));
        ReloadCampaignOptions();
        LoadFromActive();
        RebuildFamilyResults();
        RefreshExecutionState();
        RefreshValidation();
    }

    private void RefreshValidation()
    {
        ValidationIssues.Clear();
        var issues = ScenarioValidator.Validate(_repo.GetScenario(), SelectedCampaignIds());
        foreach (var i in issues)
            ValidationIssues.Add(new ValidationRow
            {
                Severity = i.Severity,
                Category = i.Category,
                Text = i.CampaignName is { } c ? $"{c} · {i.Message}" : i.Message,
            });

        HasValidationIssues = ValidationIssues.Count > 0;
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warns = issues.Count(i => i.Severity == IssueSeverity.Warning);
        int infos = issues.Count(i => i.Severity == IssueSeverity.Info);
        var parts = new List<string>();
        if (errors > 0) parts.Add($"⛔ {errors} error{(errors == 1 ? "" : "s")}");
        if (warns > 0) parts.Add($"⚠ {warns} warning{(warns == 1 ? "" : "s")}");
        if (infos > 0) parts.Add($"ℹ {infos} note{(infos == 1 ? "" : "s")}");
        ValidationSummary = string.Join("  ·  ", parts);
    }

    public ObservableCollection<FamilyResultRow> FamilyResults { get; } = new();
    [ObservableProperty] private bool _hasFamilyResults;

    private void RebuildFamilyResults()
    {
        FamilyResults.Clear();
        var all = _repo.GetScenarios();
        var active = _repo.GetScenario();

        var root = active;
        var up = new HashSet<string>();
        while (!string.IsNullOrEmpty(root.ParentScenarioId) && up.Add(root.Id)
               && all.FirstOrDefault(s => s.Id == root.ParentScenarioId) is { } parent)
            root = parent;

        var byParent = all
            .Where(s => !string.IsNullOrEmpty(s.ParentScenarioId))
            .ToLookup(s => s.ParentScenarioId!);
        var seen = new HashSet<string>();

        void Walk(Scenario sc, int depth)
        {
            if (!seen.Add(sc.Id)) return;
            FamilyResults.Add(new FamilyResultRow(sc, depth, sc.Id == active.Id));
            foreach (var child in byParent[sc.Id]) Walk(child, depth + 1);
        }

        Walk(root, 0);
        if (FamilyResults.Count < 2) FamilyResults.Clear();
        HasFamilyResults = FamilyResults.Count > 0;
    }

    private void ReloadCampaignOptions()
    {
        CampaignOptions.Clear();
        foreach (var c in _repo.GetCampaigns())
        {
            var opt = new SelectableOption(c.Id, c.Name, true);
            opt.PropertyChanged += (_, _) => RefreshValidation();
            CampaignOptions.Add(opt);
        }
        HasCampaigns = CampaignOptions.Count > 0;
        GroupOptionsByPhase(_repo.GetScenario(), CampaignOptions, ScopeGroups, UngroupedScope);
    }

    private static void GroupOptionsByPhase(
        Scenario scenario,
        IReadOnlyList<SelectableOption> flat,
        ObservableCollection<ScopeGroupNode> groupsOut,
        ObservableCollection<SelectableOption> ungroupedOut)
    {
        var optById = flat.ToDictionary(o => o.Id);
        var campById = scenario.Campaigns.ToDictionary(c => c.Id);
        DateOnly CampKey(string id) => campById.TryGetValue(id, out var c)
            ? CampaignDeadlines.EffectiveMilestone(scenario, c) ?? DateOnly.MaxValue
            : DateOnly.MaxValue;

        groupsOut.Clear();
        ungroupedOut.Clear();
        var grouped = new HashSet<string>();

        foreach (var g in scenario.CampaignGroups.OrderBy(g => g.TargetEndDate ?? DateOnly.MaxValue))
        {
            var children = scenario.Campaigns
                .Where(c => c.GroupId == g.Id && optById.ContainsKey(c.Id))
                .OrderBy(c => CampKey(c.Id))
                .Select(c => optById[c.Id])
                .ToList();
            if (children.Count == 0) continue;
            foreach (var o in children) grouped.Add(o.Id);
            groupsOut.Add(new ScopeGroupNode(g.Name, children));
        }

        foreach (var o in flat.Where(o => !grouped.Contains(o.Id)).OrderBy(o => CampKey(o.Id)))
            ungroupedOut.Add(o);
    }

    [RelayCommand]
    private void SelectAllCampaigns()
    {
        foreach (var o in CampaignOptions) o.IsSelected = true;
    }

    [RelayCommand]
    private void ClearCampaigns()
    {
        foreach (var o in CampaignOptions) o.IsSelected = false;
    }

    private void LoadFromActive()
    {
        var sc = _repo.GetScenario();
        Form.LoadFrom(ScenarioSettingsMapper.FromScenario(sc));

        if (sc.ScopeCampaignIds.Count > 0)
            foreach (var o in CampaignOptions)
                o.IsSelected = sc.ScopeCampaignIds.Contains(o.Id);

        if (sc is { RunSnapshot: { } snapshot, RunSchedule: { } schedule })
        {
            Populate(snapshot, schedule);
            CurrentScenarioLabel = $"Scenario: {sc.Name}";
        }
        else
        {
            HasResult = false;
            CurrentScenarioLabel = $"Scenario: {sc.Name} (not run yet)";
        }
    }

    private bool PersistSettings()
    {
        var snapshot = Form.ToSnapshot(SelectedCampaignIds());
        if (ScenarioSettingsMapper.Validate(snapshot) is { } error)
        {
            ErrorMessage = error;
            return false;
        }
        ErrorMessage = null;

        var (config, calendar, flyable, start, end) = ScenarioSettingsMapper.ToDomain(snapshot);
        var scenario = _repo.GetScenario();
        scenario.ScopeCampaignIds = SelectedCampaignIds().ToList();
        scenario.MasterDeadline = snapshot.MasterDeadline is { } md ? DateOnly.FromDateTime(md) : null;
        _repo.SaveSettings(config, calendar, flyable, start, end);
        return true;
    }

    public void StashIntoActive()
    {
        if (!PersistSettings()) ErrorMessage = null;
    }

    private HashSet<string> SelectedCampaignIds() =>
        CampaignOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();

    [RelayCommand]
    private void SaveSettings()
    {
        if (PersistSettings()) ErrorMessage = null;
        RefreshValidation();
    }

    [RelayCommand]
    private async Task Generate()
    {
        RunStatusMessage = string.Empty;
        if (!PersistSettings()) return;

        var selectedIds = SelectedCampaignIds();
        if (selectedIds.Count == 0)
        {
            ErrorMessage = "Select at least one campaign to plan.";
            return;
        }

        var scenario = _repo.GetScenario();
        var snapshot = ScenarioJson.Clone(scenario);
        snapshot.Id = string.Empty;
        snapshot.Campaigns = snapshot.Campaigns.Where(c => selectedIds.Contains(c.Id)).ToList();
        snapshot.ScopeCampaignIds = new List<string>();
        snapshot.RunSnapshot = null;
        snapshot.RunSchedule = null;
        snapshot.CampaignResults = null;
        snapshot.McResult = null;
        snapshot.McReplay = null;

        bool useSearch = snapshot.Config.UseRevisingEngine;
        string detail = useSearch ? "Searching schedules…" : "Scheduling…";
        var dialog = new ProgressDialogViewModel
        {
            Title = "Planning · " + EngineOptions.Label(useSearch),
            Detail = detail,
            IsIndeterminate = !useSearch,
        };
        var progress = new Progress<double>(f => dialog.Report(detail, f));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        Schedule schedule;
        try
        {
            var ct = dialog.Token;
            schedule = await Task.Run(
                () => new RevisingScheduler(EngineOptions.Advanced(useSearch)).Run(snapshot, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            RunStatusMessage = "Run cancelled · the previous plan is unchanged.";
            return;
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
        }

        var results = snapshot.Campaigns.Select(c =>
        {
            var (status, end) = PlanResultsPresenter.StatusOf(snapshot, schedule, c);
            return new CampaignOutcome
            {
                CampaignId = c.Id,
                CampaignName = c.Name,
                Status = status,
                EndText = end,
            };
        }).ToList();
        int met = results.Count(r => r.Status is "Met" or "Done");

        scenario.RunSnapshot = snapshot;
        scenario.RunSchedule = schedule;
        scenario.CampaignResults = results;
        scenario.McResult = null;
        scenario.McReplay = null;
        string engineLabel = EngineOptions.Label(useSearch);
        scenario.Status = $"{met}/{results.Count} met · {schedule.Sorties.Count + schedule.SimulatorSorties.Count} sorties · {DescribeFlyable(snapshot.FlyableModel)} · {engineLabel}";
        if (ProgramReporter.Build(snapshot, schedule).SlackDays is { } slack)
            scenario.Status += slack >= 0 ? $" · {slack}d margin" : $" · ⚠ {-slack}d overrun";
        _repo.SaveSession();

        _scenarioList.RefreshStatus(scenario.Id);
        OnPropertyChanged(nameof(CanAnalyze));

        Populate(snapshot, schedule);
        RebuildFamilyResults();
        RefreshValidation();
        CurrentScenarioLabel = $"Scenario: {scenario.Name}";
        RunStatusMessage = $"Run complete · {met}/{results.Count} targets met · " +
                           $"{schedule.Sorties.Count + schedule.SimulatorSorties.Count} sortie(s) · {engineLabel} · finished {DateTime.Now:HH:mm:ss}";
    }

    private static string DescribeFlyable(FlyableDayModel model) => model switch
    {
        RateBasedFlyableDays r => $"rate {r.Rate:0.##}",
        ProbabilisticFlyableDays p => $"probabilistic (φ={p.Persistence:0.##})",
        _ => model.GetType().Name,
    };

    private void Populate(Scenario scenario, Schedule schedule)
    {
        _idle = IdleReport.Build(scenario, schedule);

        Workload.Clear();
        foreach (var row in PlanResultsPresenter.Workload(scenario, schedule, _idle)) Workload.Add(row);

        AircraftUtilization.Clear();
        foreach (var row in PlanResultsPresenter.AircraftUtilization(scenario, schedule, _idle))
            AircraftUtilization.Add(row);

        CampaignDetails.Clear();
        foreach (var row in PlanResultsPresenter.CampaignDetails(scenario, schedule)) CampaignDetails.Add(row);

        ResultGroups.Clear();
        foreach (var g in PlanResultsPresenter.CampaignResultGroups(scenario, schedule)) ResultGroups.Add(g);

        Diagnostics.Clear();
        foreach (var d in schedule.Diagnostics) Diagnostics.Add(d);
        HasDiagnostics = Diagnostics.Count > 0;

        var report = ProgramReporter.Build(scenario, schedule);
        MeetsAllDeadlines = report.Delivered;
        SummaryText = $"{schedule.Sorties.Count + schedule.SimulatorSorties.Count} sorties · {schedule.Rehearsals.Count} rehearsals";
        if (report.ProgramFinish is { } programFinish)
            SummaryText += $" · program finish {programFinish:yyyy-MM-dd}";
        if (report.SlackDays is { } slack)
            SummaryText += slack >= 0
                ? $" · {slack}d deadline margin"
                : $" · {-slack}d overrun ({report.OverdueSorties} overdue sorties)";

        _displayScenario = scenario;
        _displaySchedule = schedule;
        LegendItems.Clear();
        foreach (var item in GanttPalette.Legend(scenario)) LegendItems.Add(item);
        RebuildFilterOptions(scenario);
        ApplyResultFilters();
        HasResult = true;
    }

    private void RebuildFilterOptions(Scenario scenario)
    {
        _suppressFilters = true;
        Fill(CampaignFilterOptions, scenario.Campaigns.Select(c => (c.Id, c.Name)));
        Fill(AircraftFilterOptions, scenario.Aircraft.Select(a => (a.Id, a.Name)));
        Fill(PilotFilterOptions, scenario.Pilots.Select(p => (p.Id, p.Name)));
        Fill(FteFilterOptions, scenario.Engineers.Select(e => (e.Id, e.Name)));
        GroupOptionsByPhase(scenario, CampaignFilterOptions, CampaignFilterGroups, UngroupedFilter);
        _suppressFilters = false;

        void Fill(ObservableCollection<SelectableOption> list, IEnumerable<(string Id, string Name)> items)
        {
            list.Clear();
            foreach (var (id, name) in items)
            {
                var opt = new SelectableOption(id, name, false);
                opt.PropertyChanged += (_, _) => { if (!_suppressFilters) ApplyResultFilters(); };
                list.Add(opt);
            }
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilters = true;
        foreach (var o in CampaignFilterOptions.Concat(AircraftFilterOptions)
                     .Concat(PilotFilterOptions).Concat(FteFilterOptions))
            o.IsSelected = false;
        _suppressFilters = false;
        ApplyResultFilters();
    }

    private void ApplyResultFilters()
    {
        if (_suppressFilters || _displayScenario is null || _displaySchedule is null) return;

        var r = ResultFilter.Apply(_displayScenario, _displaySchedule,
            Selected(CampaignFilterOptions), Selected(AircraftFilterOptions),
            Selected(PilotFilterOptions), Selected(FteFilterOptions));

        IsFiltered = r.IsFiltered;
        Sorties.Clear();
        foreach (var row in r.Rows) Sorties.Add(row);
        _idle ??= IdleReport.Build(_displayScenario, _displaySchedule);
        Gantt = new GanttData
        {
            Scenario = _displayScenario, Schedule = r.GanttSchedule, Pins = CurrentPins(), Idle = _idle,
        };

        var camps = Selected(CampaignFilterOptions);
        TestPointsGantt = new GanttData
        {
            Scenario = _displayScenario,
            Schedule = _displaySchedule,
            CampaignIds = camps.Count > 0 ? camps : null,
        };
        FilterSummary = r.FilterSummary;

        static HashSet<string> Selected(IEnumerable<SelectableOption> options) =>
            options.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();
    }

    [RelayCommand]
    private async Task ExportExcel()
    {
        var path = await _dialogs.SaveFileAsync(
            "Export schedule to Excel", "Excel workbook", "schedule.xlsx", "xlsx", "*.xlsx");
        if (path is not null) ExportToExcel(path);
    }

    [RelayCommand]
    private async Task ExportPdf()
    {
        if (_displayScenario is null || _displaySchedule is null) return;
        var path = await _dialogs.SaveFileAsync(
            "Export PDF report", "PDF document", "report.pdf", "pdf", "*.pdf");
        if (path is null) return;
        try
        {
            PdfReporter.Export(_displayScenario, _displaySchedule, path);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not export the PDF report: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportHolidaysFromFile()
    {
        var path = await _dialogs.OpenFileAsync("Import holidays from CSV", "CSV / text", "*.csv", "*.txt");
        if (path is null) return;
        try
        {
            ImportHolidays(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read the file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UndockResults() => _dialogs.ShowResultsWindow(this);

    [RelayCommand]
    private void OpenGanttWindow() => _dialogs.ShowGanttWindow(this);

    [RelayCommand]
    private void OpenTestPointsWindow() => _dialogs.ShowTestPointsWindow(this);

    [RelayCommand]
    private async Task ViewDayLog()
    {
        var scenario = _repo.GetScenario();
        if (scenario.RunSnapshot is not { } snapshot || scenario.RunSchedule is null)
        {
            ErrorMessage = "Run a plan first · there is no schedule to explain.";
            return;
        }

        bool useSearch = snapshot.Config.UseRevisingEngine;
        string detail = useSearch ? "Replaying the advanced search…" : "Replaying the plan…";
        var dialog = new ProgressDialogViewModel
        {
            Title = "Building the decision log…",
            Detail = detail,
            IsIndeterminate = !useSearch,
        };
        var progress = new Progress<double>(f => dialog.Report(detail, f));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        Schedule captured;
        try
        {
            var ct = dialog.Token;
            captured = await Task.Run(
                () => new RevisingScheduler(EngineOptions.Advanced(useSearch)) { CaptureDecisions = true }
                    .Run(snapshot, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
        }

        _dialogs.ShowDecisionLogWindow(new DecisionLogViewModel(snapshot, captured));
    }

    [RelayCommand]
    private void Analyze()
    {
        if (_repo.GetScenario().RunSchedule is not null) _handoff.Request();
    }

    public string ExecutionSummary
    {
        get
        {
            var sc = _repo.GetScenario();
            return sc.FreezeDate is { } f
                ? $"Execution: {sc.ActualSorties.Count} actual sortie(s) recorded · replanning from {f:yyyy-MM-dd}"
                : string.Empty;
        }
    }

    public bool HasExecution => _repo.GetScenario().FreezeDate is not null;

    private void RefreshExecutionState()
    {
        OnPropertyChanged(nameof(ExecutionSummary));
        OnPropertyChanged(nameof(HasExecution));
        OnPropertyChanged(nameof(PinSummary));
        OnPropertyChanged(nameof(HasPins));
    }

    public string PinSummary
    {
        get
        {
            int n = _repo.GetScenario().SortiePins.Count;
            return n == 0 ? string.Empty
                : $"Pins: {n} sortie(s) pinned to their crew + aircraft";
        }
    }

    public bool HasPins => _repo.GetScenario().SortiePins.Count > 0;

    private IReadOnlyDictionary<(string CampaignId, int SortieNo), SortiePin>? CurrentPins()
    {
        var sc = _repo.GetScenario();
        if (sc.SortiePins.Count == 0 || !ReferenceEquals(_displaySchedule, sc.RunSchedule)) return null;
        var map = new Dictionary<(string, int), SortiePin>();
        foreach (var p in sc.SortiePins) map[(p.CampaignId, p.SortieNo)] = p;
        return map;
    }

    public async Task ApplyPinRequest(Views.GanttPinRequest req)
    {
        var sc = _repo.GetScenario();
        if (_displaySchedule is not { } sch || !ReferenceEquals(sch, sc.RunSchedule))
        {
            ErrorMessage = "Pins apply to the active scenario's own run · run it first.";
            return;
        }
        string camp = req.Sortie?.CampaignId ?? req.SimSortie!.CampaignId;
        int no = req.Sortie?.SortieNo ?? req.SimSortie!.SortieNo;
        DateOnly date = req.Sortie?.Date ?? req.SimSortie!.Date;

        IEnumerable<SortiePin> Planned(Func<string, int, DateOnly, bool> keep) =>
            sch.Sorties.Where(s => !s.IsActual && keep(s.CampaignId, s.SortieNo, s.Date)).Select(SortiePin.From)
                .Concat(sch.SimulatorSorties.Where(s => !s.IsActual && keep(s.CampaignId, s.SortieNo, s.Date))
                    .Select(SortiePin.From));

        void Upsert(SortiePin p)
        {
            sc.SortiePins.RemoveAll(x => x.CampaignId == p.CampaignId && x.SortieNo == p.SortieNo);
            sc.SortiePins.Add(p);
        }

        string message;
        switch (req.Action)
        {
            case Views.GanttPinAction.Pin:
                foreach (var p in Planned((c, n, _) => c == camp && n == no)) Upsert(p);
                message = $"Sortie #{no} pinned";
                break;
            case Views.GanttPinAction.PinDay:
                Upsert(new SortiePin { CampaignId = camp, SortieNo = no, Date = date });
                message = $"Sortie #{no} pinned to {date:yyyy-MM-dd}";
                break;
            case Views.GanttPinAction.EditPin:
            {
                if (!sc.Campaigns.Any(c => c.Id == camp))
                {
                    ErrorMessage = "This campaign no longer exists in the scenario.";
                    return;
                }
                var existing = sc.SortiePins.LastOrDefault(x => x.CampaignId == camp && x.SortieNo == no);
                var editor = new PinEditorViewModel(sc, req.Sortie, req.SimSortie, existing);
                await _dialogs.ShowPinEditorDialogAsync(editor);
                if (editor.Result is null) return;
                Upsert(editor.Result);
                message = $"Sortie #{no} pinned";
                break;
            }
            case Views.GanttPinAction.Unpin:
                sc.SortiePins.RemoveAll(x => x.CampaignId == camp && x.SortieNo == no);
                message = $"Sortie #{no} unpinned";
                break;
            case Views.GanttPinAction.PinCampaign:
            {
                var pins = Planned((c, _, _) => c == camp).ToList();
                foreach (var p in pins) Upsert(p);
                message = $"{pins.Count} sortie(s) of the campaign pinned";
                break;
            }
            case Views.GanttPinAction.UnpinCampaign:
                sc.SortiePins.RemoveAll(x => x.CampaignId == camp);
                message = "Campaign's pins removed";
                break;
            case Views.GanttPinAction.PinUpToDate:
            {
                var pins = Planned((_, _, d) => d <= date).ToList();
                foreach (var p in pins) Upsert(p);
                message = $"{pins.Count} sortie(s) up to {date:yyyy-MM-dd} pinned";
                break;
            }
            default:
                sc.SortiePins.Clear();
                message = "All pins cleared";
                break;
        }

        _repo.SaveSession();
        ErrorMessage = null;
        RunStatusMessage = $"{message} · run the plan again to replan around the pins.";
        ApplyResultFilters();
        RefreshExecutionState();
        RefreshValidation();
    }

    [RelayCommand]
    private void ClearPins()
    {
        var sc = _repo.GetScenario();
        if (sc.SortiePins.Count == 0) return;
        sc.SortiePins.Clear();
        _repo.SaveSession();
        RunStatusMessage = "All pins cleared · run the plan again to replan freely.";
        ApplyResultFilters();
        RefreshExecutionState();
        RefreshValidation();
    }

    [RelayCommand]
    private Task RecordActuals() => RecordActualsCore(null, null);

    public Task RecordActualsAt(Sortie clicked)
    {
        var asOf = clicked.Date;
        if (_repo.GetScenario().FreezeDate is { } f && f.AddDays(-1) > asOf)
            asOf = f.AddDays(-1);
        return RecordActualsCore(asOf, (clicked.CampaignId, clicked.SortieNo));
    }

    public Task RecordActualsAt(SimulatorSortie clicked)
    {
        var asOf = clicked.Date;
        if (_repo.GetScenario().FreezeDate is { } f && f.AddDays(-1) > asOf)
            asOf = f.AddDays(-1);
        return RecordActualsCore(asOf, (clicked.CampaignId, clicked.SortieNo));
    }

    private async Task RecordActualsCore(DateOnly? defaultAsOf, (string CampaignId, int SortieNo)? focus)
    {
        var sc = _repo.GetScenario();
        if (sc.RunSnapshot is not { } snapshot || sc.RunSchedule is not { } schedule)
        {
            ErrorMessage = "Run the scenario first · actuals are recorded against the displayed run.";
            return;
        }
        var vm = new ExecutionViewModel(sc, snapshot, schedule, defaultAsOf, focus);
        await _dialogs.ShowExecutionDialogAsync(vm);
        if (!vm.Applied) return;

        await Generate();
        RefreshExecutionState();
    }

    [RelayCommand]
    private async Task ClearActuals()
    {
        var sc = _repo.GetScenario();
        if (sc.FreezeDate is null && sc.ActualSorties.Count == 0 && sc.ActualSimulatorSorties.Count == 0) return;
        sc.FreezeDate = null;
        sc.ActualSorties.Clear();
        sc.ActualRehearsals.Clear();
        sc.ActualSimulatorSorties.Clear();

        await Generate();
        RefreshExecutionState();
    }

    public void ExportToExcel(string path)
    {
        if (_displayScenario is null || _displaySchedule is null) return;
        try
        {
            ExcelExporter.Export(_displayScenario, _displaySchedule, path);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not export to Excel: {ex.Message}";
        }
    }

    public void ImportHolidays(string csvText)
    {
        var (merged, found) = HolidayList.MergeCsv(Form.Holidays, csvText);
        if (found == 0)
        {
            ErrorMessage = "No dates found. Use yyyy-MM-dd, dd.MM.yyyy or dd/MM/yyyy.";
            return;
        }
        ErrorMessage = null;
        Form.Holidays = merged;
    }
}
