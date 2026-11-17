namespace MOIRAI.Domain;

public sealed class Aircraft
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public int MinPilots { get; set; } = 1;

    public List<DateRange> MaintenanceWindows { get; set; } = new();

    public List<PeriodicMaintenanceRule> PeriodicMaintenances { get; set; } = new();

    public double FailuresPer100FlightHours { get; set; }

    public int RepairDaysMin { get; set; } = 1;

    public int RepairDaysMax { get; set; } = 3;

    public bool IsInMaintenance(DateOnly day) => MaintenanceWindows.Any(w => w.Contains(day));
}
