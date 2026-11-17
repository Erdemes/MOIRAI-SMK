using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public partial class CampaignsViewModel
{
    private static readonly string[] FlyMonthNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    [ObservableProperty] private bool _editUseGlobalFlyable = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlyUniform))]
    [NotifyPropertyChangedFor(nameof(IsFlySeasonal))]
    private int _editFlyModelIndex;
    public bool IsFlyUniform => EditFlyModelIndex == 0;
    public bool IsFlySeasonal => EditFlyModelIndex == 1;

    [ObservableProperty] private decimal _editFlyRate = 0.7m;
    [ObservableProperty] private decimal _editFlyPersistence = 0.4m;

    public ObservableCollection<MonthRateOption> FlyMonthlyRates { get; } =
        new(FlyMonthNames.Select(m => new MonthRateOption(m, 0.7m)));

    [ObservableProperty] private bool _editUseGlobalLimits = true;
    [ObservableProperty] private decimal _editLimSortiesPerDay;
    [ObservableProperty] private decimal _editLimRehearsalsPerDay;
    [ObservableProperty] private decimal _editLimRehearsalGapDays;
    [ObservableProperty] private decimal _editLimConsecutiveDays;
    [ObservableProperty] private decimal _editLimMinRestHours;
    [ObservableProperty] private bool _editLimRehearsalOnWorkdaysOnly;
    [ObservableProperty] private decimal _editLimFlightPerDay;
    [ObservableProperty] private decimal _editLimFlightPer7Days;
    [ObservableProperty] private decimal _editLimFlightPer28Days;
    [ObservableProperty] private decimal _editLimFlightPerYear;
    [ObservableProperty] private decimal _editLimDutyPerDay;
    [ObservableProperty] private decimal _editLimDutyPer7Days;
    [ObservableProperty] private decimal _editLimDutyPer14Days;
    [ObservableProperty] private decimal _editLimDutyPer28Days;

    private void LoadOverrides(Campaign? c)
    {
        var sc = _repo.GetScenario();
        LoadFlyable(c?.OwnFlyableModel() ?? sc.FlyableModel);
        EditUseGlobalFlyable = c?.OwnFlyableModel() is null;
        LoadLimits(c is { UseGlobalLimits: false, Limits: { } own } ? own : CrewLimits.From(sc.Config));
        EditUseGlobalLimits = c is not { UseGlobalLimits: false, Limits: not null };
    }

    private void ApplyOverrides(Campaign c)
    {
        c.UseGlobalFlyable = EditUseGlobalFlyable;
        c.FlyableModel = EditUseGlobalFlyable ? null : BuildFlyable();
        c.UseGlobalLimits = EditUseGlobalLimits;
        c.Limits = EditUseGlobalLimits ? null : BuildLimits();
    }

    private void LoadFlyable(FlyableDayModel model)
    {
        double[] rates;
        double phi = 0.4;
        switch (model)
        {
            case ProbabilisticFlyableDays p:
                rates = p.MonthlyRates is { Length: 12 } ? p.MonthlyRates : Enumerable.Repeat(0.7, 12).ToArray();
                phi = p.Persistence;
                break;
            case RateBasedFlyableDays r:
                rates = Enumerable.Repeat(r.Rate, 12).ToArray();
                break;
            default:
                rates = Enumerable.Repeat(0.7, 12).ToArray();
                break;
        }
        for (int i = 0; i < 12; i++) FlyMonthlyRates[i].Rate = (decimal)rates[i];
        EditFlyRate = (decimal)rates.Average();
        EditFlyPersistence = (decimal)phi;
        EditFlyModelIndex = rates.Distinct().Count() > 1 ? 1 : 0;
    }

    private FlyableDayModel BuildFlyable()
    {
        var anchor = _repo.GetScenario().HorizonStart;
        double phi = Math.Clamp((double)EditFlyPersistence, 0, 0.98);
        return IsFlySeasonal
            ? new ProbabilisticFlyableDays
            {
                MonthlyRates = FlyMonthlyRates.Select(m => Math.Clamp((double)m.Rate, 0, 1)).ToArray(),
                Persistence = phi,
                Anchor = anchor,
            }
            : ProbabilisticFlyableDays.Uniform(Math.Clamp((double)EditFlyRate, 0, 1), phi, anchor);
    }

    private void LoadLimits(CrewLimits l)
    {
        EditLimSortiesPerDay = l.MaxSortiesPerPersonPerDay;
        EditLimRehearsalsPerDay = l.MaxRehearsalsPerPersonPerDay;
        EditLimRehearsalGapDays = l.MaxRehearsalGapDays;
        EditLimConsecutiveDays = l.MaxConsecutiveFlyingDays;
        EditLimMinRestHours = (decimal)l.MinRestHours;
        EditLimRehearsalOnWorkdaysOnly = l.RehearsalOnWorkdaysOnly;
        EditLimFlightPerDay = (decimal)l.MaxFlightHoursPerDay;
        EditLimFlightPer7Days = (decimal)l.MaxFlightHoursPer7Days;
        EditLimFlightPer28Days = (decimal)l.MaxFlightHoursPer28Days;
        EditLimFlightPerYear = (decimal)l.MaxFlightHoursPerYear;
        EditLimDutyPerDay = (decimal)l.MaxDutyHoursPerDay;
        EditLimDutyPer7Days = (decimal)l.MaxDutyHoursPer7Days;
        EditLimDutyPer14Days = (decimal)l.MaxDutyHoursPer14Days;
        EditLimDutyPer28Days = (decimal)l.MaxDutyHoursPer28Days;
    }

    private CrewLimits BuildLimits() => new()
    {
        MaxSortiesPerPersonPerDay = Math.Max(0, (int)EditLimSortiesPerDay),
        MaxRehearsalsPerPersonPerDay = Math.Max(0, (int)EditLimRehearsalsPerDay),
        MaxRehearsalGapDays = Math.Max(0, (int)EditLimRehearsalGapDays),
        MaxConsecutiveFlyingDays = Math.Max(0, (int)EditLimConsecutiveDays),
        MinRestHours = Math.Max(0, (double)EditLimMinRestHours),
        RehearsalOnWorkdaysOnly = EditLimRehearsalOnWorkdaysOnly,
        MaxFlightHoursPerDay = Math.Max(0, (double)EditLimFlightPerDay),
        MaxFlightHoursPer7Days = Math.Max(0, (double)EditLimFlightPer7Days),
        MaxFlightHoursPer28Days = Math.Max(0, (double)EditLimFlightPer28Days),
        MaxFlightHoursPerYear = Math.Max(0, (double)EditLimFlightPerYear),
        MaxDutyHoursPerDay = Math.Max(0, (double)EditLimDutyPerDay),
        MaxDutyHoursPer7Days = Math.Max(0, (double)EditLimDutyPer7Days),
        MaxDutyHoursPer14Days = Math.Max(0, (double)EditLimDutyPer14Days),
        MaxDutyHoursPer28Days = Math.Max(0, (double)EditLimDutyPer28Days),
    };
}
