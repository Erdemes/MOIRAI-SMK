using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MOIRAI.ViewModels;
using MOIRAI.Views;

namespace MOIRAI.Services;

public sealed class DialogService : IDialogService
{
    private Window? _main;

    public void Initialize(Window mainWindow) => _main = mainWindow;

    private Window Main =>
        _main ?? throw new InvalidOperationException("DialogService used before Initialize.");

    public async Task<string?> OpenFileAsync(string title, string description, params string[] patterns)
    {
        var files = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(description) { Patterns = patterns } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(
        string title, string description, string suggestedName, string defaultExt, params string[] patterns)
    {
        var file = await Main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExt,
            FileTypeChoices = new[] { new FilePickerFileType(description) { Patterns = patterns } },
        });
        return file?.TryGetLocalPath();
    }

    public void ShowResultsWindow(object dataContext) => Show(new ResultsWindow(), dataContext);
    public void ShowGanttWindow(object dataContext) => Show(new GanttWindow(), dataContext);
    public void ShowMcGanttWindow(McGanttViewModel viewModel) => Show(new McGanttWindow(), viewModel);
    public void ShowTestPointsWindow(object dataContext) => Show(new TestPointsWindow(), dataContext);
    public void ShowDecisionLogWindow(DecisionLogViewModel viewModel) => Show(new DecisionLogWindow(), viewModel);

    public Task ShowYieldPreviewDialogAsync(YieldPreviewViewModel viewModel) =>
        new YieldPreviewDialog { DataContext = viewModel }.ShowDialog(Main);

    public Task ShowExecutionDialogAsync(ExecutionViewModel viewModel) =>
        new ExecutionDialog { DataContext = viewModel }.ShowDialog(Main);
    public Task ShowPinEditorDialogAsync(PinEditorViewModel viewModel) =>
        new PinEditorDialog { DataContext = viewModel }.ShowDialog(Main);
    public void ShowComparisonWindow(object dataContext) => Show(new ComparisonWindow(), dataContext);

    public Task ShowNewPlanDialogAsync(Func<string, string?> tryCreate) =>
        new NewPlanDialog { TryCreate = tryCreate }.ShowDialog(Main);

    public Task ShowProgressDialogAsync(ProgressDialogViewModel viewModel) =>
        new ProgressDialog { DataContext = viewModel }.ShowDialog(Main);

    public async Task<LocationPickResult?> PickLocationAsync(string name, double? lon, double? lat, string? icao)
    {
        if (lon is null || lat is null)
        {
            var g = TurkeyMap.Load().Locate(name);
            if (g is { } gg) { lon = gg.Lon; lat = gg.Lat; }
        }
        if (lon is { } lo && lat is { } la && string.IsNullOrEmpty(icao)
            && TurkeyAirports.Load().Nearest(lo, la) is { } near)
        {
            lon = near.Lon; lat = near.Lat; icao = near.Icao;
        }
        var vm = new LocationPickerViewModel(name, lon, lat, icao);
        var ok = await new LocationPickerDialog { DataContext = vm }.ShowDialog<bool>(Main);
        if (!ok) return null;
        return vm.HasPin
            ? new LocationPickResult(vm.PinLon, vm.PinLat, vm.PinIcao)
            : new LocationPickResult(null, null, null);
    }

    public void ShowAboutWindow() =>
        new AboutWindow { DataContext = new AboutViewModel() }.Show(Main);

    public void ShowUpdateWindow(UpdateViewModel viewModel) =>
        new UpdateWindow { DataContext = viewModel }.Show(Main);

    private static void Show(Window window, object dataContext)
    {
        window.DataContext = dataContext;
        window.Show();
    }
}
