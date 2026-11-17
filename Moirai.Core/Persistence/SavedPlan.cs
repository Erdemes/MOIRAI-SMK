using System.Text.Json.Nodes;
using MOIRAI.Scheduling;

namespace MOIRAI.Persistence;

public sealed class SavedPlan
{
    public string Name { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public string Status { get; set; } = "Not run yet";

    public bool IsSelected { get; set; }

    public JsonNode? Settings { get; set; }

    public Scenario? RunScenario { get; set; }

    public Schedule? RunSchedule { get; set; }

    public List<SavedPlanCampaignResult>? CampaignResults { get; set; }
}

public sealed class SavedPlanCampaignResult
{
    public string CampaignId { get; set; } = string.Empty;
    public string CampaignName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string EndText { get; set; } = string.Empty;
}
