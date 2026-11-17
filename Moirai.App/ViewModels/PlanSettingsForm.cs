using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MOIRAI.ViewModels;

public partial class PlanSettingsForm : ObservableObject
{
    private static readonly string[] MonthNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private static readonly decimal[] DefaultMonthlyRates =
        { 0.42m, 0.57m, 0.54m, 0.55m, 0.69m, 0.77m, 0.96m, 0.98m, 0.87m, 0.83m, 0.67m, 0.38m };

    private const decimal DefaultPersistence = 0.41m;

    public PlanSettingsForm()
    {
        var (rates, phi) = LtaeDefaults();
        for (int i = 0; i < MonthNames.Length; i++)
            MonthlyRates.Add(new MonthRateOption(MonthNames[i],
                i < rates.Length ? (decimal)rates[i] : DefaultMonthlyRates[i]));
        Persistence = phi;
    }

    private static (double[] Rates, decimal Persistence) LtaeDefaults()
    {
        var w = Views.AirportWeather.Load().For("LTAE");
        return w is not null
            ? (w.MonthlyRates, (decimal)w.Persistence)
            : (DefaultMonthlyRates.Select(d => (double)d).ToArray(), DefaultPersistence);
    }

    [ObservableProperty] private DateTime? _horizonStart;
    [ObservableProperty] private DateTime? _horizonEnd;

    [ObservableProperty] private DateTime? _masterDeadline;

    [RelayCommand] private void SetHorizonPlus1Month() => ExtendHorizon(months: 1);
    [RelayCommand] private void SetHorizonPlus3Months() => ExtendHorizon(months: 3);
    [RelayCommand] private void SetHorizonPlus6Months() => ExtendHorizon(months: 6);
    [RelayCommand] private void SetHorizonPlus1Year() => ExtendHorizon(months: 12);

    private void ExtendHorizon(int months)
    {
        if (HorizonStart is { } start) HorizonEnd = start.AddMonths(months);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRateBased))]
    [NotifyPropertyChangedFor(nameof(IsProbabilistic))]
    private int _flyableModelIndex;

    public bool IsRateBased => FlyableModelIndex == 0;
    public bool IsProbabilistic => FlyableModelIndex == 1;

    [ObservableProperty] private decimal _flyableRate = 0.7m;

    public ObservableCollection<MonthRateOption> MonthlyRates { get; } = new();
    [ObservableProperty] private decimal _persistence = DefaultPersistence;

    [ObservableProperty] private decimal _flyablePatternLength = 30;
    [ObservableProperty] private DateTime? _flyableAnchor;

    [RelayCommand]
    private void ResetWeatherToDefaults()
    {
        var (rates, phi) = LtaeDefaults();
        for (int i = 0; i < 12 && i < MonthlyRates.Count; i++)
            if (i < rates.Length) MonthlyRates[i].Rate = (decimal)rates[i];
        Persistence = phi;
        FlyablePatternLength = 30;
        FlyableModelIndex = 1;
    }

    [ObservableProperty] private bool _weekendMonday;
    [ObservableProperty] private bool _weekendTuesday;
    [ObservableProperty] private bool _weekendWednesday;
    [ObservableProperty] private bool _weekendThursday;
    [ObservableProperty] private bool _weekendFriday;
    [ObservableProperty] private bool _weekendSaturday = true;
    [ObservableProperty] private bool _weekendSunday = true;

    [ObservableProperty] private string _holidays = string.Empty;

    [ObservableProperty] private decimal _blocksPerDay = 3;
    [ObservableProperty] private decimal _loadBalanceWindowDays = 7;
    [ObservableProperty] private decimal _maxSortiesPerDay = 6;
    [ObservableProperty] private decimal _maxRehearsalsPerDay = 1;
    [ObservableProperty] private decimal _pilotsPerSortie = 1;
    [ObservableProperty] private decimal _engineersPerSortie = 2;
    [ObservableProperty] private decimal _maxRehearsalGapDays = 2;
    [ObservableProperty] private bool _rehearsalOnWorkdaysOnly = true;
    [ObservableProperty] private decimal _maxConsecutiveFlyingDays = 6;
    [ObservableProperty] private decimal _minRestHours = 12;

    [ObservableProperty] private decimal _maxFlightHoursPerDay = 6;
    [ObservableProperty] private decimal _maxFlightHoursPer7Days = 30;
    [ObservableProperty] private decimal _maxFlightHoursPer28Days = 100;
    [ObservableProperty] private decimal _maxFlightHoursPerYear = 900;

    [ObservableProperty] private decimal _maxDutyHoursPerDay = 12;
    [ObservableProperty] private decimal _maxDutyHoursPer7Days = 60;
    [ObservableProperty] private decimal _maxDutyHoursPer14Days = 110;
    [ObservableProperty] private decimal _maxDutyHoursPer28Days = 190;

    [ObservableProperty] private decimal _maxFlightDutyHoursPerDay;

    [ObservableProperty] private bool _simulateYieldInNominal;

    [ObservableProperty] private bool _overrideSortieAbort;
    [ObservableProperty] private decimal _sortieAbortOverride = 0.05m;

    [ObservableProperty] private bool _useRevisingEngine;

    public PlanSettings ToSnapshot(HashSet<string> selectedCampaignIds) => new()
    {
        HorizonStart = HorizonStart,
        HorizonEnd = HorizonEnd,
        MasterDeadline = MasterDeadline,
        FlyableModelIndex = FlyableModelIndex,
        FlyableRate = FlyableRate,
        MonthlyRates = MonthlyRates.Select(m => m.Rate).ToArray(),
        Persistence = Persistence,
        FlyablePatternLength = FlyablePatternLength,
        FlyableAnchor = FlyableAnchor,
        WeekendDays = new[]
        {
            WeekendMonday, WeekendTuesday, WeekendWednesday, WeekendThursday,
            WeekendFriday, WeekendSaturday, WeekendSunday,
        },
        Holidays = Holidays,
        BlocksPerDay = BlocksPerDay,
        LoadBalanceWindowDays = LoadBalanceWindowDays,
        MaxSortiesPerDay = MaxSortiesPerDay,
        MaxRehearsalsPerDay = MaxRehearsalsPerDay,
        PilotsPerSortie = PilotsPerSortie,
        EngineersPerSortie = EngineersPerSortie,
        MaxRehearsalGapDays = MaxRehearsalGapDays,
        RehearsalOnWorkdaysOnly = RehearsalOnWorkdaysOnly,
        MaxConsecutiveFlyingDays = MaxConsecutiveFlyingDays,
        MinRestHours = MinRestHours,
        MaxFlightHoursPerDay = MaxFlightHoursPerDay,
        MaxFlightHoursPer7Days = MaxFlightHoursPer7Days,
        MaxFlightHoursPer28Days = MaxFlightHoursPer28Days,
        MaxFlightHoursPerYear = MaxFlightHoursPerYear,
        MaxDutyHoursPerDay = MaxDutyHoursPerDay,
        MaxDutyHoursPer7Days = MaxDutyHoursPer7Days,
        MaxDutyHoursPer14Days = MaxDutyHoursPer14Days,
        MaxDutyHoursPer28Days = MaxDutyHoursPer28Days,
        MaxFlightDutyHoursPerDay = MaxFlightDutyHoursPerDay,
        SimulateYieldInNominal = SimulateYieldInNominal,
        OverrideSortieAbort = OverrideSortieAbort,
        SortieAbortOverride = SortieAbortOverride,
        UseRevisingEngine = UseRevisingEngine,
        SelectedCampaignIds = selectedCampaignIds,
    };

    public void LoadFrom(PlanSettings s)
    {
        HorizonStart = s.HorizonStart;
        HorizonEnd = s.HorizonEnd;
        MasterDeadline = s.MasterDeadline;
        FlyableModelIndex = s.FlyableModelIndex;
        FlyableRate = s.FlyableRate;
        if (s.MonthlyRates is { Length: 12 })
            for (int i = 0; i < 12; i++)
                MonthlyRates[i].Rate = s.MonthlyRates[i];
        Persistence = s.Persistence;
        FlyablePatternLength = s.FlyablePatternLength;
        FlyableAnchor = s.FlyableAnchor;

        if (s.WeekendDays is { Length: 7 })
        {
            WeekendMonday = s.WeekendDays[0];
            WeekendTuesday = s.WeekendDays[1];
            WeekendWednesday = s.WeekendDays[2];
            WeekendThursday = s.WeekendDays[3];
            WeekendFriday = s.WeekendDays[4];
            WeekendSaturday = s.WeekendDays[5];
            WeekendSunday = s.WeekendDays[6];
        }
        Holidays = s.Holidays;

        BlocksPerDay = s.BlocksPerDay;
        LoadBalanceWindowDays = s.LoadBalanceWindowDays;
        MaxSortiesPerDay = s.MaxSortiesPerDay;
        MaxRehearsalsPerDay = s.MaxRehearsalsPerDay;
        PilotsPerSortie = s.PilotsPerSortie;
        EngineersPerSortie = s.EngineersPerSortie;
        MaxRehearsalGapDays = s.MaxRehearsalGapDays;
        RehearsalOnWorkdaysOnly = s.RehearsalOnWorkdaysOnly;
        MaxConsecutiveFlyingDays = s.MaxConsecutiveFlyingDays;
        MinRestHours = s.MinRestHours;
        MaxFlightHoursPerDay = s.MaxFlightHoursPerDay;
        MaxFlightHoursPer7Days = s.MaxFlightHoursPer7Days;
        MaxFlightHoursPer28Days = s.MaxFlightHoursPer28Days;
        MaxFlightHoursPerYear = s.MaxFlightHoursPerYear;
        MaxDutyHoursPerDay = s.MaxDutyHoursPerDay;
        MaxDutyHoursPer7Days = s.MaxDutyHoursPer7Days;
        MaxDutyHoursPer14Days = s.MaxDutyHoursPer14Days;
        MaxDutyHoursPer28Days = s.MaxDutyHoursPer28Days;
        MaxFlightDutyHoursPerDay = s.MaxFlightDutyHoursPerDay;
        SimulateYieldInNominal = s.SimulateYieldInNominal;
        OverrideSortieAbort = s.OverrideSortieAbort;
        SortieAbortOverride = s.SortieAbortOverride;
        UseRevisingEngine = s.UseRevisingEngine;
    }
}
