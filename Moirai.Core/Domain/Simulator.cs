namespace MOIRAI.Domain;

public sealed class Simulator
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public int MaxBlocksPerDay { get; set; }

    public List<DateRange> MaintenanceWindows { get; set; } = new();

    public double FailuresPer100Hours { get; set; }

    public int RepairDaysMin { get; set; } = 1;

    public int RepairDaysMax { get; set; } = 3;

    public bool IsInMaintenance(DateOnly day) => MaintenanceWindows.Any(w => w.Contains(day));
}
