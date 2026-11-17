namespace MOIRAI.Domain;

public sealed class SortiePin
{
    public string CampaignId { get; set; } = string.Empty;

    public int SortieNo { get; set; }

    public string? AircraftId { get; set; }

    public List<string> PilotIds { get; set; } = new();

    public List<string> EngineerIds { get; set; } = new();

    public List<string> SupportAircraftIds { get; set; } = new();

    public List<string> SupportPilotIds { get; set; } = new();

    public string? SimulatorId { get; set; }

    public string? Note { get; set; }

    public DateOnly? Date { get; set; }

    public bool IsEmpty =>
        Date is null && string.IsNullOrEmpty(AircraftId) && string.IsNullOrEmpty(SimulatorId)
        && PilotIds.Count == 0 && EngineerIds.Count == 0
        && SupportAircraftIds.Count == 0 && SupportPilotIds.Count == 0;

    public static SortiePin From(Sortie s) => new()
    {
        CampaignId = s.CampaignId,
        SortieNo = s.SortieNo,
        AircraftId = s.AircraftId,
        PilotIds = new[] { s.PilotId }.Where(p => p.Length > 0).Concat(s.AdditionalPilotIds).ToList(),
        EngineerIds = new List<string>(s.EngineerIds),
        SupportAircraftIds = new List<string>(s.SupportAircraftIds),
        SupportPilotIds = new List<string>(s.SupportPilotIds),
    };

    public static SortiePin From(SimulatorSortie s) => new()
    {
        CampaignId = s.CampaignId,
        SortieNo = s.SortieNo,
        SimulatorId = s.SimulatorId,
        PilotIds = new[] { s.PilotId }.Where(p => p.Length > 0).Concat(s.AdditionalPilotIds).ToList(),
        EngineerIds = new List<string>(s.EngineerIds),
    };
}
