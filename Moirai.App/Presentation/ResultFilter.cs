using MOIRAI.Domain;
using MOIRAI.Scheduling;
using MOIRAI.ViewModels;

namespace MOIRAI.Presentation;

public sealed record FilteredResults(
    bool IsFiltered,
    List<ScheduleRow> Rows,
    Schedule GanttSchedule,
    string FilterSummary);

public static class ResultFilter
{
    public static FilteredResults Apply(
        Scenario scenario, Schedule schedule,
        ISet<string> camps, ISet<string> tails, ISet<string> pilots, ISet<string> ftes)
    {
        bool isFiltered = camps.Count > 0 || tails.Count > 0 || pilots.Count > 0 || ftes.Count > 0;

        bool Match(Sortie s) =>
            (camps.Count == 0 || camps.Contains(s.CampaignId))
            && (tails.Count == 0 || tails.Contains(s.AircraftId))
            && (pilots.Count == 0 || pilots.Contains(s.PilotId) || s.AdditionalPilotIds.Any(pilots.Contains))
            && (ftes.Count == 0 || s.EngineerIds.Any(ftes.Contains));

        bool MatchSim(SimulatorSortie ss) =>
            (camps.Count == 0 || camps.Contains(ss.CampaignId))
            && tails.Count == 0
            && (pilots.Count == 0 || (ss.PilotId.Length > 0 && pilots.Contains(ss.PilotId)) || ss.AdditionalPilotIds.Any(pilots.Contains))
            && (ftes.Count == 0 || ss.EngineerIds.Any(ftes.Contains));

        var sorties = schedule.Sorties.Where(Match).ToList();
        var simSorties = schedule.SimulatorSorties.Where(MatchSim).ToList();
        var kept = sorties.Select(s => (s.CampaignId, s.SortieNo)).ToHashSet();
        var rehearsals = schedule.Rehearsals
            .Where(r => kept.Contains((r.CampaignId, r.SortieNo))).ToList();

        var campNames = scenario.Campaigns.ToDictionary(c => c.Id, c => c.Name);
        var acNames = scenario.Aircraft.ToDictionary(a => a.Id, a => a.Name);
        var simNames = scenario.Simulators.ToDictionary(s => s.Id, s => s.Name);
        var people = scenario.Pilots.Cast<Resource>().Concat(scenario.Engineers)
            .ToDictionary(r => r.Id, r => r.Name);
        var campById = scenario.Campaigns.ToDictionary(c => c.Id);

        int blocksPerDay = scenario.Config.BlocksPerDay;
        var rows = sorties
            .Select(so => (so.Date, so.Block, Row: new ScheduleRow
            {
                Date = so.Date,
                Block = BlockNames.Name(so.Block, blocksPerDay),
                Campaign = campNames.GetValueOrDefault(so.CampaignId, so.CampaignId),
                SortieNo = (so.IsActual ? "✓ " : "") + $"{so.SortieNo}/{campById[so.CampaignId].RequiredSorties()}",
                Aircraft = acNames.GetValueOrDefault(so.AircraftId, so.AircraftId),
                Pilot = string.Join(", ", so.AdditionalPilotIds.Prepend(so.PilotId)
                    .Select(id => people.GetValueOrDefault(id, id))),
                Engineers = string.Join(", ", so.EngineerIds.Select(id => people.GetValueOrDefault(id, id))),
                RehearsalDate = so.RehearsalDate,
            }))
            .Concat(simSorties.Select(ss => (ss.Date, ss.Block, Row: new ScheduleRow
            {
                Date = ss.Date,
                Block = BlockNames.Name(ss.Block, blocksPerDay),
                Campaign = campNames.GetValueOrDefault(ss.CampaignId, ss.CampaignId),
                SortieNo = (ss.IsActual ? "✓ " : "") + $"{ss.SortieNo}/{campById[ss.CampaignId].RequiredSorties()}",
                Aircraft = simNames.GetValueOrDefault(ss.SimulatorId, ss.SimulatorId),
                Pilot = ss.PilotId.Length > 0
                    ? string.Join(", ", ss.AdditionalPilotIds.Prepend(ss.PilotId).Select(id => people.GetValueOrDefault(id, id)))
                    : "-",
                Engineers = string.Join(", ", ss.EngineerIds.Select(id => people.GetValueOrDefault(id, id))),
                RehearsalDate = null,
            })))
            .OrderBy(x => x.Date).ThenBy(x => x.Block)
            .Select(x => x.Row)
            .ToList();

        Schedule shown = isFiltered
            ? new Schedule
            {
                Sorties = sorties,
                SimulatorSorties = simSorties,
                Rehearsals = rehearsals,
                SimulatorRepairs = schedule.SimulatorRepairs,
                CampaignEndDates = schedule.CampaignEndDates,
                CampaignCompleted = schedule.CampaignCompleted,
                Bottlenecks = schedule.Bottlenecks,
                Diagnostics = schedule.Diagnostics,
            }
            : schedule;

        string summary = isFiltered
            ? $"Showing {sorties.Count + simSorties.Count} of {schedule.Sorties.Count + schedule.SimulatorSorties.Count} sorties ({rehearsals.Count} rehearsals)"
            : string.Empty;

        return new FilteredResults(isFiltered, rows, shown, summary);
    }
}
