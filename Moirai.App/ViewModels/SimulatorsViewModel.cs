using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Persistence;

namespace MOIRAI.ViewModels;

public partial class SimulatorsViewModel : ViewModelBase
{
    private readonly IRepository _repo;

    private bool _isLoadingPlanner;

    public ObservableCollection<Simulator> Items { get; } = new();

    public WindowPlannerViewModel Planner { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Simulator? _selectedItem;

    [ObservableProperty]
    private string _editName = string.Empty;

    public ObservableCollection<string> LocationOptions { get; } = new();
    private List<string> _locationIds = new();

    [ObservableProperty]
    private int _editLocationIndex;

    public bool HasLocationOptions => LocationOptions.Count > 1;

    [ObservableProperty]
    private decimal _editMaxBlocksPerDay;

    [ObservableProperty] private decimal _editFailureRate;
    [ObservableProperty] private decimal _editRepairDaysMin = 1;
    [ObservableProperty] private decimal _editRepairDaysMax = 3;

    [ObservableProperty]
    private WindowsCalendarData? _calendarData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasSelection => SelectedItem is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string CapacitySummary
    {
        get
        {
            int n = Items.Count;
            if (n == 0)
                return "No simulators. Rehearsals and simulator campaigns require a device.";
            int blocks = Math.Max(1, _repo.GetScenario().Config.BlocksPerDay);
            int main = Items.Count(s => string.IsNullOrEmpty(s.LocationId));
            int remote = n - main;
            string mainText = $"Main base: {main} simulator{(main == 1 ? "" : "s")} · up to " +
                              $"{main * blocks} rehearsal slots/day before maintenance and device limits.";
            return remote > 0 ? mainText + $" {remote} simulator{(remote == 1 ? "" : "s")} at remote bases." : mainText;
        }
    }

    public SimulatorsViewModel(IRepository repo)
    {
        _repo = repo;
        Planner.Changed += OnPlannerChanged;
    }

    public override void OnNavigatedTo()
    {
        Refresh();
        RebuildCalendar();
    }

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in _repo.GetSimulators()) Items.Add(item);
        OnPropertyChanged(nameof(CapacitySummary));
    }

    private void ReloadLocations(Simulator? sim)
    {
        LocationOptions.Clear();
        _locationIds = new List<string> { string.Empty };
        LocationOptions.Add("Main base");
        foreach (var l in _repo.GetLocations())
        {
            LocationOptions.Add(l.Name);
            _locationIds.Add(l.Id);
        }
        int li = sim is null ? 0 : _locationIds.IndexOf(sim.LocationId);
        EditLocationIndex = li < 0 ? 0 : li;
        OnPropertyChanged(nameof(HasLocationOptions));
    }

    partial void OnSelectedItemChanged(Simulator? value)
    {
        ErrorMessage = null;
        EditName = value?.Name ?? string.Empty;
        EditMaxBlocksPerDay = value?.MaxBlocksPerDay ?? 0;
        EditFailureRate = (decimal)(value?.FailuresPer100Hours ?? 0);
        EditRepairDaysMin = value?.RepairDaysMin ?? 1;
        EditRepairDaysMax = value?.RepairDaysMax ?? 3;
        ReloadLocations(value);

        _isLoadingPlanner = true;
        Planner.Load(value?.MaintenanceWindows ?? Enumerable.Empty<DateRange>());
        _isLoadingPlanner = false;
    }

    public string? TryCreate(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Name is required.";
        if (Items.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "A simulator with this name already exists.";

        var sim = new Simulator { Id = IRepository.NewId(), Name = name };
        _repo.AddSimulator(sim);
        Refresh();
        OnPropertyChanged(nameof(CapacitySummary));
        SelectedItem = Items.FirstOrDefault(s => s.Id == sim.Id);
        RebuildCalendar();
        return null;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        if (SelectedItem is not { } sim) return;
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Name is required.";
            return;
        }
        if (EditFailureRate < 0)
        {
            ErrorMessage = "The failure rate cannot be negative (0 = no random failures).";
            return;
        }
        if (EditRepairDaysMin < 1 || EditRepairDaysMax < EditRepairDaysMin)
        {
            ErrorMessage = "Repair days need min ≥ 1 and max ≥ min.";
            return;
        }

        sim.Name = EditName.Trim();
        sim.LocationId = EditLocationIndex >= 0 && EditLocationIndex < _locationIds.Count
            ? _locationIds[EditLocationIndex]
            : string.Empty;
        sim.MaxBlocksPerDay = Math.Max(0, (int)EditMaxBlocksPerDay);
        sim.FailuresPer100Hours = (double)EditFailureRate;
        sim.RepairDaysMin = (int)EditRepairDaysMin;
        sim.RepairDaysMax = (int)EditRepairDaysMax;
        _repo.UpdateSimulator(sim);

        var id = sim.Id;
        Refresh();
        SelectedItem = Items.FirstOrDefault(s => s.Id == id);
        RebuildCalendar();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is not { } sim) return;
        try
        {
            _repo.DeleteSimulator(sim.Id);
        }
        catch (System.InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        Refresh();
        RebuildCalendar();
    }

    private void OnPlannerChanged()
    {
        if (!_isLoadingPlanner && SelectedItem is { } sim)
        {
            sim.MaintenanceWindows = Planner.ToList();
            _repo.UpdateSimulator(sim);
        }
        RebuildCalendar();
    }

    [RelayCommand]
    private void ApplyWindowChange(WindowChange? change)
    {
        if (change is null) return;
        var sim = Items.FirstOrDefault(s => s.Id == change.RowId);
        if (sim?.MaintenanceWindows.FirstOrDefault(w => w.Id == change.WindowId) is not { } win) return;

        win.Start = change.NewStart;
        win.End = change.NewEnd;
        _repo.UpdateSimulator(sim);

        if (SelectedItem?.Id == sim.Id)
        {
            _isLoadingPlanner = true;
            Planner.Load(sim.MaintenanceWindows);
            _isLoadingPlanner = false;
        }
        RebuildCalendar();
    }

    private void RebuildCalendar()
    {
        var rows = Items.Select(s => new WindowsCalendarRow
        {
            Id = s.Id,
            Name = s.Name,
            Windows = s.MaintenanceWindows.ToList(),
        }).ToList();

        CalendarData = WindowsCalendarData.Build(
            WindowsCalendarKind.Maintenance, _repo.GetScenario(), rows, SelectedItem?.Id);
    }
}
