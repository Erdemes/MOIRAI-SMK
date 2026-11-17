using MOIRAI.Domain;

namespace MOIRAI.Scheduling.Revising;

internal static class ScheduleObjective
{
    public readonly record struct Score(int Incomplete, int Missed, int Lateness, int Makespan)
        : IComparable<Score>
    {
        public int CompareTo(Score o)
        {
            int c = Incomplete.CompareTo(o.Incomplete); if (c != 0) return c;
            c = Missed.CompareTo(o.Missed); if (c != 0) return c;
            c = Lateness.CompareTo(o.Lateness); if (c != 0) return c;
            return Makespan.CompareTo(o.Makespan);
        }
    }

    public static Score Of(Scenario sc, Schedule sch)
    {
        int incomplete = 0, missed = 0, lateness = 0;
        foreach (var c in sc.Campaigns)
        {
            if (!sch.CampaignCompleted.GetValueOrDefault(c.Id)) { incomplete++; continue; }
            if (DeadlineFor(sc, c) is { } dl
                && sch.CampaignEndDates.TryGetValue(c.Id, out var end) && end is { } e)
            {
                int late = e.DayNumber - dl.DayNumber;
                if (late > 0) { missed++; lateness += late; }
            }
        }
        int makespan = sch.ProgramFinish is { } pf ? Math.Max(0, pf.DayNumber - sc.HorizonStart.DayNumber) : 0;
        return new Score(incomplete, missed, lateness, makespan);
    }

    public static double Scalar(Score s) =>
        s.Incomplete * 1_000_000.0 + s.Missed * 1_000.0 + s.Lateness + s.Makespan * 0.001;

    public static bool Accept(Score cand, Score cur, double t, Random rng)
    {
        if (cand.CompareTo(cur) <= 0) return true;
        double delta = Scalar(cand) - Scalar(cur);
        return rng.NextDouble() < Math.Exp(-delta / Math.Max(1e-9, t));
    }

    public static List<T> Ruin<T>(List<T> order, int maxRuin, Random rng)
    {
        var next = new List<T>(order);
        int n = next.Count;
        int k = 1 + rng.Next(Math.Min(Math.Max(1, maxRuin), Math.Max(1, n)));

        var removeAt = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(k)
            .OrderByDescending(x => x).ToList();
        var removed = new List<T>(k);
        foreach (var x in removeAt) { removed.Add(next[x]); next.RemoveAt(x); }
        foreach (var item in removed) next.Insert(rng.Next(next.Count + 1), item);
        return next;
    }

    private static DateOnly? DeadlineFor(Scenario sc, Campaign c) =>
        CampaignDeadlines.EffectiveTarget(sc, c);
}
