using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MOIRAI.Persistence;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly (string Title, string Description, string Pattern) SessionFile =
        ("session", "MOIRAI session", "*.json");

    private readonly IRepository _repo;
    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private readonly AnalysisHandoff _handoff;
    private readonly ScenarioListStore _scenarios;

    private bool _suppressScenarioSelection;

    public ObservableCollection<NavItem> NavItems { get; } = new();

    public ObservableCollection<ScenarioItem> Scenarios => _scenarios.Items;

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private ScenarioItem? _selectedScenario;

    [ObservableProperty] private bool _canCompare;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _sessionName = string.Empty;

    public string WindowTitle =>
        string.IsNullOrWhiteSpace(SessionName) ? "MOIRAI" : $"MOIRAI · {SessionName}";

    [RelayCommand]
    private void OpenUserManual()
    {
        var pdf = Path.Combine(AppContext.BaseDirectory, "MOIRAI-User-Manual.pdf");
        if (!File.Exists(pdf))
        {
            StatusMessage = "User manual not found next to the application.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pdf) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the manual: {ex.Message}";
        }
    }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        var dialog = new ProgressDialogViewModel
        {
            Title = "Checking for updates",
            Detail = "Checking for a new version…",
            IsIndeterminate = true,
        };
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        var checker = new MOIRAI.Services.UpdateChecker();
        MOIRAI.Services.UpdateChecker.Result? result = null;
        try
        {
            result = await checker.CheckAsync(dialog.Token);
        }
        catch { }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
        }

        if (result?.Status == MOIRAI.Services.UpdateChecker.Status.UpdateAvailable)
            _dialogs.ShowUpdateWindow(new UpdateViewModel(checker, result));
    }

    public MainWindowViewModel(IRepository repo, IServiceProvider services, IDialogService dialogs,
        AnalysisHandoff handoff, ScenarioListStore scenarios)
    {
        _repo = repo;
        _services = services;
        _dialogs = dialogs;
        _handoff = handoff;
        _scenarios = scenarios;
        _handoff.AnalysisRequested += GoToAnalysis;
        scenarios.ScenariosChanged += () => RebuildScenarioList();
        _sessionName = _repo.GetSession().Name;
        RebuildScenarioList();
        BuildPages();
        if (repo is JsonRepository { RecoveryMessage: { } message }) StatusMessage = message;
    }

    private PlanViewModel? PlanVm =>
        NavItems.Select(n => n.ViewModel).OfType<PlanViewModel>().FirstOrDefault();

    private void GoToAnalysis()
    {
        if (NavItems.FirstOrDefault(n => n.ViewModel is AnalysisViewModel) is { } analysis)
            SelectedNav = analysis;
    }

    private void BuildPages()
    {
        NavItems.Clear();
        NavItems.Add(NavItem.Header("RESOURCES"));
        NavItems.Add(new("Aircraft", "✈", _services.GetRequiredService<AircraftViewModel>()));
        NavItems.Add(new("Simulators", "🖥", _services.GetRequiredService<SimulatorsViewModel>()));
        NavItems.Add(new("Locations", "⚑", _services.GetRequiredService<LocationsViewModel>()));
        NavItems.Add(new("External Support", "⇄", _services.GetRequiredService<ExternalSupportViewModel>()));
        NavItems.Add(new("Pilots", "✦", _services.GetRequiredService<PilotsViewModel>()));
        NavItems.Add(new("FTEs", "⚙", _services.GetRequiredService<EngineersViewModel>()));
        NavItems.Add(NavItem.Header("CAMPAIGNS"));
        NavItems.Add(new("Campaigns", "◎", _services.GetRequiredService<CampaignsViewModel>()));
        NavItems.Add(NavItem.Header("PLAN & ANALYSE"));
        NavItems.Add(new("Planning", "▣", _services.GetRequiredService<PlanViewModel>()));
        NavItems.Add(new("Analysis", "📊", _services.GetRequiredService<AnalysisViewModel>()));
        NavItems.Add(new("Optimize", "✣", _services.GetRequiredService<OptimizationViewModel>()));
        NavItems.Add(new("Animation", "▶", _services.GetRequiredService<AnimationViewModel>()));
        SelectedNav = NavItems.First(n => !n.IsHeader);
    }

    private void RebuildScenarioList()
    {
        _suppressScenarioSelection = true;
        _scenarios.Rebuild(_repo);
        foreach (var item in Scenarios) Track(item);
        SelectedScenario = Scenarios.FirstOrDefault(s => s.Model.Id == _repo.GetScenario().Id);
        _suppressScenarioSelection = false;
        RecomputeCanCompare();
    }

    private void Track(ScenarioItem item) => item.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(ScenarioItem.IsSelected) or nameof(ScenarioItem.Status))
            RecomputeCanCompare();
        if (e.PropertyName is nameof(ScenarioItem.IsSelected))
            _repo.SaveSession();
    };

    private void RecomputeCanCompare() =>
        CanCompare = Scenarios.Count(s => s.IsSelected && s.HasRun) >= 2;

    partial void OnSelectedScenarioChanged(ScenarioItem? oldValue, ScenarioItem? newValue)
    {
        if (_suppressScenarioSelection || newValue is null) return;

        if (oldValue is not null && oldValue.Model.Id == _repo.GetScenario().Id)
            PlanVm?.StashIntoActive();

        _repo.SetActiveScenario(newValue.Model.Id);
        SelectedNav?.ViewModel?.OnNavigatedTo();
    }

    [RelayCommand]
    private Task NewScenario() => _dialogs.ShowNewPlanDialogAsync(TryCreateScenario);

    private string? TryCreateScenario(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Scenario name is required.";
        if (_repo.GetScenarios().Any(s => s.Name == name)) return "A scenario with this name already exists.";

        PlanVm?.StashIntoActive();
        _repo.AddScenarioCopy(name);
        RebuildScenarioList();
        SelectedNav?.ViewModel?.OnNavigatedTo();
        return null;
    }

    public void CommitRenameScenario(ScenarioItem item)
    {
        var name = (item.EditName ?? string.Empty).Trim();
        if (name.Length == 0 || name == item.Model.Name)
        {
            item.CancelEdit();
            return;
        }
        if (_repo.GetScenarios().Any(s => s.Id != item.Model.Id && s.Name == name))
        {
            StatusMessage = $"A scenario named \"{name}\" already exists.";
            item.CancelEdit();
            return;
        }
        item.ApplyRename(name);
        _repo.SaveSession();
        StatusMessage = $"Renamed scenario to \"{name}\".";
    }

    [RelayCommand]
    private void RemoveScenario(ScenarioItem item)
    {
        _repo.DeleteScenario(item.Model.Id);
        RebuildScenarioList();
        SelectedNav?.ViewModel?.OnNavigatedTo();
    }

    [RelayCommand]
    private void Compare()
    {
        var selected = Scenarios.Where(s => s.IsSelected && s.HasRun).ToList();
        if (ScenarioComparison.Build(selected) is { } comparison)
            _dialogs.ShowComparisonWindow(comparison);
    }

    [RelayCommand]
    private void NewSession()
    {
        _repo.NewSession();
        SessionName = _repo.GetSession().Name;
        RebuildScenarioList();
        BuildPages();
        StatusMessage = "New session created";
    }

    [RelayCommand]
    private async Task OpenSession()
    {
        var path = await _dialogs.OpenFileAsync("Open session", SessionFile.Description, SessionFile.Pattern);
        if (path is null) return;
        try
        {
            _repo.LoadSessionFrom(path);
            SessionName = _repo.GetSession().Name;
            RebuildScenarioList();
            BuildPages();
            StatusMessage = $"Loaded {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load session: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveSessionAs()
    {
        var path = await _dialogs.SaveFileAsync(
            "Save session as", SessionFile.Description, $"{SessionName}.json", "json", SessionFile.Pattern);
        if (path is null) return;
        try
        {
            _repo.SetSessionName(Path.GetFileNameWithoutExtension(path));
            SessionName = _repo.GetSession().Name;
            _repo.SaveSessionTo(path);
            StatusMessage = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save session: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowAbout() => _dialogs.ShowAboutWindow();

    partial void OnSelectedNavChanged(NavItem? value) => value?.ViewModel?.OnNavigatedTo();
}
