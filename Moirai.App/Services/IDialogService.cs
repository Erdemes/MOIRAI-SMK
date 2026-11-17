using Avalonia.Controls;

namespace MOIRAI.Services;

public readonly record struct LocationPickResult(double? Lon, double? Lat, string? Icao);

public interface IDialogService
{
    void Initialize(Window mainWindow);

    Task<string?> OpenFileAsync(string title, string description, params string[] patterns);

    Task<string?> SaveFileAsync(
        string title, string description, string suggestedName, string defaultExt, params string[] patterns);

    void ShowResultsWindow(object dataContext);

    void ShowGanttWindow(object dataContext);

    void ShowMcGanttWindow(ViewModels.McGanttViewModel viewModel);

    void ShowTestPointsWindow(object dataContext);

    void ShowDecisionLogWindow(ViewModels.DecisionLogViewModel viewModel);

    Task ShowYieldPreviewDialogAsync(ViewModels.YieldPreviewViewModel viewModel);

    Task ShowExecutionDialogAsync(ViewModels.ExecutionViewModel viewModel);

    Task ShowPinEditorDialogAsync(ViewModels.PinEditorViewModel viewModel);

    void ShowComparisonWindow(object dataContext);

    Task ShowNewPlanDialogAsync(Func<string, string?> tryCreate);

    Task ShowProgressDialogAsync(ViewModels.ProgressDialogViewModel viewModel);

    Task<LocationPickResult?> PickLocationAsync(string name, double? lon, double? lat, string? icao);

    void ShowAboutWindow();

    void ShowUpdateWindow(ViewModels.UpdateViewModel viewModel);
}
