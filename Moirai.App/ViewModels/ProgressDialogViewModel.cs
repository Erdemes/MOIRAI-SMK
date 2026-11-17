using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MOIRAI.ViewModels;

public partial class ProgressDialogViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public event Action? CloseRequested;

    [ObservableProperty] private string _title = "Working…";
    [ObservableProperty] private string _detail = "Starting…";
    [ObservableProperty] private string _percentText = string.Empty;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _isIndeterminate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    private bool _isCancelling;

    public string CancelLabel => IsCancelling ? "Cancelling…" : "Cancel";

    public void Report(int done, int total)
    {
        IsIndeterminate = false;
        Fraction = total <= 0 ? 0 : (double)done / total;
        Detail = $"{done:N0} / {total:N0} runs";
        PercentText = Fraction.ToString("P0");
    }

    public void Report(string detail, double fraction)
    {
        IsIndeterminate = false;
        Fraction = Math.Clamp(fraction, 0, 1);
        Detail = detail;
        PercentText = Fraction.ToString("P0");
    }

    [RelayCommand]
    private void Cancel()
    {
        IsCancelling = true;
        _cts.Cancel();
    }

    public void RequestClose() => CloseRequested?.Invoke();
}
