namespace MOIRAI.Domain;

public sealed class PeriodicMaintenanceRule
{
    public double IntervalFlightHours { get; set; }

    public int DurationDays { get; set; }
}
