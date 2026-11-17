using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Persistence;

namespace MOIRAI.ViewModels;

public partial class AircraftViewModel : ViewModelBase
{
    private readonly IRepository _repo;

    private bool _isLoadingPlanner;

    public ObservableCollection<Aircraft> Items { get; } = new();

    public WindowPlannerViewModel Planner { get; } = new();

    public ObservableCollection<PeriodicMaintenanceRule> PeriodicRules { get; } = new();

    [ObservableProperty] private decimal _editRuleIntervalHours = 100;
    [ObservableProperty] private decimal _editRuleDurationDays = 1;

    public bool HasPeriodicRules => PeriodicRules.Count > 0;

    public ObservableCollection<Campaign> AssignedCampaigns { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Aircraft? _selectedItem;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty] private decimal _editMinPilots = 1;

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
    public bool HasAssignedCampaigns => AssignedCampaigns.Count > 0;

    public AircraftViewModel(IRepository repo)
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
        foreach (var item in _repo.GetAircraft()) Items.Add(item);
    }

    partial void OnSelectedItemChanged(Aircraft? value)
    {
        ErrorMessage = null;
        EditName = value?.Name ?? string.Empty;
        EditMinPilots = Math.Max(1, value?.MinPilots ?? 1);
        EditFailureRate = (decimal)(value?.FailuresPer100FlightHours ?? 0);
        EditRepairDaysMin = value?.RepairDaysMin ?? 1;
        EditRepairDaysMax = value?.RepairDaysMax ?? 3;

        AssignedCampaigns.Clear();
        if (value is not null)
            foreach (var c in _repo.GetCampaigns().Where(c => c.CompatibleAircraftIds.Contains(value.Id)))
                AssignedCampaigns.Add(c);
        OnPropertyChanged(nameof(HasAssignedCampaigns));

        _isLoadingPlanner = true;
        Planner.Load(value?.MaintenanceWindows ?? Enumerable.Empty<DateRange>());
        _isLoadingPlanner = false;

        ReloadPeriodicRules(value);
    }

    private void ReloadPeriodicRules(Aircraft? aircraft)
    {
        PeriodicRules.Clear();
        if (aircraft is not null)
            foreach (var r in aircraft.PeriodicMaintenances.OrderBy(r => r.IntervalFlightHours))
                PeriodicRules.Add(r);
        OnPropertyChanged(nameof(HasPeriodicRules));
    }

    [RelayCommand]
    private void AddPeriodicRule()
    {
        if (SelectedItem is not { } aircraft) return;
        if (EditRuleIntervalHours <= 0 || EditRuleDurationDays <= 0)
        {
            ErrorMessage = "Periodic maintenance needs a positive flight-hour interval and a positive duration.";
            return;
        }
        ErrorMessage = null;

        aircraft.PeriodicMaintenances.Add(new PeriodicMaintenanceRule
        {
            IntervalFlightHours = (double)EditRuleIntervalHours,
            DurationDays = (int)EditRuleDurationDays,
        });
        _repo.UpdateAircraft(aircraft);
        ReloadPeriodicRules(aircraft);
    }

    [RelayCommand]
    private void RemovePeriodicRule(PeriodicMaintenanceRule rule)
    {
        if (SelectedItem is not { } aircraft) return;
        aircraft.PeriodicMaintenances.Remove(rule);
        _repo.UpdateAircraft(aircraft);
        ReloadPeriodicRules(aircraft);
    }

    public string? TryCreate(string tail, string name)
    {
        tail = tail.Trim();
        name = name.Trim();
        if (tail.Length == 0) return "Tail number is required.";
        if (name.Length == 0) return "Name is required.";
        if (Items.Any(a => a.Id == tail)) return "An aircraft with this tail number already exists.";

        _repo.AddAircraft(new Aircraft { Id = tail, Name = name });
        Refresh();
        SelectedItem = Items.FirstOrDefault(a => a.Id == tail);
        RebuildCalendar();
        return null;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        if (SelectedItem is not { } aircraft) return;
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

        aircraft.Name = EditName.Trim();
        aircraft.MinPilots = Math.Max(1, (int)EditMinPilots);
        aircraft.FailuresPer100FlightHours = (double)EditFailureRate;
        aircraft.RepairDaysMin = (int)EditRepairDaysMin;
        aircraft.RepairDaysMax = (int)EditRepairDaysMax;
        _repo.UpdateAircraft(aircraft);

        var id = aircraft.Id;
        Refresh();
        SelectedItem = Items.FirstOrDefault(a => a.Id == id);
        RebuildCalendar();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is not { } aircraft) return;
        try
        {
            _repo.DeleteAircraft(aircraft.Id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        Refresh();
        RebuildCalendar();
    }

    private void OnPlannerChanged()
    {
        if (!_isLoadingPlanner && SelectedItem is { } aircraft)
        {
            aircraft.MaintenanceWindows = Planner.ToList();
            _repo.UpdateAircraft(aircraft);
        }
        RebuildCalendar();
    }

    [RelayCommand]
    private void ApplyWindowChange(WindowChange? change)
    {
        if (change is null) return;
        var aircraft = Items.FirstOrDefault(a => a.Id == change.RowId);
        if (aircraft?.MaintenanceWindows.FirstOrDefault(w => w.Id == change.WindowId) is not { } win) return;

        win.Start = change.NewStart;
        win.End = change.NewEnd;
        _repo.UpdateAircraft(aircraft);

        if (SelectedItem?.Id == aircraft.Id)
        {
            _isLoadingPlanner = true;
            Planner.Load(aircraft.MaintenanceWindows);
            _isLoadingPlanner = false;
        }
        RebuildCalendar();
    }

    private void RebuildCalendar()
    {
        var rows = Items.Select(a => new WindowsCalendarRow
        {
            Id = a.Id,
            Name = a.Name,
            Windows = a.MaintenanceWindows.ToList(),
        }).ToList();

        CalendarData = WindowsCalendarData.Build(
            WindowsCalendarKind.Maintenance, _repo.GetScenario(), rows, SelectedItem?.Id);
    }
}
