using Avalonia.Media;
using MOIRAI.Domain;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.ViewModels;

namespace MOIRAI.Presentation;

public static class PlanResultsPresenter
{
    private static readonly IBrush MetBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush LateBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#EF4444"));

    public static (string Status, string EndText) StatusOf(Scenario scenario, Schedule schedule, Campaign c)
    {
        bool done = schedule.CampaignCompleted.GetValueOrDefault(c.Id);
        schedule.CampaignEndDates.TryGetValue(c.Id, out var end);
        var target = CampaignDeadlines.EffectiveMilestone(scenario, c);
        string status =
            !done ? "Incomplete"
            : target is not { } t ? "Done"
            : end.HasValue && end.Value > t ? "Late"
            : "Met";
        return (status, end.HasValue ? end.Value.ToString("yyyy-MM-dd") : "-");
    }

    public static IBrush BrushOf(string status) => status switch
    {
        "Met" or "Done" => MetBrush,
        "Late" => LateBrush,
        _ => BadBrush,
    };

    public static List<WorkloadRow> Workload(Scenario scenario, Schedule schedule, IdleReport? idle = null)
    {
        var campById = scenario.Campaigns.ToDictionary(c => c.Id);

        var load = new Dictionary<string, (double Flight, double Duty, int Sorties, int Rehearsals)>();
        void AddLoad(string id, double f, double d, int s, int r)
        {
            var c = load.GetValueOrDefault(id);
            load[id] = (c.Flight + f, c.Duty + d, c.Sorties + s, c.Rehearsals + r);
        }
        foreach (var so in schedule.Sorties)
        {
            var camp = campById[so.CampaignId];
            foreach (var id in Ids(so.PilotId, so.AdditionalPilotIds.Concat(so.SupportPilotIds).Concat(so.EngineerIds)))
                AddLoad(id, camp.ExpectedSortieFlightHours, camp.ExpectedSortieDutyHours, 1, 0);
        }
        foreach (var rh in schedule.Rehearsals)
        {
            var camp = campById[rh.CampaignId];
            foreach (var id in rh.CrewIds)
                AddLoad(id, 0, camp.ExpectedRehearsalDutyHours, 0, 1);
        }
        foreach (var ss in schedule.SimulatorSorties)
        {
            var camp = campById[ss.CampaignId];
            var ids = ss.AdditionalPilotIds.Concat(ss.EngineerIds);
            if (ss.PilotId.Length > 0) ids = ids.Prepend(ss.PilotId);
            foreach (var id in ids)
                AddLoad(id, 0, camp.ExpectedSortieDutyHours, 1, 0);
        }

        var rows = new List<WorkloadRow>();
        foreach (var p in scenario.Pilots.Cast<Resource>().Concat(scenario.Engineers))
        {
            int idleDays = idle?.IdleDays(p.Id) ?? 0;
            if (!load.TryGetValue(p.Id, out var l) && idleDays == 0) continue;
            rows.Add(new WorkloadRow
            {
                Name = p.Name,
                Role = p is Pilot ? "Pilot" : "Engineer",
                Sorties = l.Sorties,
                FlightHours = l.Flight,
                DutyHours = l.Duty,
                Rehearsals = l.Rehearsals,
                IdleDays = idleDays,
                IdleBreakdown = idle?.Breakdown(p.Id) ?? string.Empty,
            });
        }
        return rows;
    }

    public static List<AircraftUtilizationRow> AircraftUtilization(Scenario scenario, Schedule schedule,
        IdleReport? idle = null)
    {
        var campName = scenario.Campaigns.ToDictionary(c => c.Id, c => c.Name);
        var byTail = schedule.Sorties.GroupBy(s => s.AircraftId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<AircraftUtilizationRow>();
        foreach (var a in scenario.Aircraft)
        {
            var tailSorties = byTail.GetValueOrDefault(a.Id) ?? new List<Sortie>();
            var perCampaign = tailSorties
                .GroupBy(s => s.CampaignId)
                .Select(g => (Name: campName.GetValueOrDefault(g.Key, g.Key), Count: g.Count()))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
                .ToList();

            string label = string.IsNullOrWhiteSpace(a.Name) || a.Name == a.Id
                ? a.Id
                : $"{a.Id} · {a.Name}";

            rows.Add(new AircraftUtilizationRow
            {
                Name = label,
                Sorties = tailSorties.Count,
                DaysUsed = tailSorties.Select(s => s.Date).Distinct().Count(),
                CampaignBreakdown = perCampaign.Count > 0
                    ? string.Join(" · ", perCampaign.Select(x => $"{x.Name} {x.Count}"))
                    : "unused",
                IdleDays = idle?.IdleDays(a.Id) ?? 0,
                IdleBreakdown = idle?.Breakdown(a.Id) ?? string.Empty,
            });
        }
        return rows;
    }

    public static List<CampaignDetailRow> CampaignDetails(Scenario scenario, Schedule schedule)
    {
        var shortOf = ShortNames(scenario);
        return scenario.Campaigns.Select(c => DetailRow(scenario, schedule, c, shortOf)).ToList();
    }

    public static List<CampaignResultGroup> CampaignResultGroups(Scenario scenario, Schedule schedule)
    {
        var shortOf = ShortNames(scenario);
        var rollupById = ProgramReporter.Build(scenario, schedule).Groups.ToDictionary(g => g.GroupId);
        DateOnly CampKey(Campaign c) => CampaignDeadlines.EffectiveMilestone(scenario, c) ?? DateOnly.MaxValue;

        var groups = new List<CampaignResultGroup>();
        var handled = new HashSet<string>();

        foreach (var g in scenario.CampaignGroups.OrderBy(g => g.TargetEndDate ?? DateOnly.MaxValue))
        {
            var children = scenario.Campaigns.Where(c => c.GroupId == g.Id).OrderBy(CampKey).ToList();
            if (children.Count == 0) continue;
            foreach (var c in children) handled.Add(c.Id);
            groups.Add(new CampaignResultGroup
            {
                Header = rollupById.TryGetValue(g.Id, out var rollup)
                    ? PhaseRow(scenario, schedule, rollup, children)
                    : null,
                Campaigns = children.Select(c => DetailRow(scenario, schedule, c, shortOf)).ToList(),
            });
        }

        var ungrouped = scenario.Campaigns.Where(c => !handled.Contains(c.Id)).OrderBy(CampKey).ToList();
        if (ungrouped.Count > 0)
            groups.Add(new CampaignResultGroup
            {
                Header = null,
                Campaigns = ungrouped.Select(c => DetailRow(scenario, schedule, c, shortOf)).ToList(),
            });
        return groups;
    }

    private static Dictionary<string, string> ShortNames(Scenario scenario) =>
        scenario.Pilots.Cast<Resource>().Concat(scenario.Engineers).ToDictionary(r => r.Id, r => r.DisplayShort);

    private static PhaseRollupRow PhaseRow(
        Scenario scenario, Schedule schedule, GroupMilestone rollup, List<Campaign> children)
    {
        string status = rollup.Status switch
        {
            MilestoneStatus.Met => "Met",
            MilestoneStatus.Missed => "Late",
            MilestoneStatus.NoTarget => "Done",
            _ => "Incomplete",
        };
        var parts = new List<string>
        {
            rollup.Target is { } t ? $"target {t:yyyy-MM-dd}" : "no target",
        };
        if (rollup.End is { } e) parts.Add($"ends {e:yyyy-MM-dd}");
        parts.Add(rollup.ChildCount == 1 ? "1 campaign" : $"{rollup.ChildCount} campaigns");

        var why = BottleneckNarrator.DescribePhase(schedule, scenario, children);
        return new PhaseRollupRow
        {
            Name = rollup.GroupName,
            Status = status,
            StatusBrush = BrushOf(status),
            Detail = string.Join(" · ", parts),
            IsSlipping = why is not null,
            WhyGlyph = why?.Glyph ?? string.Empty,
            WhyHeadline = why?.Headline ?? string.Empty,
            WhyReason = why?.Reason ?? string.Empty,
            WhyBreakdown = why?.Breakdown ?? string.Empty,
        };
    }

    private static CampaignDetailRow DetailRow(
        Scenario scenario, Schedule schedule, Campaign c, Dictionary<string, string> shortOf)
    {
        var cs = schedule.Sorties.Where(s => s.CampaignId == c.Id).ToList();
        var sims = schedule.SimulatorSorties.Where(s => s.CampaignId == c.Id).ToList();
        var (status, endText) = StatusOf(scenario, schedule, c);
        string start = c.StartDate == DateOnly.MinValue ? "-" : c.StartDate.ToString("yyyy-MM-dd");
        string target = c.TargetEndDate == DateOnly.MaxValue ? "-" : c.TargetEndDate.ToString("yyyy-MM-dd");

        var simNames = scenario.Simulators.ToDictionary(s => s.Id, s => s.Name);
        string tails = c.Venue == CampaignVenue.Simulator
            ? string.Join(", ", sims.Select(s => s.SimulatorId).Distinct()
                .Select(id => simNames.GetValueOrDefault(id, id)))
            : string.Join(", ", cs.Select(s => s.AircraftId).Distinct());
        var pilotIds = cs.SelectMany(s => s.AdditionalPilotIds.Prepend(s.PilotId))
            .Concat(sims.SelectMany(s => s.AdditionalPilotIds.Append(s.PilotId)))
            .Where(id => id.Length > 0).Distinct();
        string pilots = string.Join(", ", pilotIds.Select(id => shortOf.GetValueOrDefault(id, id)));
        string engs = string.Join(", ", cs.SelectMany(s => s.EngineerIds)
            .Concat(sims.SelectMany(s => s.EngineerIds)).Distinct()
            .Select(id => shortOf.GetValueOrDefault(id, id)));

        var narrative = BottleneckNarrator.Describe(schedule, c);

        return new CampaignDetailRow
        {
            Name = c.Name,
            Dates = $"{start} → {target}",
            ActualEnd = endText,
            Aircraft = tails.Length > 0 ? tails : "-",
            Pilots = pilots.Length > 0 ? pilots : "-",
            Engineers = engs.Length > 0 ? engs : "-",
            Status = status,
            StatusBrush = BrushOf(status),
            IsSlipping = narrative is not null,
            Glyph = narrative?.Glyph ?? string.Empty,
            Headline = narrative?.Headline ?? string.Empty,
            Reason = narrative?.Reason ?? string.Empty,
            Advice = narrative?.Advice ?? string.Empty,
            Breakdown = narrative?.Breakdown ?? string.Empty,
            Phases = narrative?.Phases ?? string.Empty,
        };
    }

    private static IEnumerable<string> Ids(string pilotId, IEnumerable<string> engineers)
    {
        yield return pilotId;
        foreach (var e in engineers) yield return e;
    }
}
