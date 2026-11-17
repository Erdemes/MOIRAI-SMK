using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public partial class LocationsViewModel : ViewModelBase
{
    private readonly IRepository _repo;
    private readonly IDialogService _dialogs;

    public ObservableCollection<Location> Items { get; } = new();

    public ObservableCollection<Campaign> AssignedCampaigns { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Location? _selectedItem;

    [ObservableProperty] private string _editName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    [NotifyPropertyChangedFor(nameof(HasCoordinates))]
    private double? _editLatitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    [NotifyPropertyChangedFor(nameof(HasCoordinates))]
    private double? _editLongitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    private string? _editIcao;

    public bool HasCoordinates => EditLatitude is not null && EditLongitude is not null;
    public string CoordinatesText => HasCoordinates
        ? (string.IsNullOrEmpty(EditIcao) ? "" : $"{EditIcao} · ") +
          $"{Math.Abs(EditLatitude!.Value):0.0000}° {(EditLatitude.Value >= 0 ? "N" : "S")},  " +
          $"{Math.Abs(EditLongitude!.Value):0.0000}° {(EditLongitude.Value >= 0 ? "E" : "W")}"
        : "Not set";

    [ObservableProperty] private decimal _editBlocksCap;
    [ObservableProperty] private decimal _editFerryDays = 1;
    [ObservableProperty] private decimal _editFerryFlightHours = 2;
    [ObservableProperty] private decimal _editFerryDutyHours = 6;
    [ObservableProperty] private decimal _editSetupDays = 2;
    [ObservableProperty] private decimal _editTeardownDays = 1;
    [ObservableProperty] private decimal _editPostDeploymentRestDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverrideWeather))]
    private bool _editUseScenarioWeather;
    public bool OverrideWeather => !EditUseScenarioWeather;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeatherUniform))]
    [NotifyPropertyChangedFor(nameof(IsWeatherSeasonal))]
    private int _editWeatherModelIndex;
    public bool IsWeatherUniform => EditWeatherModelIndex == 0;
    public bool IsWeatherSeasonal => EditWeatherModelIndex == 1;

    [ObservableProperty] private decimal _editWeatherRate = 0.7m;
    [ObservableProperty] private decimal _editWeatherPersistence = 0.40m;

    public ObservableCollection<MonthRateOption> WeatherMonthlyRates { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRangeRisk))]
    private bool _editRangeAlwaysAvailable = true;
    public bool HasRangeRisk => !EditRangeAlwaysAvailable;
    [ObservableProperty] private decimal _editRangeRate = 0.8m;
    [ObservableProperty] private decimal _editRangePersistence = 0.5m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasSelection => SelectedItem is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasAssignedCampaigns => AssignedCampaigns.Count > 0;

    private static readonly string[] MonthNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private static readonly decimal[] DefaultMonthlyRates =
        { 0.42m, 0.54m, 0.56m, 0.58m, 0.69m, 0.77m, 0.96m, 0.98m, 0.89m, 0.82m, 0.71m, 0.40m };

    public LocationsViewModel(IRepository repo, IDialogService dialogs)
    {
        _repo = repo;
        _dialogs = dialogs;
        for (int i = 0; i < MonthNames.Length; i++)
            WeatherMonthlyRates.Add(new MonthRateOption(MonthNames[i], DefaultMonthlyRates[i]));
    }

    public override void OnNavigatedTo() => Refresh();

    private void Refresh()
    {
        Items.Clear();
        foreach (var l in _repo.GetLocations()) Items.Add(l);
    }

    partial void OnSelectedItemChanged(Location? value)
    {
        ErrorMessage = null;
        EditName = value?.Name ?? string.Empty;
        EditLatitude = value?.Latitude;
        EditLongitude = value?.Longitude;
        EditIcao = value?.Icao;
        EditBlocksCap = value?.BlocksPerDayCap ?? 0;
        EditFerryDays = value?.FerryDays ?? 1;
        EditFerryFlightHours = (decimal)(value?.FerryFlightHours ?? 2);
        EditFerryDutyHours = (decimal)(value?.FerryDutyHours ?? 6);
        EditSetupDays = value?.SetupDays ?? 2;
        EditTeardownDays = value?.TeardownDays ?? 1;
        EditPostDeploymentRestDays = value?.PostDeploymentRestDays ?? 0;

        if (value?.FlyableModel is ProbabilisticFlyableDays p)
        {
            EditUseScenarioWeather = false;
            EditWeatherPersistence = (decimal)p.Persistence;
            var rates = p.MonthlyRates is { Length: 12 } ? p.MonthlyRates : null;
            bool seasonal = rates is not null && rates.Distinct().Count() > 1;
            EditWeatherModelIndex = seasonal ? 1 : 0;
            EditWeatherRate = (decimal)(rates is null ? 0.7 : rates.Average());
            if (rates is not null)
                for (int i = 0; i < 12; i++) WeatherMonthlyRates[i].Rate = (decimal)rates[i];
        }
        else
        {
            ApplySeasonalWeather(value?.Icao);
        }

        if (value?.RangeModel is ProbabilisticFlyableDays r)
        {
            EditRangeAlwaysAvailable = false;
            EditRangeRate = (decimal)(r.MonthlyRates is { Length: 12 } ? r.MonthlyRates.Average() : 0.8);
            EditRangePersistence = (decimal)r.Persistence;
        }
        else
        {
            EditRangeAlwaysAvailable = true;
        }

        AssignedCampaigns.Clear();
        if (value is not null)
            foreach (var c in _repo.GetCampaigns().Where(c => c.LocationId == value.Id))
                AssignedCampaigns.Add(c);
        OnPropertyChanged(nameof(HasAssignedCampaigns));
    }

    [RelayCommand]
    private void New()
    {
        var loc = new Location { Name = $"Location {Items.Count + 1}" };
        _repo.AddLocation(loc);
        Refresh();
        SelectedItem = Items.FirstOrDefault(l => l.Id == loc.Id);
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        if (SelectedItem is not { } loc) return;
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Name is required.";
            return;
        }

        loc.Name = EditName.Trim();
        loc.Latitude = EditLatitude;
        loc.Longitude = EditLongitude;
        loc.Icao = EditIcao;
        loc.BlocksPerDayCap = EditBlocksCap > 0 ? (int)EditBlocksCap : null;
        loc.FerryDays = Math.Max(0, (int)EditFerryDays);
        loc.FerryFlightHours = Math.Max(0, (double)EditFerryFlightHours);
        loc.FerryDutyHours = Math.Max(0, (double)EditFerryDutyHours);
        loc.SetupDays = Math.Max(0, (int)EditSetupDays);
        loc.TeardownDays = Math.Max(0, (int)EditTeardownDays);
        loc.PostDeploymentRestDays = Math.Max(0, (int)EditPostDeploymentRestDays);
        var anchor = _repo.GetScenario().HorizonStart;
        double weatherPhi = Math.Clamp((double)EditWeatherPersistence, 0, 0.98);
        loc.FlyableModel = EditUseScenarioWeather
            ? null
            : IsWeatherSeasonal
                ? new ProbabilisticFlyableDays
                {
                    MonthlyRates = WeatherMonthlyRates.Select(m => Math.Clamp((double)m.Rate, 0, 1)).ToArray(),
                    Persistence = weatherPhi,
                    Anchor = anchor,
                }
                : ProbabilisticFlyableDays.Uniform(
                    Math.Clamp((double)EditWeatherRate, 0, 1), weatherPhi, anchor);
        loc.RangeModel = EditRangeAlwaysAvailable
            ? null
            : ProbabilisticFlyableDays.Uniform(
                Math.Clamp((double)EditRangeRate, 0, 1),
                Math.Clamp((double)EditRangePersistence, 0, 0.98),
                _repo.GetScenario().HorizonStart);
        _repo.UpdateLocation(loc);

        var id = loc.Id;
        Refresh();
        SelectedItem = Items.FirstOrDefault(l => l.Id == id);
    }

    [RelayCommand]
    private async Task PickLocation()
    {
        var result = await _dialogs.PickLocationAsync(EditName, EditLongitude, EditLatitude, EditIcao);
        if (result is not { } r) return;
        EditLongitude = r.Lon;
        EditLatitude = r.Lat;
        EditIcao = r.Icao;
        ApplySeasonalWeather(r.Icao);
    }

    private void ApplySeasonalWeather(string? icao)
    {
        EditUseScenarioWeather = false;
        EditWeatherModelIndex = 1;
        if (Views.AirportWeather.Load().For(icao) is { } w)
        {
            for (int i = 0; i < 12 && i < WeatherMonthlyRates.Count; i++)
                WeatherMonthlyRates[i].Rate = (decimal)w.MonthlyRates[i];
            EditWeatherPersistence = (decimal)w.Persistence;
        }
        else
        {
            for (int i = 0; i < 12 && i < WeatherMonthlyRates.Count; i++)
                WeatherMonthlyRates[i].Rate = DefaultMonthlyRates[i];
            EditWeatherPersistence = 0.40m;
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is not { } loc) return;
        try
        {
            _repo.DeleteLocation(loc.Id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        Refresh();
        SelectedItem = null;
    }
}
