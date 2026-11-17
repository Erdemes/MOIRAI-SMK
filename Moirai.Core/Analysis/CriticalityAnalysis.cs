using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.Analysis;

public static class CriticalityAnalysis
{
    public sealed record CampaignCriticality(
        string CampaignId, string CampaignName, bool Completed,
        DateOnly? Finish, int? FloatDays, bool AtCap, bool IsCritical);

    public sealed record ResourceCriticality(
        string ResourceId, string Name, string Kind,
        int DeltaFinishDays, int NewlyIncomplete, bool BreaksPlan)
    {
        public bool IsCritical => BreaksPlan || DeltaFinishDays > 0;
    }

    public sealed record Result(
        DateOnly? BaselineFinish, int? BaselineSlackDays, bool BaselineComplete,
        IReadOnlyList<CampaignCriticality> Campaigns, IReadOnlyList<ResourceCriticality> Resources);

    public static Schedule GreedyRun(Scenario s) => new Scheduler().Run(s);

    public static Result Analyze(Scenario scenario, int maxFloatDays = 60,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => Analyze(scenario, GreedyRun, maxFloatDays, progress, ct);

    public static Result Analyze(Scenario scenario, Func<Scenario, Schedule> evaluate,
        int maxFloatDays = 60, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        maxFloatDays = Math.Max(1, maxFloatDays);

        var baseSchedule = evaluate(scenario);
        var baselineFinish = baseSchedule.ProgramFinish;
        bool baselineComplete = baseSchedule.AllCampaignsCompleted();
        int? baselineSlack = scenario.MasterDeadline is { } m && baselineFinish is { } f
            ? m.DayNumber - f.DayNumber
            : null;

        var firstSortie = baseSchedule.Sorties.Select(s => (s.CampaignId, s.Date))
            .Concat(baseSchedule.SimulatorSorties.Select(s => (s.CampaignId, s.Date)))
            .GroupBy(x => x.CampaignId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.Date));

        var campaigns = scenario.Campaigns.ToList();
        var resources = new List<(string Id, string Name, string Kind)>();
        resources.AddRange(scenario.Pilots.Select(p => (p.Id, p.Name, "Pilot")));
        resources.AddRange(scenario.Engineers.Select(e => (e.Id, e.Name, "Engineer")));
        resources.AddRange(scenario.Aircraft.Select(a => (a.Id, a.Name, "Aircraft")));
        resources.AddRange(scenario.Simulators.Select(s => (s.Id, s.Name.Length > 0 ? s.Name : s.Id, "Simulator")));

        if (baselineFinish is null)
            return new Result(null, baselineSlack, baselineComplete,
                campaigns.Select(c => new CampaignCriticality(c.Id, c.Name, false, null, null, false, true)).ToList(),
                new List<ResourceCriticality>());

        var campResults = new CampaignCriticality[campaigns.Count];
        var resResults = new ResourceCriticality[resources.Count];

        int total = campaigns.Count + resources.Count;
        int done = 0;
        void Tick() => progress?.Report((double)Interlocked.Increment(ref done) / Math.Max(1, total));

        var po = new ParallelOptions { CancellationToken = ct };

        Parallel.For(0, campaigns.Count, po, ci =>
        {
            var c = campaigns[ci];
            bool completed = baseSchedule.CampaignCompleted.GetValueOrDefault(c.Id);
            DateOnly? finish = baseSchedule.CampaignEndDates.GetValueOrDefault(c.Id);
            if (!completed)
            {
                campResults[ci] = new CampaignCriticality(c.Id, c.Name, false, null, null, false, true);
                Tick();
                return;
            }

            DateOnly anchor = firstSortie.TryGetValue(c.Id, out var fd) && fd > c.StartDate ? fd : c.StartDate;

            bool Feasible(int delay)
            {
                var clone = ScenarioJson.Clone(scenario);
                var cc = clone.Campaigns.First(x => x.Id == c.Id);
                cc.StartDate = anchor.AddDays(delay);
                var sch = evaluate(clone);
                return baseSchedule.CampaignCompleted.Where(x => x.Value)
                           .All(x => sch.CampaignCompleted.GetValueOrDefault(x.Key))
                       && sch.ProgramFinish is { } pf && pf <= baselineFinish.Value;
            }

            int floatDays = 0;
            while (floatDays < maxFloatDays && Feasible(floatDays + 1)) floatDays++;
            bool atCap = floatDays == maxFloatDays;
            campResults[ci] = new CampaignCriticality(
                c.Id, c.Name, true, finish, floatDays, atCap, IsCritical: floatDays == 0);
            Tick();
        });

        Parallel.For(0, resources.Count, po, ri =>
        {
            var (id, name, kind) = resources[ri];
            var clone = ScenarioJson.Clone(scenario);
            RemoveResource(clone, id, kind);
            var sch = evaluate(clone);

            int newlyIncomplete = baseSchedule.CampaignCompleted
                .Count(kv => kv.Value && !sch.CampaignCompleted.GetValueOrDefault(kv.Key));
            bool breaks = newlyIncomplete > 0;
            int delta = !breaks && sch.ProgramFinish is { } pf
                ? Math.Max(0, pf.DayNumber - baselineFinish.Value.DayNumber)
                : 0;
            resResults[ri] = new ResourceCriticality(id, name, kind, delta, newlyIncomplete, breaks);
            Tick();
        });

        return new Result(baselineFinish, baselineSlack, baselineComplete, campResults, resResults);
    }

    private static void RemoveResource(Scenario scenario, string id, string kind)
    {
        switch (kind)
        {
            case "Pilot":
                scenario.Pilots.RemoveAll(p => p.Id == id);
                foreach (var c in scenario.Campaigns) c.AssignedPilotIds.RemoveAll(x => x == id);
                break;
            case "Engineer":
                scenario.Engineers.RemoveAll(e => e.Id == id);
                foreach (var c in scenario.Campaigns) c.AssignedEngineerIds.RemoveAll(x => x == id);
                break;
            case "Aircraft":
                scenario.Aircraft.RemoveAll(a => a.Id == id);
                foreach (var c in scenario.Campaigns)
                {
                    c.CompatibleAircraftIds.RemoveAll(x => x == id);
                    c.SupportAircraftIds.RemoveAll(x => x == id);
                }
                break;
            case "Simulator":
                scenario.Simulators.RemoveAll(s => s.Id == id);
                break;
        }
    }
}
