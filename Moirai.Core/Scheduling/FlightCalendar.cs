namespace MOIRAI.Scheduling;

public sealed class FlightCalendar
{
    public HashSet<DateOnly> Holidays { get; set; } = new();

    public HashSet<DayOfWeek> WeekendDays { get; set; } = new() { DayOfWeek.Saturday, DayOfWeek.Sunday };

    public HashSet<DateOnly> ExtraWorkdays { get; set; } = new();

    public bool IsWorkday(DateOnly date) =>
        ExtraWorkdays.Contains(date)
        || (!WeekendDays.Contains(date.DayOfWeek) && !Holidays.Contains(date));

    public bool IsWorkday(Domain.Campaign c, DateOnly date) =>
        c.UseGlobalCalendar
            ? IsWorkday(date)
            : ExtraWorkdays.Contains(date)
              || (!c.WeekendDays.Contains(date.DayOfWeek) && (c.WorkOnHolidays || !Holidays.Contains(date)));
}
