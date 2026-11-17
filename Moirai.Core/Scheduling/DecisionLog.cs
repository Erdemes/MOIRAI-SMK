using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

public enum DecisionKind
{
    Placed,
    Blocked,
    Waiting,
    Rollback,
    Repair,
    Deployment,
    Search,
    Outcome,
}

public sealed record DecisionEntry(
    int DayIndex,
    DateOnly? Date,
    string? CampaignId,
    string? CampaignName,
    DecisionKind Kind,
    string Message);
