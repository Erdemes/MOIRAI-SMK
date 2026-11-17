namespace MOIRAI.Scheduling;

public sealed class DeploymentInfo
{
    public required string LocationId { get; init; }
    public required string LocationName { get; init; }
    public required DateOnly Departure { get; init; }
    public required DateOnly FlyFrom { get; init; }
    public DateOnly? ReturnEnd { get; set; }

    public DateOnly? RestEnd { get; set; }

    public List<string> AircraftIds { get; init; } = new();
    public List<string> PersonIds { get; init; } = new();
}

public sealed class RelocationFlight
{
    public required string LocationId { get; init; }
    public required DateOnly Date { get; init; }
    public required bool Outbound { get; init; }
    public List<string> AircraftIds { get; init; } = new();
    public List<string> CrewIds { get; init; } = new();
}
