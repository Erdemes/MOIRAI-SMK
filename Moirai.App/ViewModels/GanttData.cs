using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed class GanttData
{
    public required Scenario Scenario { get; init; }
    public required Schedule Schedule { get; init; }

    public IReadOnlyCollection<string>? CampaignIds { get; init; }

    public IReadOnlyDictionary<(string CampaignId, int SortieNo), MOIRAI.Domain.SortiePin>? Pins { get; init; }

    public MOIRAI.Reporting.IdleReport? Idle { get; init; }
}
