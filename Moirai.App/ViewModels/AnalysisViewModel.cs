using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Analysis;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public partial class AnalysisViewModel : ViewModelBase
{
    private readonly IRepository _repo;
    private readonly IDialogService _dialogs;

    public AnalysisViewModel(IRepository repo, IDialogService dialogs)
    {
        _repo = repo;
        _dialogs = dialogs;
    }

    private Scenario Active => _repo.GetScenario();

    [ObservableProperty] private decimal _runs = 50000;

    [ObservableProperty] private bool _mcUseAdvancedEngine;

    private bool _loadingEngineFlag;

    partial void OnMcUseAdvancedEngineChanged(bool value)
    {
        if (_loadingEngineFlag) return;
        var sc = Active;
        sc.Config.McUseAdvancedEngine = value;
        if (sc.RunSnapshot is { } snap) snap.Config.McUseAdvancedEngine = value;
        sc.McResult = null;
        sc.McReplay = null;
        _mcResult = null;
        _analyzedSnapshot = null;
        HasResult = false;
        Risks.Clear();
        _decompositionResults = null;
        _sensitivityResult = null;
        HasDecomposition = false;
        Decomposition.Clear();
        HasSensitivity = false;
        SensitivityChart = null;
        GlobalImportanceChart = null;
        Sweeps.Clear();
        HasExportableResults = false;
        _repo.SaveSession();
    }

    private const int FixedSeed = 12345;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanRecommend))]
    private bool _canRun;

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _modelHint = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;

    [ObservableProperty] private string _pDeliveryText = string.Empty;
    [ObservableProperty] private IBrush _pDeliveryBrush = Brushes.Gray;

    [ObservableProperty] private string _effortText = string.Empty;

    private Scenario? _analyzedSnapshot;

    private MonteCarlo.Result? _mcResult;
    private List<(string Label, MonteCarlo.Result Result)>? _decompositionResults;
    private MonteCarlo.SensitivityResult? _sensitivityResult;

    [ObservableProperty] private bool _hasExportableResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool NotRunning => !IsRunning;
    public bool CanStart => CanRun && !IsRunning;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public ObservableCollection<CampaignRiskRow> Risks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCriticality))]
    private bool _hasRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCriticality))]
    private bool _isCriticalityRunning;

    [ObservableProperty] private bool _hasCriticality;
    [ObservableProperty] private bool _hasCriticalResources;
    [ObservableProperty] private string _criticalitySummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriticalityError))]
    private string? _criticalityError;

    public bool HasCriticalityError => !string.IsNullOrWhiteSpace(CriticalityError);
    public bool CanRunCriticality => HasRun && !IsCriticalityRunning;

    public ObservableCollection<CampaignCriticalityRow> Criticality { get; } = new();
    public ObservableCollection<ResourceCriticalityRow> CriticalResources { get; } = new();

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverAddPilot = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverAddEngineer = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverAddAircraft = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverAddSimulator = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverReassign = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverDailyLimits = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverTestPoints = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverImproveRetest = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanRecommend))] private bool _leverReduceAborts = true;

    [ObservableProperty] private decimal _targetConfidence = 90;
    [ObservableProperty] private decimal _maxSteps = 4;
    [ObservableProperty] private decimal _sweepRuns = 150;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRecommending))]
    [NotifyPropertyChangedFor(nameof(CanRecommend))]
    private bool _isRecommending;

    [ObservableProperty] private bool _hasRecommendation;
    [ObservableProperty] private string _recommendationSummary = string.Empty;
    [ObservableProperty] private IBrush _recommendationSummaryBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendError))]
    private string? _recommendError;

    public bool NotRecommending => !IsRecommending;
    public bool HasRecommendError => !string.IsNullOrWhiteSpace(RecommendError);
    public bool AnyLever => LeverAddPilot || LeverAddEngineer || LeverAddAircraft || LeverAddSimulator
                            || LeverReassign || LeverDailyLimits || LeverTestPoints
                            || LeverImproveRetest || LeverReduceAborts;
    public bool CanRecommend => CanRun && !IsRecommending && AnyLever;

    public ObservableCollection<RecommendStepRow> Steps { get; } = new();

    public override void OnNavigatedTo()
    {
        var snap = Active.RunSnapshot;
        if (!ReferenceEquals(snap, _analyzedSnapshot))
        {
            HasRecommendation = false;
            Steps.Clear();
            RecommendError = null;
            HasDecomposition = false;
            Decomposition.Clear();
            HasSensitivity = false;
            SensitivityChart = null;
            GlobalImportanceChart = null;
            Sweeps.Clear();
            SensitivityError = null;
            ErrorMessage = null;
            _decompositionResults = null;
            _sensitivityResult = null;
            HasCriticality = false;
            Criticality.Clear();
            CriticalResources.Clear();
            CriticalityError = null;

            if (snap is not null && Active.McResult is { } stored)
            {
                _analyzedSnapshot = snap;
                Populate(stored);
                HasResult = true;
            }
            else
            {
                _analyzedSnapshot = null;
                HasResult = false;
                Risks.Clear();
                _mcResult = null;
                HasExportableResults = false;
            }
        }
        _loadingEngineFlag = true;
        McUseAdvancedEngine = Active.Config.McUseAdvancedEngine;
        _loadingEngineFlag = false;
        RefreshState();
    }

    private void RefreshState()
    {
        var scenario = Active;
        HasRun = scenario.RunSnapshot is not null;
        if (scenario.RunSnapshot is not { } sc)
        {
            CanRun = false;
            ModelHint = $"Run \"{scenario.Name}\" with probabilistic weather on the Planning page first.";
            return;
        }
        if (sc.FlyableModel is ProbabilisticFlyableDays)
        {
            CanRun = true;
            int effOn = sc.Campaigns.Count(c => c.TestEfficiencyEnabled);
            double phi = (sc.FlyableModel as ProbabilisticFlyableDays)?.Persistence ?? 0;
            var lines = new List<string>
            {
                scenario.Name,
                $"{sc.HorizonStart:yyyy-MM-dd} to {sc.HorizonEnd:yyyy-MM-dd}",
                $"Weather persistence: {phi:0.##}",
                $"{sc.Campaigns.Count} campaigns",
                effOn > 0
                    ? $"Test efficiency: {effOn}/{sc.Campaigns.Count} campaigns"
                    : "Test efficiency off",
            };
            if (sc.Config.OverrideSortieAbort)
                lines.Add($"Abort override {sc.Config.SortieAbortOverride:0.##}");
            int extProb = sc.ExternalSupports.Count(a => a.AvailabilityModel is ProbabilisticFlyableDays);
            if (extProb > 0)
                lines.Add($"Booking risk: {extProb} external assets");
            ModelHint = string.Join(" · ", lines);
        }
        else
        {
            CanRun = false;
            ModelHint = $"Re-run \"{scenario.Name}\" with probabilistic weather on the Planning page.";
        }
    }

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning || Active.RunSnapshot is not { } sc) return;
        if (sc.FlyableModel is not ProbabilisticFlyableDays)
        {
            ErrorMessage = "This scenario's flyable model is not probabilistic.";
            return;
        }

        ErrorMessage = null;
        IsRunning = true;
        HasResult = false;

        sc.Config.McUseAdvancedEngine = McUseAdvancedEngine;

        int runs = Math.Max(1, (int)Runs);
        var dialog = new ProgressDialogViewModel { Title = "Monte Carlo simulation" };
        var progress = new Progress<int>(done => dialog.Report(done, runs));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        try
        {
            var result = await Task.Run(
                () => MonteCarlo.Analyze(sc, runs, FixedSeed, progress, dialog.Token));
            Populate(result);
            HasResult = true;
            _analyzedSnapshot = sc;

            Active.McResult = result;
            Active.McReplay = new McReplayInfo
            {
                Seed = FixedSeed,
                FinishP10Run = result.FinishP10Run,
                FinishP50Run = result.FinishP50Run,
                FinishP90Run = result.FinishP90Run,
            };
            _repo.SaveSession();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Monte Carlo cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Monte Carlo failed: {ex.Message}";
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunCriticality()
    {
        if (IsCriticalityRunning || Active.RunSnapshot is not { } sc) return;
        CriticalityError = null;
        IsCriticalityRunning = true;

        const string detail = "Perturbing the plan…";
        var dialog = new ProgressDialogViewModel { Title = "Criticality & float", Detail = detail };
        var progress = new Progress<double>(f => dialog.Report(detail, f));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        bool useSearch = sc.Config.UseRevisingEngine;
        Func<Scenario, Schedule> evaluate = s =>
            new RevisingScheduler(EngineOptions.Advanced(useSearch, searchParallelism: 1)).Run(s);

        try
        {
            var result = await Task.Run(
                () => CriticalityAnalysis.Analyze(sc, evaluate, maxFloatDays: 60, progress, dialog.Token));
            PopulateCriticality(result, EngineOptions.Label(useSearch));
            HasCriticality = true;
        }
        catch (OperationCanceledException)
        {
            CriticalityError = "Criticality analysis cancelled.";
        }
        catch (Exception ex)
        {
            CriticalityError = $"Criticality analysis failed: {ex.Message}";
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
            IsCriticalityRunning = false;
        }
    }

    private void PopulateCriticality(CriticalityAnalysis.Result r, string engineLabel)
    {
        Criticality.Clear();
        foreach (var c in r.Campaigns
                     .OrderByDescending(x => x.IsCritical || !x.Completed)
                     .ThenBy(x => x.FloatDays ?? int.MaxValue)
                     .ThenBy(x => x.CampaignName, StringComparer.Ordinal))
        {
            string floatText = !c.Completed ? "-" : c.AtCap ? $"≥{c.FloatDays}" : $"{c.FloatDays}";
            string status = !c.Completed ? "Incomplete"
                : c.IsCritical ? "Critical"
                : c.AtCap ? "Ample margin"
                : $"{c.FloatDays} day(s) margin";
            IBrush brush = !c.Completed || c.IsCritical ? Poor
                : c.AtCap || c.FloatDays > 5 ? Good
                : Marginal;
            Criticality.Add(new CampaignCriticalityRow
            {
                Name = c.CampaignName,
                Finish = c.Finish is { } f ? f.ToString("yyyy-MM-dd") : "-",
                Float = floatText,
                Status = status,
                Brush = brush,
            });
        }

        CriticalResources.Clear();
        foreach (var res in r.Resources
                     .Where(x => x.IsCritical)
                     .OrderByDescending(x => x.BreaksPlan)
                     .ThenByDescending(x => x.NewlyIncomplete)
                     .ThenByDescending(x => x.DeltaFinishDays)
                     .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            string impact = res.BreaksPlan
                ? $"removing → {res.NewlyIncomplete} campaign(s) incomplete"
                : $"removing → +{res.DeltaFinishDays} day(s) to finish";
            CriticalResources.Add(new ResourceCriticalityRow
            {
                Name = res.Name,
                Kind = res.Kind,
                Impact = impact,
                Brush = res.BreaksPlan ? Poor : Marginal,
            });
        }
        HasCriticalResources = CriticalResources.Count > 0;

        int nCrit = r.Campaigns.Count(c => c.IsCritical && c.Completed);
        int nIncomplete = r.Campaigns.Count(c => !c.Completed);
        int nLoad = r.Resources.Count(x => x.IsCritical);
        string finish = r.BaselineFinish is { } bf ? bf.ToString("yyyy-MM-dd") : "-";
        string slack = r.BaselineSlackDays is { } s ? (s >= 0 ? $", {s}d margin" : $", {-s}d overrun") : "";
        CriticalitySummary =
            $"Baseline finish {finish}{slack} ({engineLabel}). " +
            $"{nCrit} critical campaign(s)" + (nIncomplete > 0 ? $", {nIncomplete} incomplete" : "") +
            $", {nLoad} load-bearing resource(s). " +
            "Float = days a campaign can slip before the program finish moves.";
    }

    [RelayCommand]
    private async Task ExportExcel()
    {
        if (_analyzedSnapshot is not { } snapshot) return;
        var path = await _dialogs.SaveFileAsync(
            "Export analysis to Excel", "Excel workbook", "analysis.xlsx", "xlsx", "*.xlsx");
        if (path is null) return;
        try
        {
            var mc = _mcResult;
            var decomposition = _decompositionResults;
            var sensitivity = _sensitivityResult;
            int seed = Active.McReplay?.Seed ?? FixedSeed;
            await Task.Run(() =>
            {
                var percentiles = mc is null
                    ? null
                    : MonteCarlo.ReplayFinishPercentiles(snapshot, mc, seed);
                ExcelExporter.ExportAnalysis(snapshot, mc, decomposition, sensitivity, path, percentiles);
            });
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not export to Excel: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportPdf()
    {
        var active = Active;
        if (active.RunSnapshot is not { } snapshot || active.RunSchedule is not { } schedule) return;
        var path = await _dialogs.SaveFileAsync(
            "Export PDF report", "PDF document", "report.pdf", "pdf", "*.pdf");
        if (path is null) return;
        try
        {
            PdfReporter.Export(snapshot, schedule, path, _mcResult, _decompositionResults, _sensitivityResult);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not export the PDF report: {ex.Message}";
        }
    }

    private void Populate(MonteCarlo.Result result)
    {
        _mcResult = result;
        HasExportableResults = true;
        var snapshot = Active.RunSnapshot;
        var targets = snapshot?.Campaigns
            .ToDictionary(c => c.Id, c => c.TargetEndDate) ?? new();

        Risks.Clear();

        var master = snapshot?.MasterDeadline;
        bool masterP50Late = master is { } md0 && result.FinishP50 is { } mp50 && mp50 > md0;
        Risks.Add(new CampaignRiskRow
        {
            Name = master is { } ? "Program (master deadline)" : "Program (all campaigns)",
            Target = master?.ToString("yyyy-MM-dd") ?? "-",
            PMeet = result.PDelivery,
            PMeetText = result.PDelivery.ToString("P0"),
            Bar = BarBrush(result.PDelivery),
            Verdict = Verdict(result.PDelivery),
            P10 = Day(result.FinishP10),
            P50 = Day(result.FinishP50),
            P50Brush = masterP50Late ? Poor : Neutral,
            P90 = Day(result.FinishP90),
            Incomplete = result.PProgramIncomplete.ToString("P0"),
            Sorties = $"{result.SortiesP10} / {result.SortiesP50} / {result.SortiesP90}",
            Aborted = Aborted(result.AbortedSortiesMean),
            P10Run = result.FinishP10Run,
            P50Run = result.FinishP50Run,
            P90Run = result.FinishP90Run,
        });

        foreach (var c in result.Campaigns)
        {
            var target = targets.GetValueOrDefault(c.CampaignId, DateOnly.MaxValue);
            bool p50Late = c.P50.HasValue && target != DateOnly.MaxValue && c.P50.Value > target;
            Risks.Add(new CampaignRiskRow
            {
                Name = c.CampaignName,
                Target = target == DateOnly.MaxValue ? "-" : target.ToString("yyyy-MM-dd"),
                PMeet = c.PMeet,
                PMeetText = c.PMeet.ToString("P0"),
                Bar = BarBrush(c.PMeet),
                Verdict = Verdict(c.PMeet),
                P10 = Day(c.P10),
                P50 = Day(c.P50),
                P50Brush = p50Late ? Poor : Neutral,
                P90 = Day(c.P90),
                Incomplete = c.PIncomplete.ToString("P0"),
                Sorties = $"{c.SortiesP10} / {c.SortiesP50} / {c.SortiesP90}",
                Aborted = Aborted(c.AbortedMean),
                P10Run = c.P10Run,
                P50Run = c.P50Run,
                P90Run = c.P90Run,
            });
        }

        PDeliveryText = snapshot?.MasterDeadline is { } md
            ? $"P(master deadline {md:yyyy-MM-dd} met) = {result.PDelivery:P0}"
            : $"P(all campaigns complete) = {result.PDelivery:P0}";
        PDeliveryBrush = BarBrush(result.PDelivery);

        int rawPlan = snapshot?.Campaigns.Sum(c => c.RequiredSorties()) ?? 0;
        EffortText = $"Effort: sorties P50 {result.SortiesP50} · P90 {result.SortiesP90} · " +
                     $"mean {result.SortiesMean:0.#} (raw plan {rawPlan})" +
                     (result.AbortedSortiesMean > 0
                         ? $" · avg {result.AbortedSortiesMean:0.#} aborted"
                         : "");

        int likely = result.Campaigns.Count(c => c.PMeet >= 0.8);
        int below = result.Campaigns.Count - likely;
        Summary = $"Scenario \"{Active.Name}\" · {result.Runs} runs · " +
                  $"{likely}/{result.Campaigns.Count} campaign(s) likely on milestone (≥80%)" +
                  (below > 0 ? $" · {below} at risk or unlikely" : "");
    }

    [RelayCommand]
    private async Task Recommend()
    {
        if (IsRecommending || Active.RunSnapshot is not { } sc) return;
        if (sc.FlyableModel is not ProbabilisticFlyableDays)
        {
            RecommendError = "This scenario's flyable model is not probabilistic.";
            return;
        }
        var levers = BuildLevers();
        if (levers == ResourceSuggester.RecommenderLevers.None)
        {
            RecommendError = "Enable at least one lever.";
            return;
        }

        RecommendError = null;
        IsRecommending = true;
        HasRecommendation = false;

        int runs = Math.Max(1, (int)SweepRuns);
        int maxSteps = Math.Max(1, (int)MaxSteps);
        double target = Math.Clamp((double)TargetConfidence / 100.0, 0.01, 1.0);

        var dialog = new ProgressDialogViewModel { Title = "Recommendation search" };
        var progress = new Progress<ResourceSuggester.SweepProgress>(
            p => dialog.Report(p.Phase, p.Fraction));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        try
        {
            var result = await Task.Run(() =>
                ResourceSuggester.Recommend(sc, levers, runs, target, maxSteps, FixedSeed,
                    progress, dialog.Token));
            PopulateRecommendation(result);
            HasRecommendation = true;
            _analyzedSnapshot ??= sc;
        }
        catch (OperationCanceledException)
        {
            RecommendError = "Recommendation search cancelled.";
        }
        catch (Exception ex)
        {
            RecommendError = $"Recommendation failed: {ex.Message}";
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
            IsRecommending = false;
        }
    }

    private ResourceSuggester.RecommenderLevers BuildLevers()
    {
        var levers = ResourceSuggester.RecommenderLevers.None;
        if (LeverAddPilot) levers |= ResourceSuggester.RecommenderLevers.AddPilot;
        if (LeverAddEngineer) levers |= ResourceSuggester.RecommenderLevers.AddEngineer;
        if (LeverAddAircraft) levers |= ResourceSuggester.RecommenderLevers.AddAircraft;
        if (LeverAddSimulator) levers |= ResourceSuggester.RecommenderLevers.AddSimulator;
        if (LeverReassign)
            levers |= ResourceSuggester.RecommenderLevers.ReassignPilot
                    | ResourceSuggester.RecommenderLevers.ReassignEngineer;
        if (LeverDailyLimits)
            levers |= ResourceSuggester.RecommenderLevers.RaiseDailySorties
                    | ResourceSuggester.RecommenderLevers.AddBlock;
        if (LeverTestPoints) levers |= ResourceSuggester.RecommenderLevers.RaiseTestPoints;
        if (LeverImproveRetest) levers |= ResourceSuggester.RecommenderLevers.ImproveRetest;
        if (LeverReduceAborts) levers |= ResourceSuggester.RecommenderLevers.ReduceAborts;
        return levers;
    }

    private void PopulateRecommendation(ResourceSuggester.Result r)
    {
        Steps.Clear();
        for (int i = 0; i < r.Steps.Count; i++)
            Steps.Add(new RecommendStepRow
            {
                Order = $"{i + 1}.",
                Addition = r.Steps[i].Addition,
                Effect = r.Steps[i].Effect,
            });
        RecommendationSummary = r.Summary;
        RecommendationSummaryBrush = r.AllMet ? Good : Marginal;
    }

    [ObservableProperty] private decimal _sensitivityRuns = 2000;
    [ObservableProperty] private decimal _sensitivitySamples = 256;

    [ObservableProperty] private bool _orderSpread;

    [ObservableProperty] private bool _hasSensitivity;
    [ObservableProperty] private string _sensitivitySummary = string.Empty;
    [ObservableProperty] private string _globalImportanceSummary = string.Empty;
    [ObservableProperty] private SensitivityChartData? _sensitivityChart;
    [ObservableProperty] private SobolChartData? _globalImportanceChart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSensitivityError))]
    private string? _sensitivityError;

    public bool HasSensitivityError => !string.IsNullOrWhiteSpace(SensitivityError);

    public ObservableCollection<ParameterSweepRow> Sweeps { get; } = new();

    [RelayCommand]
    private async Task RunSensitivity()
    {
        if (IsRunning || Active.RunSnapshot is not { } sc) return;
        if (sc.FlyableModel is not ProbabilisticFlyableDays)
        {
            SensitivityError = "This scenario's flyable model is not probabilistic.";
            return;
        }

        SensitivityError = null;
        IsRunning = true;
        HasSensitivity = false;

        int runs = Math.Max(1, (int)SensitivityRuns);
        int samples = Math.Max(16, (int)SensitivitySamples);
        var dialog = new ProgressDialogViewModel { Title = "Sensitivity analysis" };
        var progress = new Progress<(string Phase, double Fraction)>(
            p => dialog.Report(p.Phase, p.Fraction));
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        try
        {
            bool spread = OrderSpread;
            var result = await Task.Run(
                () => MonteCarlo.Sensitivity(sc, runs, FixedSeed, progress, dialog.Token, samples, spread));
            PopulateSensitivity(result);
            HasSensitivity = true;
            _analyzedSnapshot ??= sc;
        }
        catch (OperationCanceledException)
        {
            SensitivityError = "Sensitivity analysis cancelled.";
        }
        catch (Exception ex)
        {
            SensitivityError = $"Sensitivity analysis failed: {ex.Message}";
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
            IsRunning = false;
        }
    }

    private void PopulateSensitivity(MonteCarlo.SensitivityResult r)
    {
        _sensitivityResult = r;
        HasExportableResults = true;

        SensitivityChart = new SensitivityChartData
        {
            Baseline = r.BaselinePDelivery,
            Rows = r.Factors.Select(f => new TornadoRow
            {
                Name = f.Name,
                LowP = f.LowPDelivery,
                HighP = f.HighPDelivery,
                LowLabel = f.LowLabel,
                HighLabel = f.HighLabel,
                HasBand = f.HasBand,
                LowLo = f.LowPDeliveryLo, LowHi = f.LowPDeliveryHi,
                HighLo = f.HighPDeliveryLo, HighHi = f.HighPDeliveryHi,
            }).ToList(),
        };

        Sweeps.Clear();
        foreach (var sw in r.Sweeps)
        {
            var group = new ParameterSweepRow { Name = sw.Name };
            foreach (var p in sw.Points)
                group.Points.Add(new SweepPointRow
                {
                    Label = p.Label,
                    PDelivery = p.PDelivery,
                    PText = p.PDelivery.ToString("P0"),
                    Bar = BarBrush(p.PDelivery),
                    FinishP50 = Day(p.FinishP50),
                    HasBand = p.HasBand, PLo = p.PDeliveryLo, PHi = p.PDeliveryHi,
                });
            Sweeps.Add(group);
        }

        var top = r.Factors.FirstOrDefault();
        SensitivitySummary = top is null
            ? $"Baseline P(program) = {r.BaselinePDelivery:P0}. No parameters were swept."
            : $"Most influential parameter: {top.Name} · swings P(program) by {top.Swing:P0} " +
              $"({top.LowLabel} → {top.LowPDelivery:P0}, {top.HighLabel} → {top.HighPDelivery:P0}). " +
              $"Baseline P(program) = {r.BaselinePDelivery:P0}.";

        GlobalImportanceChart = new SobolChartData
        {
            JointInteractionShare = r.JointInteractionShare,
            Rows = r.GlobalImportance.Select(g => new SobolRow
            {
                Name = g.Name,
                DirectShare = g.DirectShare,
                TotalShare = g.TotalShare,
            }).ToList(),
        };

        var biggest = r.GlobalImportance.FirstOrDefault();
        GlobalImportanceSummary = !r.HasGlobalVariation ? "No variation in delivery probability across the sampled settings."
            : biggest is null
            ? string.Empty
            : $"{biggest.Name}: {biggest.DirectShare:P0} direct contribution, " +
              $"{biggest.TotalShare:P0} including interactions. " +
              $"Overall interaction share: {r.JointInteractionShare:P0}.";
    }

    public ObservableCollection<DecompositionRow> Decomposition { get; } = new();

    [ObservableProperty] private bool _hasDecomposition;
    [ObservableProperty] private string _decompositionSummary = string.Empty;

    [RelayCommand]
    private async Task DecomposeRisk()
    {
        if (IsRunning || Active.RunSnapshot is not { } sc) return;
        if (sc.FlyableModel is not ProbabilisticFlyableDays)
        {
            ErrorMessage = "This scenario's flyable model is not probabilistic.";
            return;
        }

        ErrorMessage = null;
        IsRunning = true;

        int runs = Math.Max(1, (int)Runs);

        var modes = new List<(string Label, string Short, MonteCarlo.UncertaintySources Sources)>
        {
            ("Weather only", "weather", MonteCarlo.UncertaintySources.Weather),
            ("Efficiency only", "test efficiency", MonteCarlo.UncertaintySources.Yield),
        };
        if (sc.Locations.Any(l => l.RangeModel is ProbabilisticFlyableDays))
            modes.Add(("Range only", "range availability", MonteCarlo.UncertaintySources.Range));
        if (sc.ExternalSupports.Any(a => a.AvailabilityModel is ProbabilisticFlyableDays))
            modes.Add(("External support only", "external support availability",
                MonteCarlo.UncertaintySources.ExternalSupport));
        if (sc.Aircraft.Any(a => a.FailuresPer100FlightHours > 0)
            || sc.Simulators.Any(s => s.FailuresPer100Hours > 0))
            modes.Add(("Failures only", "unscheduled maintenance", MonteCarlo.UncertaintySources.Failures));
        modes.Add(("Combined", "combined", MonteCarlo.UncertaintySources.All));

        var results = new MonteCarlo.Result[modes.Count];

        var dialog = new ProgressDialogViewModel { Title = "Risk decomposition" };
        var dialogTask = _dialogs.ShowProgressDialogAsync(dialog);

        try
        {
            dialog.Report("Preparing…", 0);
            var frozenOrder = await Task.Run(() => MonteCarlo.FrozenOrder(sc), dialog.Token);

            for (int i = 0; i < modes.Count; i++)
            {
                int idx = i;
                var progress = new Progress<int>(done =>
                    dialog.Report($"{modes[idx].Label} ({idx + 1}/{modes.Count})",
                        (idx + (double)done / runs) / modes.Count));
                results[i] = await Task.Run(() =>
                    MonteCarlo.Analyze(sc, runs, FixedSeed, progress, dialog.Token, modes[idx].Sources, frozenOrder));
            }

            _decompositionResults = modes.Select((m, i) => (m.Label, results[i])).ToList();
            HasExportableResults = true;

            Decomposition.Clear();
            for (int i = 0; i < modes.Count; i++)
                Decomposition.Add(new DecompositionRow
                {
                    Source = modes[i].Label,
                    PDelivery = results[i].PDelivery.ToString("P0"),
                    PDeliveryBrush = BarBrush(results[i].PDelivery),
                    FinishP10 = Day(results[i].FinishP10),
                    FinishP50 = Day(results[i].FinishP50),
                    FinishP90 = Day(results[i].FinishP90),
                    Range = RangeDays(results[i]),
                    Sorties = $"{results[i].SortiesP50} / {results[i].SortiesP90}",
                });

            var singles = modes.Take(modes.Count - 1)
                .Select((m, i) => (m.Short, Result: results[i]))
                .ToList();
            var combined = results[^1];

            string dominant = singles.MinBy(s => s.Result.PDelivery).Short;
            var spreads = singles
                .Select(s => (s.Short, Days: RangeOf(s.Result)))
                .Where(s => s.Days is not null)
                .ToList();
            string spread = spreads.Count > 0
                ? " · spread: " + string.Join(" · ", spreads.Select(s => $"{s.Short} ±{s.Days}d")) +
                  $" → {spreads.MaxBy(s => s.Days).Short} widens the outcome most"
                : "";
            DecompositionSummary =
                $"Dominant risk driver: {dominant} · P(program): " +
                string.Join(" · ", singles.Select(s => $"{s.Short}-only {s.Result.PDelivery:P0}")) +
                $" · combined {combined.PDelivery:P0}{spread}";
            HasDecomposition = true;
            _analyzedSnapshot ??= sc;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Risk decomposition cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Risk decomposition failed: {ex.Message}";
        }
        finally
        {
            dialog.RequestClose();
            await dialogTask;
            IsRunning = false;
        }
    }

    private static int? RangeOf(MonteCarlo.Result r) =>
        r.FinishP10 is { } p10 && r.FinishP90 is { } p90 ? p90.DayNumber - p10.DayNumber : null;

    private static string RangeDays(MonteCarlo.Result r) =>
        RangeOf(r) is { } d ? $"{d}d" : "-";

    [RelayCommand] private void OpenP10(CampaignRiskRow row) => OpenPercentileGantt(row, row.P10Run, "P10", row.P10);
    [RelayCommand] private void OpenP50(CampaignRiskRow row) => OpenPercentileGantt(row, row.P50Run, "P50", row.P50);
    [RelayCommand] private void OpenP90(CampaignRiskRow row) => OpenPercentileGantt(row, row.P90Run, "P90", row.P90);

    private void OpenPercentileGantt(CampaignRiskRow row, int runIndex, string label, string date)
    {
        if (runIndex < 0 || _analyzedSnapshot is not { } snapshot) return;
        try
        {
            var (scenario, schedule) = MonteCarlo.ReplayRun(snapshot, runIndex, FixedSeed);
            _dialogs.ShowMcGanttWindow(new McGanttViewModel(
                $"{label} realization · {row.Name} ends {date} (MC run #{runIndex})",
                scenario, schedule, GanttPalette.Legend(scenario).ToList()));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not replay the run: {ex.Message}";
        }
    }

    private static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "-";

    private static string Aborted(double mean) => mean > 0.05 ? mean.ToString("0.#") : "-";

    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Marginal = new SolidColorBrush(Color.Parse("#FFBF1F"));
    private static readonly IBrush Poor = new SolidColorBrush(Color.Parse("#F25C54"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#E6EAF2"));

    private static IBrush BarBrush(double pMeet) =>
        pMeet >= 0.8 ? Good : pMeet >= 0.5 ? Marginal : Poor;

    private static string Verdict(double pMeet) =>
        pMeet >= 0.8 ? "Likely on time" : pMeet >= 0.5 ? "At risk" : "Unlikely";
}

public sealed class CampaignRiskRow
{
    public required string Name { get; init; }
    public string Target { get; init; } = "-";
    public double PMeet { get; init; }
    public string PMeetText { get; init; } = string.Empty;
    public IBrush Bar { get; init; } = Brushes.Gray;
    public string Verdict { get; init; } = string.Empty;
    public string P10 { get; init; } = "-";
    public string P50 { get; init; } = "-";
    public IBrush P50Brush { get; init; } = Brushes.Gray;
    public string P90 { get; init; } = "-";
    public string Incomplete { get; init; } = string.Empty;

    public string Sorties { get; init; } = "-";
    public string Aborted { get; init; } = "-";

    public int P10Run { get; init; } = -1;
    public int P50Run { get; init; } = -1;
    public int P90Run { get; init; } = -1;
    public bool HasP10 => P10Run >= 0;
    public bool HasP50 => P50Run >= 0;
    public bool HasP90 => P90Run >= 0;
}

public sealed class RecommendStepRow
{
    public required string Order { get; init; }
    public required string Addition { get; init; }
    public required string Effect { get; init; }
}

public sealed class CampaignCriticalityRow
{
    public required string Name { get; init; }
    public required string Finish { get; init; }
    public required string Float { get; init; }
    public required string Status { get; init; }
    public IBrush Brush { get; init; } = Brushes.Gray;
}

public sealed class ResourceCriticalityRow
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Impact { get; init; }
    public IBrush Brush { get; init; } = Brushes.Gray;
}

public sealed class DecompositionRow
{
    public required string Source { get; init; }
    public required string PDelivery { get; init; }
    public IBrush PDeliveryBrush { get; init; } = Brushes.Gray;
    public required string FinishP10 { get; init; }
    public required string FinishP50 { get; init; }
    public required string FinishP90 { get; init; }
    public required string Range { get; init; }
    public required string Sorties { get; init; }
}

public sealed class TornadoRow
{
    public required string Name { get; init; }
    public required double LowP { get; init; }
    public required double HighP { get; init; }
    public required string LowLabel { get; init; }
    public required string HighLabel { get; init; }
    public double Swing => Math.Abs(HighP - LowP);

    public double LowLo { get; init; }
    public double LowHi { get; init; }
    public double HighLo { get; init; }
    public double HighHi { get; init; }
    public bool HasBand { get; init; }
}

public sealed class SensitivityChartData
{
    public required double Baseline { get; init; }
    public required List<TornadoRow> Rows { get; init; }
}

public sealed class SobolRow
{
    public required string Name { get; init; }
    public required double DirectShare { get; init; }
    public required double TotalShare { get; init; }
    public double InteractionShare => Math.Max(0, TotalShare - DirectShare);
}

public sealed class SobolChartData
{
    public required List<SobolRow> Rows { get; init; }
    public required double JointInteractionShare { get; init; }
}

public sealed class ParameterSweepRow
{
    public required string Name { get; init; }
    public ObservableCollection<SweepPointRow> Points { get; } = new();
}

public sealed class SweepPointRow
{
    public required string Label { get; init; }
    public double PDelivery { get; init; }
    public required string PText { get; init; }
    public IBrush Bar { get; init; } = Brushes.Gray;
    public required string FinishP50 { get; init; }

    public bool HasBand { get; init; }
    public double PLo { get; init; }
    public double PHi { get; init; }
    public string BandText => HasBand ? $"order band {PLo:P0}-{PHi:P0}" : "";
}
