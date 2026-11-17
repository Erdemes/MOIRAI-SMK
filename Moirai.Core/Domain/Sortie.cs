namespace MOIRAI.Domain;

public sealed class Sortie
{
    public string CampaignId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }

    public int Block { get; set; } = 1;

    public int SortieNo { get; set; }

    public string AircraftId { get; set; } = string.Empty;

    public string PilotId { get; set; } = string.Empty;

    public List<string> AdditionalPilotIds { get; set; } = new();

    public List<string> EngineerIds { get; set; } = new();

    public double TestPointsFlown { get; set; }

    public bool Aborted { get; set; }

    public bool IsActual { get; set; }

    public DateOnly? RehearsalDate { get; set; }

    public List<string> SupportAircraftIds { get; set; } = new();

    public List<string> SupportPilotIds { get; set; } = new();

    public List<string> ExternalSupportIds { get; set; } = new();
}
