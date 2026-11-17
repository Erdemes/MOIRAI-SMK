using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Presentation;

public static class BottleneckNarrator
{
    public readonly record struct Narrative(
        string Glyph, string Headline, string Reason, string Advice, string Breakdown,
        string Phases);

    public static Narrative? Describe(Schedule schedule, Campaign c)
    {
        bool done = schedule.CampaignCompleted.GetValueOrDefault(c.Id);
        schedule.CampaignEndDates.TryGetValue(c.Id, out var end);
        bool late = done && end.HasValue && end.Value > c.TargetEndDate;
        if (done && !late) return null;

        schedule.Bottlenecks.TryGetValue(c.Id, out var tally);

        string glyph, headline;
        if (!done)
        {
            int flown = schedule.Sorties.Count(s => s.CampaignId == c.Id)
                + schedule.SimulatorSorties.Count(s => s.CampaignId == c.Id);
            glyph = "⛔";
            headline = $"Incomplete · {flown} of {c.RequiredSorties()} sorties flown";
        }
        else
        {
            int days = end!.Value.DayNumber - c.TargetEndDate.DayNumber;
            glyph = "⚠";
            headline = $"{days} day{(days == 1 ? "" : "s")} late";
        }

        var (reason, advice) = tally?.Dominant() is { } dom
            ? Explain(dom.Reason, dom.Days)
            : ("No single blocking reason was recorded for this campaign.", "");

        string phases = schedule.DayLog.GetValueOrDefault(c.Id) is { } log
            ? DayLogCodes.PhaseSummary(log)
            : string.Empty;

        return new Narrative(glyph, headline, reason, advice,
            tally is null ? "" : Breakdown(tally), phases);
    }

    public readonly record struct PhaseNarrative(string Glyph, string Headline, string Reason, string Breakdown);

    public static PhaseNarrative? DescribePhase(
        Schedule schedule, Scenario scenario, IReadOnlyList<Campaign> children)
    {
        var slipping = children.Where(c => schedule.StatusOf(scenario, c) != CampaignStatus.Met).ToList();
        if (slipping.Count == 0) return null;

        var agg = new BottleneckTally();
        foreach (var c in slipping)
            if (schedule.Bottlenecks.TryGetValue(c.Id, out var t))
            {
                agg.Prereq += t.Prereq; agg.NotUsable += t.NotUsable; agg.Range += t.Range;
                agg.Aircraft += t.Aircraft; agg.Pilot += t.Pilot; agg.Engineer += t.Engineer;
                agg.Rehearsal += t.Rehearsal; agg.Review += t.Review; agg.Pinned += t.Pinned;
            }

        bool anyIncomplete = slipping.Any(c => !schedule.CampaignCompleted.GetValueOrDefault(c.Id));
        string headline = $"{slipping.Count} of {children.Count} campaign{(children.Count == 1 ? "" : "s")} slipped";
        var (reason, _) = agg.Dominant() is { } dom
            ? Explain(dom.Reason, dom.Days)
            : ("No single blocking reason dominated the phase.", "");
        return new PhaseNarrative(anyIncomplete ? "⛔" : "⚠", headline, reason, Breakdown(agg));
    }

    private static (string Reason, string Advice) Explain(string reason, int days)
    {
        string d = $"{days} day{(days == 1 ? "" : "s")}";
        return reason switch
        {
            "aircraft" => ($"No compatible aircraft was free on {d} (in maintenance or already in use).",
                           "Add or free a compatible aircraft (tail) for this campaign."),
            "weather/calendar" => ($"{d} were not usable (weather or calendar).",
                           "Weather-bound · shift the window to a better season, raise test points per sortie, " +
                           "or accept the risk. Extra crew or aircraft won’t add flyable days."),
            "pilot" => ($"No eligible pilot was available on {d}.",
                           "Assign another pilot to this campaign, or add one."),
            "engineer" => ($"Too few engineers were available on {d}.",
                           "Assign more FTEs to this campaign, or add one."),
            "prerequisite" => ($"Blocked {d} waiting on an unfinished prerequisite campaign.",
                           "Finish or re-sequence the prerequisite, or start this campaign earlier."),
            "rehearsal" => ($"A required simulator rehearsal could not be scheduled on {d}.",
                           "Increase the rehearsal gap (working days) or free a crew block before the sortie."),
            "data review" => ($"Waiting out the inter-sortie data-review gap on {d} (campaign pacing).",
                           "Inherent pacing · raise test points per sortie, shorten the review gap, " +
                           "or accept it. Extra crew or aircraft won't speed up the analysis."),
            "pinned resources" => ($"A pinned sortie waited {d} for its pinned crew/aircraft.",
                           "User-imposed · unpin the sortie (right-click on the Gantt) or free the pinned " +
                           "resources. Adding crew or aircraft won't help a pinned sortie."),
            _ => ($"Blocked on {d}.", ""),
        };
    }

    private static string Breakdown(BottleneckTally t)
    {
        var parts = new (string Label, int Days)[]
        {
            ("aircraft", t.Aircraft),
            ("weather", t.NotUsable),
            ("pilot", t.Pilot),
            ("engineer", t.Engineer),
            ("prereq", t.Prereq),
            ("rehearsal", t.Rehearsal),
            ("review", t.Review),
            ("pinned", t.Pinned),
        };
        return string.Join("  ·  ",
            parts.Where(p => p.Days > 0)
                 .OrderByDescending(p => p.Days)
                 .Select(p => $"{p.Label} {p.Days}d"));
    }
}
