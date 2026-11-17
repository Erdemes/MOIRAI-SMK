using System.Text;
using MOIRAI.Analysis;
using MOIRAI.Scheduling;

namespace MOIRAI.Reporting;

public static class BottleneckReport
{
    public static string Build(Schedule schedule, Scenario scenario)
    {
        var sb = new StringBuilder();
        foreach (var camp in scenario.Campaigns
                     .OrderBy(c => CampaignDeadlines.EffectiveMilestone(scenario, c) ?? DateOnly.MaxValue))
        {
            var status = schedule.StatusOf(scenario, camp);
            var tally = schedule.Bottlenecks.GetValueOrDefault(camp.Id);
            var dominant = tally?.Dominant();
            string blocking = dominant is { } d
                ? $"dominant bottleneck = {d.Reason} ({d.Days} days)"
                : "no blocking days";
            string line = $"{camp.Name} [{status}]: {blocking}, placed on {tally?.Placed ?? 0} day(s)";

            if (schedule.DayLog.GetValueOrDefault(camp.Id) is { } log
                && DayLogCodes.PhaseSummary(log) is { Length: > 0 } phases)
                line += $" · phases: {phases}";
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}
