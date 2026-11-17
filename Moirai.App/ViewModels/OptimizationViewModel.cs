using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public partial class OptimizationViewModel : ViewModelBase
{
    private readonly IRepository _repo;
    private readonly IDialogService _dialogs;
    private const int FixedSeed = 12345;

    public OptimizationViewModel(IRepository repo, IDialogService dialogs, ScenarioListStore scenarios)
    {
        _repo = repo;
        _dialogs = dialogs;
        _scenarios = scenarios;
    }

    private readonly ScenarioListStore _scenarios;

    private Scenario Active => _repo.GetScenario();
    private Optimizer.Result? _result;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverOrdering = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverMaintenance = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverLeave = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverCrew = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverStartDates = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverExtraWorkdays;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverAddPilots;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverAddEngineers;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverAddAircraft;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))] private bool _leverAddSimulators;
    [ObservableProperty] private decimal _maxShiftDays = 14;
    [ObservableProperty] private decimal _maxStartDelayDays = 14;
    [ObservableProperty] private decimal _maxExtraWorkdays = 4;
    [ObservableProperty] private decimal _maxAddedPilots = 2;
    [ObservableProperty] private decimal _maxAddedEngineers = 2;
    [ObservableProperty] private decimal _maxAddedAircraft = 2;
    [ObservableProperty] private decimal _maxAddedSimulators = 2;
    [ObservableProperty] private decimal _iterations = 600;
    [ObservableProperty] private decimal _searchRuns = 40;
    [ObservableProperty] private decimal _finalRuns = 300;

    [ObservableProperty] private int _searchMethodIndex;
    public IReadOnlyList<string> SearchMethodOptions { get; } = new[] { "Simulated annealing", "Genetic algorithm" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _canRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;

    [ObservableProperty] private string _modelHint = string.Empty;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private IBrush _summaryBrush = Brushes.Gray;
    [ObservableProperty] private bool _canApply;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool NotRunning => !IsRunning;
    public bool AnyLever => LeverOrdering || LeverMaintenance || LeverLeave
                            || LeverCrew || LeverStartDates || LeverExtraWorkdays
                            || LeverAddPilots || LeverAddEngineers || LeverAddAircraft || LeverAddSimulators;

    public ObservableCollection<FrontierRow> Frontier { get; } = new();
    [ObservableProperty] private bool _hasFrontier;
    public bool CanStart => CanRun && !IsRunning && AnyLever;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public ObservableCollection<OptimizerChangeRow> Changes { get; } = new();

    public override void OnNavigatedTo()
    {
        if (Active.RunSnapshot is null || (_result is not null && !ReferenceEquals(_optimizedFrom, Active.RunSnapshot)))
        {
            HasResult = false; CanApply = false; Changes.Clear(); _result = null; ErrorMessage = null;
            Frontier.Clear(); HasFrontier = false; AppliedMessage = null;
        }
        RefreshState();
    }

    private Scenario? _optimizedFrom;

    private void RefreshState()
    {
        var sc = Active;
        if (sc.RunSnapshot is not { } snap)
        {
            CanRun = false;
            ModelHint = $"Run \"{sc.Name}\" with probabilistic weather on the Planning page first.";
            return;
        }
        bool prob = snap.FlyableModel is ProbabilisticFlyableDays;
        CanRun = prob;
        ModelHint = prob
            ? $"{sc.Name} · probabilistic weather"
            : $"Re-run \"{sc.Name}\" with probabilistic weather on the Planning page.";
    }

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning || Active.RunSnapshot is not { } snap) return;
        if (snap.FlyableModel is not ProbabilisticFlyableDays)
        {
            ErrorMessage = "This scenario's flyable model is not probabilistic.";
            return;
        }

        ErrorMessage = null;
        IsRunning = true;
        HasResult = false;
        CanApply = false;

        var opts = new Optimizer.Options
        {
            Levers = BuildLevers(),
            Iterations = Math.Max(1, (int)Iterations),
            SearchRuns = Math.Max(1, (int)SearchRuns),
            FinalRuns = Math.Max(1, (int)FinalRuns),
            MaxShiftDays = Math.Max(0, (int)MaxShiftDays),
            MaxStartDelayDays = Math.Max(0, (int)MaxStartDelayDays),
            MaxExtraWorkdays = Math.Max(0, (int)MaxExtraWorkdays),
            MaxAddedPilots = Math.Max(0, (int)MaxAddedPilots),
            MaxAddedEngineers = Math.Max(0, (int)MaxAddedEngineers),
            MaxAddedAircraft = Math.Max(0, (int)MaxAddedAircraft),
            MaxAddedSimulators = Math.Max(0, (int)MaxAddedSimulators),
            Search = (Optimizer.Method)SearchMethodIndex,
            Seed = FixedSeed,
        };

        var dialog = new ProgressDialogViewModel { Title = "Plan optimization" };
        var progress = new Progress<Optimizer.Progress>(p => dialog.Report(p.Phase, p.Fraction));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        try
        {
            var result = await Task.Run(() => Optimizer.Optimize(snap, opts, progress, dialog.Token));
            _optimizedFrom = snap;
            Populate(result);
            HasResult = true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Optimization cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Optimization failed: {ex.Message}";
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
            IsRunning = false;
        }
    }

    private Optimizer.Levers BuildLevers()
    {
        var l = Optimizer.Levers.None;
        if (LeverOrdering) l |= Optimizer.Levers.Ordering;
        if (LeverMaintenance) l |= Optimizer.Levers.Maintenance;
        if (LeverLeave) l |= Optimizer.Levers.Leave;
        if (LeverCrew) l |= Optimizer.Levers.CrewAssignment;
        if (LeverStartDates) l |= Optimizer.Levers.StartDates;
        if (LeverExtraWorkdays) l |= Optimizer.Levers.ExtraWorkdays;
        if (LeverAddPilots) l |= Optimizer.Levers.AddPilots;
        if (LeverAddEngineers) l |= Optimizer.Levers.AddEngineers;
        if (LeverAddAircraft) l |= Optimizer.Levers.AddAircraft;
        if (LeverAddSimulators) l |= Optimizer.Levers.AddSimulators;
        return l;
    }

    private void Populate(Optimizer.Result r)
    {
        _result = r;
        AppliedMessage = null;
        Changes.Clear();
        foreach (var c in r.Changes)
        {
            var row = new OptimizerChangeRow(c);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OptimizerChangeRow.IsSelected)) UpdateCanApply();
            };
            Changes.Add(row);
        }

        Frontier.Clear();
        foreach (var p in r.Frontier)
            Frontier.Add(new FrontierRow
            {
                Additions = p.Additions.ToString(),
                What = p.Description,
                Score = p.Score.ToString("0.##"),
                PDelivery = p.PDelivery.ToString("P0"),
            });
        HasFrontier = Frontier.Count > 1;

        Summary = r.Improved
            ? $"Improved: Score {r.BaselineScore:0.##} → {r.OptimizedScore:0.##} · " +
              $"P(delivery) {r.BaselinePDelivery:P0} → {r.OptimizedPDelivery:P0} ({r.Changes.Count} change(s), {r.Runs} runs)."
            : $"No improvement found within this search budget " +
              $"(Score {r.BaselineScore:0.##} · P(delivery) {r.BaselinePDelivery:P0}).";
        SummaryBrush = r.Improved ? Good : Marginal;
        UpdateCanApply();
    }

    private void UpdateCanApply() =>
        CanApply = _result is { Improved: true } && Changes.Any(c => c.IsSelected);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplied))]
    private string? _appliedMessage;
    public bool HasApplied => !string.IsNullOrWhiteSpace(AppliedMessage);

    [RelayCommand]
    private void Apply()
    {
        if (_result is not { Improved: true }) return;
        var selected = Changes.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;

        var source = Active;
        string name = UniqueScenarioName($"{source.Name} · optimized");
        var copy = _repo.AddScenarioCopy(name);
        copy.ParentScenarioId = source.Id;
        foreach (var row in selected) row.Source.Apply(copy);
        _repo.SetActiveScenario(source.Id);
        _repo.SaveSession();
        _scenarios.NotifyScenariosChanged();

        AppliedMessage = $"Created \"{name}\" with {selected.Count} changes. Run it on the Planning page.";
    }

    private string UniqueScenarioName(string baseName)
    {
        var existing = _repo.GetScenarios().Select(s => s.Name).ToHashSet();
        if (!existing.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
            if (!existing.Contains($"{baseName} ({n})"))
                return $"{baseName} ({n})";
    }

    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Marginal = new SolidColorBrush(Color.Parse("#FFBF1F"));
}

public sealed partial class OptimizerChangeRow : ObservableObject
{
    public OptimizerChangeRow(Optimizer.Change source) => Source = source;

    public Optimizer.Change Source { get; }
    public string Kind => Source.Kind;
    public string Description => Source.Description;

    [ObservableProperty] private bool _isSelected = true;
}

public sealed class FrontierRow
{
    public required string Additions { get; init; }
    public required string What { get; init; }
    public required string Score { get; init; }
    public required string PDelivery { get; init; }
}
