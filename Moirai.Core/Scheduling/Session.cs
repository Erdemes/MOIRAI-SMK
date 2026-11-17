namespace MOIRAI.Scheduling;

public sealed class Session
{
    public string Name { get; set; } = "Untitled session";

    public List<Scenario> Scenarios { get; set; } = new();

    public string? ActiveScenarioId { get; set; }

    public Scenario? FindActive() =>
        Scenarios.FirstOrDefault(s => s.Id == ActiveScenarioId) ?? Scenarios.FirstOrDefault();
}
