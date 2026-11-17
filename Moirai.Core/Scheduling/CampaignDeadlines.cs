using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

public static class CampaignDeadlines
{
    public static DateOnly? EffectiveTarget(Scenario sc, Campaign c)
    {
        if (c.TargetEndDate != DateOnly.MaxValue) return c.TargetEndDate;
        if (sc.GroupOf(c)?.TargetEndDate is { } groupTarget) return groupTarget;
        return sc.MasterDeadline;
    }

    public static DateOnly? EffectiveMilestone(Scenario sc, Campaign c)
    {
        if (c.TargetEndDate != DateOnly.MaxValue) return c.TargetEndDate;
        if (sc.GroupOf(c)?.TargetEndDate is { } groupTarget) return groupTarget;
        return null;
    }

    public static List<string> EffectivePrerequisiteCampaignIds(Scenario sc, Campaign c)
    {
        if (sc.GroupOf(c) is not { PrerequisiteGroupIds.Count: > 0 } group)
            return c.PrerequisiteCampaignIds;

        var ids = new List<string>(c.PrerequisiteCampaignIds);
        var seen = new HashSet<string>(ids);
        foreach (var child in sc.Campaigns)
            if (child.GroupId.Length > 0
                && group.PrerequisiteGroupIds.Contains(child.GroupId)
                && seen.Add(child.Id))
                ids.Add(child.Id);
        return ids;
    }
}
