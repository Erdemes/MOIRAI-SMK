using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Reporting;

public enum MilestoneStatus
{
    Met,
    Missed,
    NoTarget,
    Incomplete,
}

public sealed class CampaignMilestone
{
    public required string CampaignId { get; init; }
    public required string CampaignName { get; init; }
    public DateOnly? Target { get; init; }
    public MilestoneStatus Status { get; init; }
    public DateOnly? End { get; init; }
}

public sealed class GroupMilestone
{
    public required string GroupId { get; init; }
    public required string GroupName { get; init; }
    public DateOnly? Target { get; init; }
    public MilestoneStatus Status { get; init; }
    public DateOnly? End { get; init; }
    public int ChildCount { get; init; }
}

public sealed class ProgramReport
{
    public DateOnly? MasterDeadline { get; init; }

    public DateOnly? ProgramFinish { get; init; }

    public bool AllCompleted { get; init; }

    public bool Delivered { get; init; }

    public int? SlackDays { get; init; }

    public int OverdueSorties { get; init; }

    public required List<CampaignMilestone> Campaigns { get; init; }

    public List<GroupMilestone> Groups { get; init; } = new();

    public int TotalSorties { get; init; }
    public int TotalRehearsals { get; init; }
    public double TotalFlightHours { get; init; }
    public double TotalDutyHours { get; init; }
    public int AircraftUsed { get; init; }
    public int CrewUsed { get; init; }
}

public static class ProgramReporter
{
    public static ProgramReport Build(Scenario scenario, Schedule schedule)
    {
        var campaigns = scenario.Campaigns.Select(c => new CampaignMilestone
        {
            CampaignId = c.Id,
            CampaignName = c.Name,
            Target = CampaignDeadlines.EffectiveMilestone(scenario, c),
            Status = MilestoneOf(scenario, schedule, c),
            End = schedule.CampaignEndDates.GetValueOrDefault(c.Id),
        }).ToList();

        var crew = new HashSet<string>();
        foreach (var s in schedule.Sorties)
        {
            crew.Add(s.PilotId);
            crew.UnionWith(s.AdditionalPilotIds);
            crew.UnionWith(s.SupportPilotIds);
            crew.UnionWith(s.EngineerIds);
        }
        foreach (var ss in schedule.SimulatorSorties)
        {
            if (ss.PilotId.Length > 0) crew.Add(ss.PilotId);
            crew.UnionWith(ss.AdditionalPilotIds);
            crew.UnionWith(ss.EngineerIds);
        }
        var campById = scenario.Campaigns.ToDictionary(c => c.Id);
        foreach (var rehearsal in schedule.Rehearsals) crew.UnionWith(rehearsal.CrewIds);

        return new ProgramReport
        {
            MasterDeadline = scenario.MasterDeadline,
            ProgramFinish = schedule.ProgramFinish,
            AllCompleted = schedule.AllCampaignsCompleted(),
            Delivered = IsDelivered(scenario, schedule),
            SlackDays = scenario.MasterDeadline is { } m && schedule.ProgramFinish is { } mk
                ? m.DayNumber - mk.DayNumber
                : null,
            OverdueSorties = scenario.MasterDeadline is { } master
                ? schedule.Sorties.Count(s => s.Date > master) + schedule.SimulatorSorties.Count(s => s.Date > master)
                : 0,
            Campaigns = campaigns,
            Groups = BuildGroupRollup(scenario, schedule),
            TotalSorties = schedule.Sorties.Count + schedule.SimulatorSorties.Count,
            TotalRehearsals = schedule.Rehearsals.Count,
            TotalFlightHours = schedule.Sorties.Sum(s => campById[s.CampaignId].ExpectedSortieFlightHours
                * (1 + s.SupportAircraftIds.Distinct().Count())),
            TotalDutyHours = schedule.Sorties.Sum(s => campById[s.CampaignId].ExpectedSortieDutyHours)
                             + schedule.Rehearsals.Sum(r => campById[r.CampaignId].ExpectedRehearsalDutyHours)
                             + schedule.SimulatorSorties.Sum(s => campById[s.CampaignId].ExpectedSortieDutyHours),
            AircraftUsed = schedule.Sorties.SelectMany(s => s.SupportAircraftIds.Prepend(s.AircraftId)).Distinct().Count(),
            CrewUsed = crew.Count,
        };
    }

    public static bool IsDelivered(Scenario scenario, Schedule schedule) =>
        schedule.MeetsMaster(scenario.MasterDeadline);

    private static MilestoneStatus MilestoneOf(Scenario scenario, Schedule schedule, Campaign c)
    {
        if (!schedule.CampaignCompleted.GetValueOrDefault(c.Id)) return MilestoneStatus.Incomplete;
        if (CampaignDeadlines.EffectiveMilestone(scenario, c) is not { } target) return MilestoneStatus.NoTarget;
        return schedule.MeetsDeadline(c.Id, target) ? MilestoneStatus.Met : MilestoneStatus.Missed;
    }

    private static List<GroupMilestone> BuildGroupRollup(Scenario scenario, Schedule schedule)
    {
        var rows = new List<GroupMilestone>();
        foreach (var g in scenario.CampaignGroups)
        {
            var children = scenario.Campaigns.Where(c => c.GroupId == g.Id).ToList();
            if (children.Count == 0) continue;

            bool allDone = children.All(c => schedule.CampaignCompleted.GetValueOrDefault(c.Id));

            DateOnly? end = null;
            if (allDone)
                foreach (var c in children)
                    if (schedule.CampaignEndDates.GetValueOrDefault(c.Id) is { } e && (end is null || e > end))
                        end = e;

            MilestoneStatus status =
                !allDone ? MilestoneStatus.Incomplete
                : g.TargetEndDate is not { } target ? MilestoneStatus.NoTarget
                : end is { } phaseEnd && phaseEnd <= target ? MilestoneStatus.Met
                : MilestoneStatus.Missed;

            rows.Add(new GroupMilestone
            {
                GroupId = g.Id,
                GroupName = g.Name,
                Target = g.TargetEndDate,
                Status = status,
                End = end,
                ChildCount = children.Count,
            });
        }
        return rows;
    }
}
