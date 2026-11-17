using System.Text;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.Analysis;

public static class ResourceSuggester
{
    [Flags]
    public enum RecommenderLevers
    {
        None = 0,
        AddPilot = 1,
        AddEngineer = 2,
        AddAircraft = 4,
        ReassignPilot = 8,
        ReassignEngineer = 16,
        RaiseDailySorties = 32,
        AddBlock = 64,
        RaiseTestPoints = 128,
        ImproveRetest = 256,
        ReduceAborts = 512,
        AddSimulator = 1024,
        AddResources = AddPilot | AddEngineer | AddAircraft,
    }

    public sealed class Step
    {
        public required string Addition { get; init; }
        public required string Effect { get; init; }
    }

    public sealed record SweepProgress(string Phase, double Fraction);

    private sealed class CandidateProgress(
        IProgress<SweepProgress> outer, string phase, int index, int count, int runs) : IProgress<int>
    {
        public void Report(int done) =>
            outer.Report(new SweepProgress(phase, (index + (double)done / runs) / count));
    }

    public sealed class Result
    {
        public required List<Step> Steps { get; init; }
        public required bool AllMet { get; init; }
        public required string Summary { get; init; }
        public required Scenario FinalScenario { get; init; }

        public string ToText()
        {
            var sb = new StringBuilder();
            foreach (var s in Steps) sb.AppendLine($"{s.Addition}: {s.Effect}");
            sb.AppendLine(Summary);
            return sb.ToString();
        }
    }

    public static Result Suggest(Scenario scenario, int maxAdditions = 3)
    {
        var current = ScenarioJson.Clone(scenario);
        var steps = new List<Step>();

        var score = DeterministicScore(current, out var failing);
        for (int step = 1; step <= maxAdditions && failing.Count > 0; step++)
        {
            (Scenario Sc, string Label, (int Met, int NegLate, int Sorties) Score)? best = null;
            foreach (var (candidate, label) in Candidates(current, failing, step, RecommenderLevers.AddResources))
            {
                var s = DeterministicScore(candidate, out _);
                if (best is null || Lex(s).CompareTo(Lex(best.Value.Score)) > 0)
                    best = (candidate, label, s);
            }

            if (best is null
                || (best.Value.Score.Met, best.Value.Score.NegLate).CompareTo((score.Met, score.NegLate)) <= 0)
                break;

            var bestVal = best.Value;
            string effect = $"campaigns met {score.Met} → {bestVal.Score.Met}" +
                            (bestVal.Score.NegLate != score.NegLate
                                ? $", total lateness {-score.NegLate} → {-bestVal.Score.NegLate} days"
                                : "");
            steps.Add(new Step { Addition = bestVal.Label, Effect = effect });
            current = bestVal.Sc;
            score = DeterministicScore(current, out failing);
        }

        bool allMet = failing.Count == 0;
        return new Result
        {
            Steps = steps,
            AllMet = allMet,
            Summary = allMet
                ? $"All campaigns meet their deadlines after {steps.Count} addition(s)."
                : StructuralLimitText(current, failing),
            FinalScenario = current,
        };
    }

    private static (int Met, int NegLate, int Sorties) DeterministicScore(
        Scenario sc, out List<Campaign> failing)
    {
        var sch = new Scheduler().Run(sc);
        failing = sc.Campaigns.Where(c => sch.StatusOf(sc, c) != CampaignStatus.Met).ToList();

        int met = sc.Campaigns.Count - failing.Count;
        int late = 0;
        foreach (var c in sc.Campaigns)
        {
            var dl = CampaignDeadlines.EffectiveMilestone(sc, c) ?? DateOnly.MaxValue;
            if (sch.MeetsDeadline(c.Id, dl)) continue;
            if (sch.CampaignEndDates.TryGetValue(c.Id, out var end) && end.HasValue)
                late += Math.Max(0, end.Value.DayNumber - dl.DayNumber);
            else
                late += Math.Max(1, sc.HorizonEnd.DayNumber - dl.DayNumber + 1);
        }
        return (met, -late, sch.Sorties.Count);
    }

    private static (long, long, long) Lex((int Met, int NegLate, int Sorties) s) =>
        (s.Met, s.NegLate, s.Sorties);

    private static string StructuralLimitText(Scenario sc, List<Campaign> failing)
    {
        var sch = new Scheduler().Run(sc);
        var parts = failing.Select(c =>
        {
            var dom = sch.Bottlenecks.GetValueOrDefault(c.Id)?.Dominant();
            return dom is { } d ? $"{c.Name} (dominant bottleneck: {d.Reason}, {d.Days} days)" : c.Name;
        });
        return "Additional resources do not help · structural limit. Still failing: " +
               string.Join("; ", parts) + ".";
    }

    public static Result SuggestMc(
        Scenario scenario, int runs = 200, double targetConfidence = 0.90,
        int maxAdditions = 3, int seed = 12345,
        IProgress<SweepProgress>? progress = null, CancellationToken cancellation = default) =>
        RunMcSweep(scenario, RecommenderLevers.AddResources, runs, targetConfidence, maxAdditions, seed,
            progress, cancellation);

    public static Result Recommend(
        Scenario scenario, RecommenderLevers levers, int runs = 150,
        double targetConfidence = 0.90, int maxSteps = 4, int seed = 12345,
        IProgress<SweepProgress>? progress = null, CancellationToken cancellation = default) =>
        RunMcSweep(scenario, levers, runs, targetConfidence, maxSteps, seed, progress, cancellation);

    private static Result RunMcSweep(
        Scenario scenario, RecommenderLevers levers, int runs, double targetConfidence,
        int maxSteps, int seed, IProgress<SweepProgress>? progress, CancellationToken cancellation)
    {
        if (scenario.FlyableModel is not ProbabilisticFlyableDays prob)
            throw new InvalidOperationException(
                "The MC resource sweep requires a probabilistic flyable model.");

        var realizations = MonteCarlo.SampleRealizations(
            prob, scenario.HorizonStart, scenario.HorizonEnd, runs, seed);

        bool programMatters = scenario.MasterDeadline is not null;
        double Score(MonteCarlo.Result r) => TotalPMeet(r) + (programMatters ? r.PDelivery : 0);
        bool ProgramFailing(MonteCarlo.Result r) => programMatters && r.PDelivery < targetConfidence;

        var current = ScenarioJson.Clone(scenario);
        var steps = new List<Step>();

        var risk = MonteCarlo.Evaluate(current, realizations,
            progress is null ? null : new CandidateProgress(progress, "Evaluating baseline", 0, 1, runs),
            cancellation, seed);
        var failing = Failing(current, risk, targetConfidence);

        for (int step = 1; step <= maxSteps && (failing.Count > 0 || ProgramFailing(risk)); step++)
        {
            var moveTargets = failing.Count > 0 ? failing : current.Campaigns.ToList();

            var candidates = Candidates(current, moveTargets, step, levers).ToList();

            (Scenario Sc, string Label, MonteCarlo.Result Risk)? best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (candidate, label) = candidates[i];
                var r = MonteCarlo.Evaluate(candidate, realizations,
                    progress is null
                        ? null
                        : new CandidateProgress(progress,
                            $"Step {step}/{maxSteps} · candidate {i + 1}/{candidates.Count}",
                            i, candidates.Count, runs),
                    cancellation, seed);
                if (best is null || Score(r) > Score(best.Value.Risk))
                    best = (candidate, label, r);
            }

            if (best is null || Score(best.Value.Risk) <= Score(risk) + 1e-9)
                break;

            steps.Add(new Step
            {
                Addition = best.Value.Label,
                Effect = ConfidenceDelta(risk, best.Value.Risk, failing, programMatters),
            });
            current = best.Value.Sc;
            risk = best.Value.Risk;
            failing = Failing(current, risk, targetConfidence);
        }

        bool allConfident = failing.Count == 0 && !ProgramFailing(risk);
        var below = failing.Select(c => $"{c.Name} ({PMeetOf(risk, c.Id):P0})").ToList();
        if (ProgramFailing(risk)) below.Insert(0, $"PROGRAM/master deadline ({risk.PDelivery:P0})");

        return new Result
        {
            Steps = steps,
            AllMet = allConfident,
            Summary = allConfident
                ? $"All campaigns{(programMatters ? " and the program" : "")} reach ≥ {targetConfidence:P0} " +
                  $"deadline confidence after {steps.Count} addition(s)."
                : "Target confidence not reachable with the chosen levers. Still below target: " +
                  string.Join("; ", below) + ".",
            FinalScenario = current,
        };
    }

    private static double TotalPMeet(MonteCarlo.Result r) => r.Campaigns.Sum(c => c.PMeet);

    private static double PMeetOf(MonteCarlo.Result r, string campaignId) =>
        r.Campaigns.FirstOrDefault(c => c.CampaignId == campaignId)?.PMeet ?? 0;

    private static List<Campaign> Failing(Scenario sc, MonteCarlo.Result risk, double target) =>
        sc.Campaigns.Where(c => PMeetOf(risk, c.Id) < target).ToList();

    private static string ConfidenceDelta(
        MonteCarlo.Result before, MonteCarlo.Result after, List<Campaign> failing,
        bool programMatters)
    {
        var parts = new List<string>();
        if (programMatters)
            parts.Add($"program {before.PDelivery:P0} → {after.PDelivery:P0}");
        parts.AddRange(failing.Select(c =>
            $"{c.Name} {PMeetOf(before, c.Id):P0} → {PMeetOf(after, c.Id):P0}"));
        return string.Join(", ", parts);
    }

    private static IEnumerable<(Scenario Sc, string Label)> Candidates(
        Scenario current, List<Campaign> failing, int step, RecommenderLevers levers)
    {
        var failingIds = failing.Select(c => c.Id).ToHashSet();
        bool On(RecommenderLevers l) => (levers & l) != 0;

        if (On(RecommenderLevers.AddPilot))
        {
            var sc = ScenarioJson.Clone(current);
            var pilot = new Pilot { Id = $"suggest-pilot-{step}", Name = $"Extra Pilot {step}", ShortName = $"+P{step}" };
            sc.Pilots.Add(pilot);
            foreach (var c in sc.Campaigns.Where(c => failingIds.Contains(c.Id)))
                c.AssignedPilotIds.Add(pilot.Id);
            yield return (sc, $"+1 pilot ({pilot.Name})");
        }
        if (On(RecommenderLevers.AddEngineer))
        {
            var sc = ScenarioJson.Clone(current);
            var eng = new Engineer { Id = $"suggest-engineer-{step}", Name = $"Extra Engineer {step}", ShortName = $"+E{step}" };
            sc.Engineers.Add(eng);
            foreach (var c in sc.Campaigns.Where(c => failingIds.Contains(c.Id)))
                c.AssignedEngineerIds.Add(eng.Id);
            yield return (sc, $"+1 engineer ({eng.Name})");
        }
        if (On(RecommenderLevers.AddAircraft))
        {
            var sc = ScenarioJson.Clone(current);
            var ac = new Aircraft { Id = $"EXTRA-{step}", Name = $"Extra Aircraft {step}" };
            sc.Aircraft.Add(ac);
            foreach (var c in sc.Campaigns.Where(c => failingIds.Contains(c.Id)))
                c.CompatibleAircraftIds.Add(ac.Id);
            yield return (sc, $"+1 aircraft ({ac.Id})");
        }
        if (On(RecommenderLevers.AddSimulator))
        {
            var sc = ScenarioJson.Clone(current);
            var sim = new Simulator { Id = $"EXTRA-SIM-{step}", Name = $"Extra Simulator {step}", LocationId = "" };
            sc.Simulators.Add(sim);
            foreach (var c in sc.Campaigns.Where(c => failingIds.Contains(c.Id) && c.CompatibleSimulatorIds.Count > 0))
                c.CompatibleSimulatorIds.Add(sim.Id);
            yield return (sc, $"+1 simulator ({sim.Id})");
        }

        if (On(RecommenderLevers.ReassignPilot))
            foreach (var c in failing)
                foreach (var p in current.Pilots.Where(p => !c.AssignedPilotIds.Contains(p.Id)))
                {
                    var sc = ScenarioJson.Clone(current);
                    sc.Campaigns.First(x => x.Id == c.Id).AssignedPilotIds.Add(p.Id);
                    yield return (sc, $"assign pilot {p.Name} → {c.Name}");
                }
        if (On(RecommenderLevers.ReassignEngineer))
            foreach (var c in failing)
                foreach (var e in current.Engineers.Where(e => !c.AssignedEngineerIds.Contains(e.Id)))
                {
                    var sc = ScenarioJson.Clone(current);
                    sc.Campaigns.First(x => x.Id == c.Id).AssignedEngineerIds.Add(e.Id);
                    yield return (sc, $"assign engineer {e.Name} → {c.Name}");
                }

        if (On(RecommenderLevers.RaiseDailySorties))
        {
            var sc = ScenarioJson.Clone(current);
            int before = sc.Config.MaxSortiesPerPersonPerDay;
            sc.Config.MaxSortiesPerPersonPerDay = before + 1;
            yield return (sc, $"daily sorties/person {before} → {before + 1}");
        }
        if (On(RecommenderLevers.AddBlock))
        {
            var sc = ScenarioJson.Clone(current);
            int before = sc.Config.BlocksPerDay;
            sc.Config.BlocksPerDay = before + 1;
            yield return (sc, $"blocks/day {before} → {before + 1}");
        }

        if (On(RecommenderLevers.RaiseTestPoints))
            foreach (var c in failing)
            {
                var sc = ScenarioJson.Clone(current);
                var camp = sc.Campaigns.First(x => x.Id == c.Id);
                double before = camp.TestPointsPerSortie;
                camp.TestPointsPerSortie = before + 1;
                yield return (sc, $"test points/sortie of {c.Name} {before:0.##} → {before + 1:0.##}");
            }

        if (On(RecommenderLevers.ImproveRetest)
            && failing.Any(c => c.TestEfficiencyEnabled && c.RetestProbabilityStart > 0))
        {
            var sc = ScenarioJson.Clone(current);
            foreach (var c in sc.Campaigns.Where(c =>
                         failingIds.Contains(c.Id) && c.TestEfficiencyEnabled && c.RetestProbabilityStart > 0))
                c.RetestProbabilityStart *= 0.75;
            yield return (sc, "improve retest rate (−25% start) of failing campaigns");
        }
        if (On(RecommenderLevers.ReduceAborts))
        {
            if (current.Config.OverrideSortieAbort && current.Config.SortieAbortOverride > 0)
            {
                var sc = ScenarioJson.Clone(current);
                double before = sc.Config.SortieAbortOverride;
                sc.Config.SortieAbortOverride = before * 0.5;
                yield return (sc, $"halve the scenario abort override {before:0.##} → {before * 0.5:0.##}");
            }
            else if (failing.Any(c => c.TestEfficiencyEnabled && c.AbortProbability > 0))
            {
                var sc = ScenarioJson.Clone(current);
                foreach (var c in sc.Campaigns.Where(c =>
                             failingIds.Contains(c.Id) && c.TestEfficiencyEnabled && c.AbortProbability > 0))
                    c.AbortProbability *= 0.5;
                yield return (sc, "halve abort probability of failing campaigns");
            }
        }
    }
}
