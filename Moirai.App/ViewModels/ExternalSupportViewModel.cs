using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public partial class ExternalSupportViewModel : ViewModelBase
{
    private readonly IRepository _repo;

    public ObservableCollection<ExternalSupport> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ExternalSupport? _selectedItem;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private decimal _editMaxSortiesPerDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverrideAvailability))]
    private bool _editAlwaysAvailable = true;
    public bool OverrideAvailability => !EditAlwaysAvailable;

    [ObservableProperty] private decimal _editAvailabilityRate = 0.8m;
    [ObservableProperty] private decimal _editAvailabilityPersistence = 0.3m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasSelection => SelectedItem is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string Summary
    {
        get
        {
            int n = Items.Count;
            if (n == 0)
                return "No external support defined.";
            int prob = Items.Count(a => a.AvailabilityModel is ProbabilisticFlyableDays);
            return $"{n} external asset{(n == 1 ? "" : "s")}" +
                   (prob > 0
                       ? $" · {prob} with booking risk"
                       : ", all always available.");
        }
    }

    public ExternalSupportViewModel(IRepository repo) => _repo = repo;

    public override void OnNavigatedTo() => Refresh();

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in _repo.GetExternalSupports()) Items.Add(item);
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnSelectedItemChanged(ExternalSupport? value)
    {
        ErrorMessage = null;
        EditName = value?.Name ?? string.Empty;
        EditMaxSortiesPerDay = value?.MaxSortiesPerDay ?? 0;
        if (value?.AvailabilityModel is ProbabilisticFlyableDays p)
        {
            EditAlwaysAvailable = false;
            EditAvailabilityRate = (decimal)(p.MonthlyRates is { Length: 12 } ? p.MonthlyRates.Average() : 0.8);
            EditAvailabilityPersistence = (decimal)p.Persistence;
        }
        else
        {
            EditAlwaysAvailable = true;
        }
    }

    public string? TryCreate(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Name is required.";
        if (Items.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "An external support asset with this name already exists.";

        var asset = new ExternalSupport { Id = IRepository.NewId(), Name = name };
        _repo.AddExternalSupport(asset);
        Refresh();
        SelectedItem = Items.FirstOrDefault(a => a.Id == asset.Id);
        return null;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        if (SelectedItem is not { } asset) return;
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Name is required.";
            return;
        }

        asset.Name = EditName.Trim();
        asset.MaxSortiesPerDay = Math.Max(0, (int)EditMaxSortiesPerDay);
        var anchor = _repo.GetScenario().HorizonStart;
        asset.AvailabilityModel = EditAlwaysAvailable
            ? null
            : ProbabilisticFlyableDays.Uniform(
                Math.Clamp((double)EditAvailabilityRate, 0, 1),
                Math.Clamp((double)EditAvailabilityPersistence, 0, 0.98),
                anchor);
        _repo.UpdateExternalSupport(asset);

        var id = asset.Id;
        Refresh();
        SelectedItem = Items.FirstOrDefault(a => a.Id == id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is not { } asset) return;
        try
        {
            _repo.DeleteExternalSupport(asset.Id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        Refresh();
    }
}
