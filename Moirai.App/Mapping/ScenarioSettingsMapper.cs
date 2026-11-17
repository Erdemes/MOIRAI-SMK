using MOIRAI.Scheduling;
using MOIRAI.ViewModels;

namespace MOIRAI.Mapping;

public static class ScenarioSettingsMapper
{
    public static PlanSettings FromScenario(Scenario sc)
    {
        var cfg = sc.Config;
        var s = new PlanSettings
        {
            HorizonStart = ToNullable(sc.HorizonStart),
            HorizonEnd = ToNullable(sc.HorizonEnd),
            MasterDeadline = sc.MasterDeadline?.ToDateTime(TimeOnly.MinValue),
            WeekendDays = WeekdaySet.ToArray(sc.Calendar.WeekendDays),
            Holidays = HolidayList.Format(sc.Calendar.Holidays),
            BlocksPerDay = cfg.BlocksPerDay,
            LoadBalanceWindowDays = cfg.LoadBalanceWindowDays,
            MaxSortiesPerDay = cfg.MaxSortiesPerPersonPerDay,
            MaxRehearsalsPerDay = cfg.MaxRehearsalsPerPersonPerDay,
            PilotsPerSortie = cfg.PilotsPerSortie,
            EngineersPerSortie = cfg.EngineersPerSortie,
            MaxRehearsalGapDays = cfg.MaxRehearsalGapDays,
            RehearsalOnWorkdaysOnly = cfg.RehearsalOnWorkdaysOnly,
            MaxConsecutiveFlyingDays = cfg.MaxConsecutiveFlyingDays,
            MinRestHours = (decimal)cfg.MinRestHours,
            MaxFlightHoursPerDay = (decimal)cfg.MaxFlightHoursPerDay,
            MaxFlightHoursPer7Days = (decimal)cfg.MaxFlightHoursPer7Days,
            MaxFlightHoursPer28Days = (decimal)cfg.MaxFlightHoursPer28Days,
            MaxFlightHoursPerYear = (decimal)cfg.MaxFlightHoursPerYear,
            MaxDutyHoursPerDay = (decimal)cfg.MaxDutyHoursPerDay,
            MaxDutyHoursPer7Days = (decimal)cfg.MaxDutyHoursPer7Days,
            MaxDutyHoursPer14Days = (decimal)cfg.MaxDutyHoursPer14Days,
            MaxDutyHoursPer28Days = (decimal)cfg.MaxDutyHoursPer28Days,
            MaxFlightDutyHoursPerDay = double.IsFinite(cfg.MaxFlightDutyHoursPerDay)
                ? (decimal)cfg.MaxFlightDutyHoursPerDay
                : 0,
            SimulateYieldInNominal = cfg.SimulateYieldInNominal,
            OverrideSortieAbort = cfg.OverrideSortieAbort,
            SortieAbortOverride = (decimal)cfg.SortieAbortOverride,
            UseRevisingEngine = cfg.UseRevisingEngine,
        };

        switch (sc.FlyableModel)
        {
            case RateBasedFlyableDays rate:
                s.FlyableModelIndex = 0;
                s.FlyableRate = (decimal)rate.Rate;
                s.FlyablePatternLength = rate.PatternLength;
                s.FlyableAnchor = ToNullable(rate.Anchor);
                break;
            case ProbabilisticFlyableDays prob:
                s.FlyableModelIndex = 1;
                if (prob.MonthlyRates is { Length: 12 })
                    s.MonthlyRates = prob.MonthlyRates.Select(r => (decimal)r).ToArray();
                s.Persistence = (decimal)prob.Persistence;
                s.FlyablePatternLength = prob.PatternLength;
                s.FlyableAnchor = ToNullable(prob.Anchor);
                break;
        }
        return s;
    }

    public static string? Validate(PlanSettings s)
    {
        if (s.HorizonStart is null || s.HorizonEnd is null)
            return "Set both the planning horizon start and end dates.";
        if (s.HorizonEnd.Value.Date < s.HorizonStart.Value.Date)
            return "The horizon end date must be on or after the start date.";
        if (s.MasterDeadline is { } md && md.Date < s.HorizonStart.Value.Date)
            return "The master deadline must be on or after the horizon start.";
        if (s.FlyableModelIndex == 1 && s.Persistence is < 0 or >= 1)
            return "Persistence must be in [0, 1).";
        if (s.BlocksPerDay < 1)
            return "Blocks per day must be at least 1.";
        return null;
    }

    public static (ScenarioConfig Config, FlightCalendar Calendar, FlyableDayModel Flyable, DateOnly Start, DateOnly End)
        ToDomain(PlanSettings s)
    {
        var config = new ScenarioConfig
        {
            BlocksPerDay = (int)s.BlocksPerDay,
            LoadBalanceWindowDays = (int)s.LoadBalanceWindowDays,
            MaxSortiesPerPersonPerDay = (int)s.MaxSortiesPerDay,
            MaxRehearsalsPerPersonPerDay = (int)s.MaxRehearsalsPerDay,
            PilotsPerSortie = (int)s.PilotsPerSortie,
            EngineersPerSortie = (int)s.EngineersPerSortie,
            MaxRehearsalGapDays = (int)s.MaxRehearsalGapDays,
            RehearsalOnWorkdaysOnly = s.RehearsalOnWorkdaysOnly,
            MaxConsecutiveFlyingDays = (int)s.MaxConsecutiveFlyingDays,
            MinRestHours = (double)s.MinRestHours,
            MaxFlightHoursPerDay = (double)s.MaxFlightHoursPerDay,
            MaxFlightHoursPer7Days = (double)s.MaxFlightHoursPer7Days,
            MaxFlightHoursPer28Days = (double)s.MaxFlightHoursPer28Days,
            MaxFlightHoursPerYear = (double)s.MaxFlightHoursPerYear,
            MaxDutyHoursPerDay = (double)s.MaxDutyHoursPerDay,
            MaxDutyHoursPer7Days = (double)s.MaxDutyHoursPer7Days,
            MaxDutyHoursPer14Days = (double)s.MaxDutyHoursPer14Days,
            MaxDutyHoursPer28Days = (double)s.MaxDutyHoursPer28Days,
            MaxFlightDutyHoursPerDay = s.MaxFlightDutyHoursPerDay <= 0
                ? double.PositiveInfinity
                : (double)s.MaxFlightDutyHoursPerDay,
            SimulateYieldInNominal = s.SimulateYieldInNominal,
            OverrideSortieAbort = s.OverrideSortieAbort,
            SortieAbortOverride = (double)s.SortieAbortOverride,
            UseRevisingEngine = s.UseRevisingEngine,
        };

        var calendar = new FlightCalendar
        {
            WeekendDays = WeekdaySet.ToSet(s.WeekendDays),
            Holidays = HolidayList.Parse(s.Holidays),
        };

        var anchor = DateOnly.FromDateTime(s.HorizonStart!.Value);
        double[] monthly = s.MonthlyRates.Select(r => (double)r).ToArray();
        double meanRate = monthly is { Length: > 0 } ? monthly.Average() : 0;
        FlyableDayModel flyable = s.FlyableModelIndex == 1
            ? new ProbabilisticFlyableDays
            {
                MonthlyRates = monthly,
                Persistence = (double)s.Persistence,
                PatternLength = RateBasedFlyableDays.IdealPatternLength(meanRate),
                Anchor = anchor,
            }
            : new RateBasedFlyableDays
            {
                Rate = (double)s.FlyableRate,
                PatternLength = RateBasedFlyableDays.IdealPatternLength((double)s.FlyableRate),
                Anchor = anchor,
            };

        return (config, calendar, flyable,
            DateOnly.FromDateTime(s.HorizonStart!.Value), DateOnly.FromDateTime(s.HorizonEnd!.Value));
    }

    private static DateTime? ToNullable(DateOnly date) =>
        date == default ? null : date.ToDateTime(TimeOnly.MinValue);
}
