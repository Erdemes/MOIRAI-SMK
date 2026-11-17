namespace MOIRAI.Domain;

public sealed class Rehearsal
{
    public string CampaignId { get; set; } = string.Empty;

    public DateOnly Date { get; set; }

    public int Block { get; set; } = 1;

    public int SortieNo { get; set; }

    public DateOnly SortieDate { get; set; }

    public string PilotId { get; set; } = string.Empty;

    public List<string> AdditionalPilotIds { get; set; } = new();
    public List<string> SupportPilotIds { get; set; } = new();
    public string SimulatorId { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;

    public List<string> EngineerIds { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> CrewIds => AdditionalPilotIds.Prepend(PilotId)
        .Concat(SupportPilotIds).Concat(EngineerIds).Where(id => id.Length > 0).Distinct();

    public bool IsActual { get; set; }
}
