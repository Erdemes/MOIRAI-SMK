using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public sealed partial class UpdateViewModel : ViewModelBase
{
    private readonly UpdateChecker _checker;
    private readonly UpdateChecker.Result _result;

    public UpdateViewModel(UpdateChecker checker, UpdateChecker.Result result)
    {
        _checker = checker;
        _result = result;
        Header = $"MOIRAI v{result.LatestVersion} is available";
        SubHeader = $"You have v{result.CurrentVersion}. Review the changes, then update.";
        foreach (var line in result.Changelog) Changelog.Add(line);
        if (Changelog.Count == 0 && !string.IsNullOrWhiteSpace(result.Notes))
            Changelog.Add(result.Notes!);
    }

    public string Header { get; }
    public string SubHeader { get; }
    public ObservableCollection<string> Changelog { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    public bool NotBusy => !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = string.Empty;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task ApplyAndRestart()
    {
        if (IsBusy) return;
        IsBusy = true;
        Progress = 0;
        Status = "Downloading the update…";
        try
        {
            var zip = await _checker.DownloadAsync(new Progress<double>(p => Progress = p));
            Status = "Applying the update · MOIRAI will restart…";
            Updater.ApplyAndRestart(zip);
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
            IsBusy = false;
        }
    }
}
