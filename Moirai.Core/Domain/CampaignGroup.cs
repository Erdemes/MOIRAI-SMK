namespace MOIRAI.Domain;

public sealed class CampaignGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public DateOnly? TargetStartDate { get; set; }

    public DateOnly? TargetEndDate { get; set; }

    public List<string> PrerequisiteGroupIds { get; set; } = new();

    public string? Color { get; set; }
}
