using MOIRAI.Domain;
using MOIRAI.Persistence;

namespace MOIRAI.Scheduling.Revising;

internal sealed class GreedyReorderSearch
{
    private readonly RevisingOptions _options;

    public GreedyReorderSearch(RevisingOptions options) => _options = options;

    public Schedule Run(Scenario scenario, IProgress<double>? progress, CancellationToken ct)
    {
        var baseline = new Scheduler().Run(scenario);
        if (!_options.EnableGlobalSearch || scenario.Campaigns.Count <= 1)
            return baseline;

        var work = ScenarioJson.Clone(scenario);
        var workById = work.Campaigns.ToDictionary(c => c.Id);

        Schedule Eval(List<string> order)
        {
            for (int i = 0; i < order.Count; i++)
                if (workById.TryGetValue(order[i], out var c)) c.PriorityKey = i;
            return new Scheduler().Run(work);
        }

        var rng = new Random(_options.SearchSeed);
        var startOrder = NaturalOrder(scenario);
        var bestOrder = startOrder;
        var bestSchedule = baseline;
        var bestScore = ScheduleObjective.Of(scenario, baseline);
        var curOrder = startOrder;
        var curScore = bestScore;

        int iters = Math.Max(0, _options.GlobalSearchIterations);
        double t0 = Math.Max(1e-6, _options.GlobalSearchStartTemp);
        const double tEnd = 0.05;
        double alpha = iters > 1 ? Math.Pow(tEnd / t0, 1.0 / iters) : 1.0;
        double t = t0;
        int sinceImprove = 0;

        for (int it = 0; it < iters; it++)
        {
            ct.ThrowIfCancellationRequested();
            if ((it & 63) == 0) progress?.Report((double)it / iters);

            var candOrder = ScheduleObjective.Ruin(curOrder, _options.MaxOrderRuin, rng);
            var candSchedule = Eval(candOrder);
            var candScore = ScheduleObjective.Of(scenario, candSchedule);

            if (ScheduleObjective.Accept(candScore, curScore, t, rng)) { curOrder = candOrder; curScore = candScore; }

            if (candScore.CompareTo(bestScore) < 0)
            {
                bestScore = candScore; bestSchedule = candSchedule; bestOrder = candOrder;
                sinceImprove = 0;
            }
            else sinceImprove++;

            t *= alpha;
            if (_options.GlobalSearchStall > 0 && sinceImprove >= _options.GlobalSearchStall)
            {
                curOrder = bestOrder; curScore = bestScore; t = t0; sinceImprove = 0;
            }
        }
        progress?.Report(1.0);
        return bestSchedule;
    }

    private static List<string> NaturalOrder(Scenario sc)
    {
        DateOnly DeadlineKey(Campaign c) =>
            CampaignDeadlines.EffectiveTarget(sc, c) ?? DateOnly.MaxValue;
        double OrderKey(Campaign c) => c.PriorityKey ?? DeadlineKey(c).DayNumber;
        return sc.Campaigns.OrderBy(OrderKey).Select(c => c.Id).ToList();
    }
}
