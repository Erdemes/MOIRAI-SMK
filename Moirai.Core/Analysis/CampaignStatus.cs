using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Analysis;

public enum CampaignStatus { Met, Late, Incomplete }

public static class CampaignStatusExtensions
{
    public static CampaignStatus StatusOf(this Schedule schedule, Scenario scenario, Campaign campaign)
    {
        if (!schedule.CampaignCompleted.GetValueOrDefault(campaign.Id)) return CampaignStatus.Incomplete;
        var target = CampaignDeadlines.EffectiveMilestone(scenario, campaign) ?? DateOnly.MaxValue;
        return schedule.MeetsDeadline(campaign.Id, target) ? CampaignStatus.Met : CampaignStatus.Late;
    }
}
