namespace MOIRAI.Scheduling;

public sealed class CrewLimits
{
    public int MaxSortiesPerPersonPerDay { get; set; } = 6;
    public int MaxRehearsalsPerPersonPerDay { get; set; } = 1;
    public int MaxRehearsalGapDays { get; set; } = 2;
    public bool RehearsalOnWorkdaysOnly { get; set; } = true;
    public int MaxConsecutiveFlyingDays { get; set; } = 6;
    public double MinRestHours { get; set; } = 12;
    public double MaxFlightHoursPerDay { get; set; } = 6;
    public double MaxFlightHoursPer7Days { get; set; } = 30;
    public double MaxFlightHoursPer28Days { get; set; } = 100;
    public double MaxFlightHoursPerYear { get; set; } = 900;
    public double MaxDutyHoursPerDay { get; set; } = 12;
    public double MaxDutyHoursPer7Days { get; set; } = 60;
    public double MaxDutyHoursPer14Days { get; set; } = 110;
    public double MaxDutyHoursPer28Days { get; set; } = 190;

    public static CrewLimits From(ScenarioConfig c) => new()
    {
        MaxSortiesPerPersonPerDay = c.MaxSortiesPerPersonPerDay,
        MaxRehearsalsPerPersonPerDay = c.MaxRehearsalsPerPersonPerDay,
        MaxRehearsalGapDays = c.MaxRehearsalGapDays,
        RehearsalOnWorkdaysOnly = c.RehearsalOnWorkdaysOnly,
        MaxConsecutiveFlyingDays = c.MaxConsecutiveFlyingDays,
        MinRestHours = c.MinRestHours,
        MaxFlightHoursPerDay = c.MaxFlightHoursPerDay,
        MaxFlightHoursPer7Days = c.MaxFlightHoursPer7Days,
        MaxFlightHoursPer28Days = c.MaxFlightHoursPer28Days,
        MaxFlightHoursPerYear = c.MaxFlightHoursPerYear,
        MaxDutyHoursPerDay = c.MaxDutyHoursPerDay,
        MaxDutyHoursPer7Days = c.MaxDutyHoursPer7Days,
        MaxDutyHoursPer14Days = c.MaxDutyHoursPer14Days,
        MaxDutyHoursPer28Days = c.MaxDutyHoursPer28Days,
    };

    public ScenarioConfig ApplyTo(ScenarioConfig scenario)
    {
        var c = scenario.Clone();
        c.MaxSortiesPerPersonPerDay = MaxSortiesPerPersonPerDay;
        c.MaxRehearsalsPerPersonPerDay = MaxRehearsalsPerPersonPerDay;
        c.MaxRehearsalGapDays = MaxRehearsalGapDays;
        c.RehearsalOnWorkdaysOnly = RehearsalOnWorkdaysOnly;
        c.MaxConsecutiveFlyingDays = MaxConsecutiveFlyingDays;
        c.MinRestHours = MinRestHours;
        c.MaxFlightHoursPerDay = MaxFlightHoursPerDay;
        c.MaxFlightHoursPer7Days = MaxFlightHoursPer7Days;
        c.MaxFlightHoursPer28Days = MaxFlightHoursPer28Days;
        c.MaxFlightHoursPerYear = MaxFlightHoursPerYear;
        c.MaxDutyHoursPerDay = MaxDutyHoursPerDay;
        c.MaxDutyHoursPer7Days = MaxDutyHoursPer7Days;
        c.MaxDutyHoursPer14Days = MaxDutyHoursPer14Days;
        c.MaxDutyHoursPer28Days = MaxDutyHoursPer28Days;
        return c;
    }
}
