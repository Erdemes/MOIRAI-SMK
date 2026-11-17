using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;

namespace MOIRAI.Analysis;

public static class Optimizer
{
    [Flags]
    public enum Levers
    {
        None = 0,
        Ordering = 1,
        Maintenance = 2,
        Leave = 4,
        CrewAssignment = 8,
        StartDates = 16,
        ExtraWorkdays = 32,
        AddPilots = 64,
        AddEngineers = 128,
        AddAircraft = 256,
        AddSimulators = 512,
        Resources = AddPilots | AddEngineers | AddAircraft,
        All = Ordering | Maintenance | Leave | CrewAssignment | StartDates | ExtraWorkdays | Resources,
    }

    public enum Method { Anneal = 0, Genetic = 1 }

    public sealed class Options
    {
        public Levers Levers { get; init; } = Levers.All;
        public Method Search { get; init; } = Method.Anneal;
        public int PopulationSize { get; init; } = 20;
        public int MaxAddedPilots { get; init; } = 2;
        public int MaxAddedEngineers { get; init; } = 2;
        public int MaxAddedAircraft { get; init; } = 2;
        public int MaxAddedSimulators { get; init; } = 2;
        public int MaxAdded(int type) => type switch { 0 => MaxAddedPilots, 1 => MaxAddedEngineers, 2 => MaxAddedAircraft, _ => MaxAddedSimulators };
        public double ResourceCostWeight { get; init; } = 0.15;
        public int Iterations { get; init; } = 600;
        public int SearchRuns { get; init; } = 40;
        public int FinalRuns { get; init; } = 300;
        public int MaxShiftDays { get; init; } = 14;
        public int MaxStartDelayDays { get; init; } = 14;
        public int MaxExtraWorkdays { get; init; } = 4;
        public double DisruptionWeight { get; init; } = 0.05;
        public int Seed { get; init; } = 12345;
        public double StartTemperature { get; init; } = 0.3;
        public double EndTemperature { get; init; } = 0.01;
    }

    public sealed class Change
    {
        public required string Kind { get; init; }
        public required string Description { get; init; }

        public required Action<Scenario> Apply { get; init; }
    }

    public sealed class FrontierPoint
    {
        public required int Additions { get; init; }
        public required string Description { get; init; }
        public required double Score { get; init; }
        public required double PDelivery { get; init; }
    }

    public sealed class Result
    {
        public required double BaselineScore { get; init; }
        public required double OptimizedScore { get; init; }
        public required double BaselinePDelivery { get; init; }
        public required double OptimizedPDelivery { get; init; }
        public required List<Change> Changes { get; init; }
        public required Scenario Optimized { get; init; }
        public required int Runs { get; init; }
        public required List<FrontierPoint> Frontier { get; init; }
        public bool Improved => OptimizedScore > BaselineScore + 1e-9;
    }

    public sealed record Progress(string Phase, double Fraction);

    public static Result Optimize(Scenario scenario, Options opts,
        IProgress<Progress>? progress = null, CancellationToken cancellation = default)
    {
        var rng = new Random(opts.Seed);
        var ctx = BuildContext(scenario, opts);

        var basePriority = BasePriority(scenario);
        var baseRank = Rank(basePriority);
        var state = State.Identity(basePriority, ctx);

        var frontier = new Dictionary<int, (double Score, State S)>();

        double Eval(State s, int runs)
        {
            cancellation.ThrowIfCancellationRequested();
            var work = Build(scenario, s, ctx);
            double pen = opts.DisruptionWeight * Disruption(s, baseRank, ctx)
                         + opts.ResourceCostWeight * s.ResAdd.Sum();
            double score = RobustScore(work, runs, opts.Seed, cancellation, out _, Math.Max(opts.SearchRuns, opts.FinalRuns)) - pen;
            int adds = s.ResAdd.Sum();
            if (!frontier.TryGetValue(adds, out var cur) || score > cur.Score)
                frontier[adds] = (score, s.Copy());
            return score;
        }

        double currentScore = Eval(state, opts.SearchRuns);
        var best = state.Copy();
        double bestScore = currentScore;

        int iters = ctx.AnyLever ? Math.Max(1, opts.Iterations) : 0;
        if (opts.Search == Method.Genetic && ctx.AnyLever)
        {
            RunGenetic(ref best, ref bestScore, state, currentScore, rng, ctx, opts, Eval, progress);
        }
        else
        {
            for (int it = 0; it < iters; it++)
            {
                double t = opts.StartTemperature *
                           Math.Pow(opts.EndTemperature / opts.StartTemperature, (double)it / iters);
                var candidate = Neighbor(state, rng, ctx);
                double score = Eval(candidate, opts.SearchRuns);

                double delta = score - currentScore;
                if (delta > 0 || rng.NextDouble() < Math.Exp(delta / Math.Max(1e-6, t)))
                {
                    state = candidate;
                    currentScore = score;
                }
                if (score > bestScore) { best = candidate.Copy(); bestScore = score; }

                if (progress is not null && (it % 5 == 0 || it == iters - 1))
                    progress.Report(new Progress("Searching", (double)(it + 1) / iters));
            }
        }

        progress?.Report(new Progress("Scoring", 1.0));
        var baseScenario = Build(scenario, State.Identity(basePriority, ctx), ctx);
        double baseScore = RobustScore(baseScenario, opts.FinalRuns, opts.Seed, cancellation, out double basePd, Math.Max(opts.SearchRuns, opts.FinalRuns));

        var winnerScenario = Build(scenario, best, ctx);
        double winScore = RobustScore(winnerScenario, opts.FinalRuns, opts.Seed, cancellation, out double winPd, Math.Max(opts.SearchRuns, opts.FinalRuns));

        bool keepWinner = winScore > baseScore + 1e-9
            && (scenario.MasterDeadline is null || winPd >= basePd);
        var changes = keepWinner ? DescribeChanges(scenario, best, ctx) : new List<Change>();

        return new Result
        {
            BaselineScore = baseScore,
            OptimizedScore = keepWinner ? winScore : baseScore,
            BaselinePDelivery = basePd,
            OptimizedPDelivery = keepWinner ? winPd : basePd,
            Changes = changes,
            Optimized = keepWinner ? winnerScenario : baseScenario,
            Runs = opts.FinalRuns,
            Frontier = BuildFrontier(scenario, frontier, ctx, opts, cancellation),
        };
    }

    private static void RunGenetic(ref State best, ref double bestScore,
        State seedState, double seedScore, Random rng, Context ctx, Options opts,
        Func<State, int, double> eval, IProgress<Progress>? progress)
    {
        int pop = Math.Max(6, opts.PopulationSize);
        int gens = Math.Max(1, opts.Iterations / pop);

        var population = new List<(State S, double Score)> { (seedState, seedScore) };
        for (int i = 1; i < pop; i++)
        {
            var s = Neighbor(Neighbor(seedState, rng, ctx), rng, ctx);
            population.Add((s, eval(s, opts.SearchRuns)));
        }

        for (int g = 0; g < gens; g++)
        {
            var ranked = population.OrderByDescending(x => x.Score).ToList();
            if (ranked[0].Score > bestScore) { best = ranked[0].S.Copy(); bestScore = ranked[0].Score; }

            var next = ranked.Take(2).ToList();
            while (next.Count < pop)
            {
                var pa = Tournament(population, rng);
                var pb = Tournament(population, rng);
                var child = Crossover(pa, pb, rng, ctx);
                if (rng.NextDouble() < 0.9) child = Neighbor(child, rng, ctx);
                next.Add((child, eval(child, opts.SearchRuns)));
            }
            population = next;
            progress?.Report(new Progress($"Generation {g + 1}/{gens}", (double)(g + 1) / gens));
        }

        var final = population.OrderByDescending(x => x.Score).First();
        if (final.Score > bestScore) { best = final.S.Copy(); bestScore = final.Score; }
    }

    private static State Tournament(List<(State S, double Score)> population, Random rng)
    {
        var a = population[rng.Next(population.Count)];
        var b = population[rng.Next(population.Count)];
        return (a.Score >= b.Score ? a : b).S;
    }

    private static State Crossover(State a, State b, Random rng, Context ctx)
    {
        var c = a.Copy();
        for (int i = 0; i < c.Priority.Length; i++) if (rng.Next(2) == 1) c.Priority[i] = b.Priority[i];
        for (int i = 0; i < c.MaintOffset.Length; i++) if (rng.Next(2) == 1) c.MaintOffset[i] = b.MaintOffset[i];
        for (int i = 0; i < c.LeaveOffset.Length; i++) if (rng.Next(2) == 1) c.LeaveOffset[i] = b.LeaveOffset[i];
        for (int i = 0; i < c.StartDelay.Length; i++) if (rng.Next(2) == 1) c.StartDelay[i] = b.StartDelay[i];
        for (int i = 0; i < c.CrewAdd.Length; i++) if (rng.Next(2) == 1) c.CrewAdd[i] = b.CrewAdd[i];
        for (int i = 0; i < c.ExtraDay.Length; i++) if (rng.Next(2) == 1) c.ExtraDay[i] = b.ExtraDay[i];
        for (int i = 0; i < c.ResAdd.Length; i++) if (rng.Next(2) == 1) c.ResAdd[i] = b.ResAdd[i];

        int cap = ctx.Opts.MaxExtraWorkdays;
        var on = Enumerable.Range(0, c.ExtraDay.Length).Where(x => c.ExtraDay[x]).ToList();
        while (on.Count > cap)
        {
            int drop = rng.Next(on.Count);
            c.ExtraDay[on[drop]] = false;
            on.RemoveAt(drop);
        }
        return c;
    }

    private static List<FrontierPoint> BuildFrontier(Scenario scenario,
        Dictionary<int, (double Score, State S)> frontier, Context ctx, Options opts,
        CancellationToken ct)
    {
        if (!ctx.AnyResource || frontier.Count < 2) return new List<FrontierPoint>();

        var points = new List<FrontierPoint>();
        foreach (var (adds, entry) in frontier.OrderBy(kv => kv.Key))
        {
            var work = Build(scenario, entry.S, ctx);
            double score = RobustScore(work, opts.FinalRuns, opts.Seed, ct, out double pd, Math.Max(opts.SearchRuns, opts.FinalRuns));
            points.Add(new FrontierPoint
            {
                Additions = adds,
                Description = DescribeResources(entry.S),
                Score = score,
                PDelivery = pd,
            });
        }
        return points;
    }

    private static string DescribeResources(State s)
    {
        var parts = new List<string>();
        if (s.ResAdd[0] > 0) parts.Add($"+{s.ResAdd[0]} pilot(s)");
        if (s.ResAdd[1] > 0) parts.Add($"+{s.ResAdd[1]} engineer(s)");
        if (s.ResAdd[2] > 0) parts.Add($"+{s.ResAdd[2]} aircraft");
        if (s.ResAdd[3] > 0) parts.Add($"+{s.ResAdd[3]} simulator(s)");
        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private static double RobustScore(Scenario work, int runs, int seed, CancellationToken ct, out double pDelivery, int probabilityResolution)
    {
        if (work.FlyableModel is ProbabilisticFlyableDays)
        {
            var r = MonteCarlo.Analyze(work, runs, seed, null, ct);
            pDelivery = r.PDelivery;
            double deliveryWeight = work.MasterDeadline.HasValue
                ? (work.Campaigns.Count + 1.0) * Math.Max(1, probabilityResolution) : 0;
            return deliveryWeight * r.PDelivery + r.Campaigns.Sum(c => c.PMeet);
        }

        var sch = new Scheduler().Run(work);
        var report = ProgramReporter.Build(work, sch);
        pDelivery = report.Delivered ? 1 : 0;
        int met = report.Campaigns.Count(c => c.Status == MilestoneStatus.Met);
        return met + (report.SlackDays is { } s ? Math.Tanh(s / 30.0) * 0.1 : 0);
    }

    private sealed record Context(
        bool OrderingOn,
        List<(int Ac, int W)> MaintTargets,
        List<(int P, int W)> LeaveTargets,
        List<(int Camp, int Person, bool IsPilot)> CrewCandidates,
        bool StartOn,
        List<DateOnly> ExtraDayCandidates,
        bool[] ResOn,
        Options Opts, string[][] ResourceIds)
    {
        public bool AnyResource => ResOn[0] || ResOn[1] || ResOn[2] || ResOn[3];
        public bool AnyLever => OrderingOn || StartOn || AnyResource || MaintTargets.Count > 0
            || LeaveTargets.Count > 0 || CrewCandidates.Count > 0 || ExtraDayCandidates.Count > 0;
    }

    private static Context BuildContext(Scenario sc, Options opts)
    {
        var maint = (opts.Levers.HasFlag(Levers.Maintenance)
            ? sc.Aircraft.SelectMany((a, ai) => a.MaintenanceWindows.Select((_, wi) => (ai, wi)))
            : Enumerable.Empty<(int, int)>()).ToList();
        var leave = (opts.Levers.HasFlag(Levers.Leave)
            ? People(sc).SelectMany((p, pi) => p.LeaveWindows.Select((_, wi) => (pi, wi)))
            : Enumerable.Empty<(int, int)>()).ToList();

        var crew = new List<(int, int, bool)>();
        if (opts.Levers.HasFlag(Levers.CrewAssignment))
        {
            var people = People(sc).ToList();
            for (int ci = 0; ci < sc.Campaigns.Count; ci++)
                for (int pi = 0; pi < people.Count; pi++)
                {
                    bool isPilot = people[pi] is Pilot;
                    var pool = isPilot ? sc.Campaigns[ci].AssignedPilotIds : sc.Campaigns[ci].AssignedEngineerIds;
                    if (!pool.Contains(people[pi].Id)) crew.Add((ci, pi, isPilot));
                }
        }

        var extra = new List<DateOnly>();
        if (opts.Levers.HasFlag(Levers.ExtraWorkdays) && opts.MaxExtraWorkdays > 0)
            for (var d = sc.HorizonStart; d <= sc.HorizonEnd; d = d.AddDays(1))
                if (!sc.Calendar.IsWorkday(d))
                    extra.Add(d);

        var resOn = new[]
        {
            opts.Levers.HasFlag(Levers.AddPilots) && opts.MaxAddedPilots > 0,
            opts.Levers.HasFlag(Levers.AddEngineers) && opts.MaxAddedEngineers > 0,
            opts.Levers.HasFlag(Levers.AddAircraft) && opts.MaxAddedAircraft > 0,
            opts.Levers.HasFlag(Levers.AddSimulators) && opts.MaxAddedSimulators > 0,
        };

        return new Context(
            OrderingOn: opts.Levers.HasFlag(Levers.Ordering) && sc.Campaigns.Count > 1,
            maint, leave, crew,
            StartOn: opts.Levers.HasFlag(Levers.StartDates) && sc.Campaigns.Count > 0,
            extra,
            resOn,
            opts, new[] {
                AllocateIds(sc.Pilots.Select(p => p.Id).Concat(sc.Engineers.Select(e => e.Id)), "opt-pilot-", opts.MaxAddedPilots),
                AllocateIds(sc.Pilots.Select(p => p.Id).Concat(sc.Engineers.Select(e => e.Id)), "opt-eng-", opts.MaxAddedEngineers),
                AllocateIds(sc.Aircraft.Select(a => a.Id), "OPT-AC", opts.MaxAddedAircraft),
                AllocateIds(sc.Simulators.Select(s => s.Id), "OPT-SIM", opts.MaxAddedSimulators)
            });
    }

    private sealed record State(
        double[] Priority, int[] MaintOffset, int[] LeaveOffset,
        int[] StartDelay, bool[] CrewAdd, bool[] ExtraDay,
        int[] ResAdd)
    {
        public static State Identity(double[] basePriority, Context ctx) => new(
            (double[])basePriority.Clone(),
            new int[ctx.MaintTargets.Count],
            new int[ctx.LeaveTargets.Count],
            new int[basePriority.Length],
            new bool[ctx.CrewCandidates.Count],
            new bool[ctx.ExtraDayCandidates.Count],
            new int[4]);

        public State Copy() => new(
            (double[])Priority.Clone(), (int[])MaintOffset.Clone(), (int[])LeaveOffset.Clone(),
            (int[])StartDelay.Clone(), (bool[])CrewAdd.Clone(), (bool[])ExtraDay.Clone(),
            (int[])ResAdd.Clone());
    }

    private static State Neighbor(State s, Random rng, Context ctx)
    {
        var c = s.Copy();
        var kinds = new List<int>();
        if (ctx.OrderingOn) kinds.Add(0);
        if (ctx.MaintTargets.Count > 0) kinds.Add(1);
        if (ctx.LeaveTargets.Count > 0) kinds.Add(2);
        if (ctx.StartOn) kinds.Add(3);
        if (ctx.CrewCandidates.Count > 0) kinds.Add(4);
        if (ctx.ExtraDayCandidates.Count > 0) kinds.Add(5);
        if (ctx.AnyResource) kinds.Add(6);
        if (kinds.Count == 0) return c;

        int maxShift = ctx.Opts.MaxShiftDays;
        switch (kinds[rng.Next(kinds.Count)])
        {
            case 0:
                int i = rng.Next(c.Priority.Length), j = rng.Next(c.Priority.Length);
                (c.Priority[i], c.Priority[j]) = (c.Priority[j], c.Priority[i]);
                break;
            case 1:
                int m = rng.Next(c.MaintOffset.Length);
                c.MaintOffset[m] = rng.NextDouble() < 0.2
                    ? rng.Next(-maxShift, maxShift + 1)
                    : Math.Clamp(c.MaintOffset[m] + rng.Next(-3, 4), -maxShift, maxShift);
                break;
            case 2:
                int l = rng.Next(c.LeaveOffset.Length);
                c.LeaveOffset[l] = rng.NextDouble() < 0.2
                    ? rng.Next(-maxShift, maxShift + 1)
                    : Math.Clamp(c.LeaveOffset[l] + rng.Next(-3, 4), -maxShift, maxShift);
                break;
            case 3:
                int sIdx = rng.Next(c.StartDelay.Length);
                c.StartDelay[sIdx] = Math.Clamp(c.StartDelay[sIdx] + rng.Next(-3, 4), 0, ctx.Opts.MaxStartDelayDays);
                break;
            case 4:
                int k = rng.Next(c.CrewAdd.Length);
                c.CrewAdd[k] = !c.CrewAdd[k];
                break;
            case 5:
                int e = rng.Next(c.ExtraDay.Length);
                if (c.ExtraDay[e]) { c.ExtraDay[e] = false; break; }
                int on = c.ExtraDay.Count(x => x);
                if (on >= ctx.Opts.MaxExtraWorkdays)
                {
                    var enabled = Enumerable.Range(0, c.ExtraDay.Length).Where(x => c.ExtraDay[x]).ToList();
                    c.ExtraDay[enabled[rng.Next(enabled.Count)]] = false;
                }
                c.ExtraDay[e] = true;
                break;
            default:
                var resTypes = new List<int>(4);
                for (int t = 0; t < 4; t++) if (ctx.ResOn[t]) resTypes.Add(t);
                int rType = resTypes[rng.Next(resTypes.Count)];
                c.ResAdd[rType] = Math.Clamp(c.ResAdd[rType] + (rng.Next(2) == 0 ? -1 : 1),
                    0, ctx.Opts.MaxAdded(rType));
                break;
        }
        return c;
    }

    private static int ClampShift(DateOnly start, int d, DateOnly? freeze)
    {
        if (d == 0 || freeze is not { } fz) return d;
        if (start < fz) return 0;
        if (start.AddDays(d) < fz) return fz.DayNumber - start.DayNumber;
        return d;
    }

    private static Scenario Build(Scenario original, State s, Context ctx)
    {
        var work = ScenarioJson.Clone(original);
        if (ctx.OrderingOn)
            for (int i = 0; i < work.Campaigns.Count; i++)
                work.Campaigns[i].PriorityKey = s.Priority[i];

        for (int k = 0; k < ctx.MaintTargets.Count; k++)
        {
            var w = work.Aircraft[ctx.MaintTargets[k].Ac].MaintenanceWindows[ctx.MaintTargets[k].W];
            int d = ClampShift(w.Start, s.MaintOffset[k], work.FreezeDate);
            if (d == 0) continue;
            w.Start = w.Start.AddDays(d);
            w.End = w.End.AddDays(d);
        }

        var people = People(work).ToList();
        for (int k = 0; k < ctx.LeaveTargets.Count; k++)
        {
            var w = people[ctx.LeaveTargets[k].P].LeaveWindows[ctx.LeaveTargets[k].W];
            int d = ClampShift(w.Start, s.LeaveOffset[k], work.FreezeDate);
            if (d == 0) continue;
            w.Start = w.Start.AddDays(d);
            w.End = w.End.AddDays(d);
        }

        if (ctx.StartOn)
            for (int i = 0; i < work.Campaigns.Count; i++)
            {
                int d = s.StartDelay[i];
                if (d > 0) work.Campaigns[i].StartDate = EffectiveStart(work, work.Campaigns[i]).AddDays(d);
            }

        for (int k = 0; k < ctx.CrewCandidates.Count; k++)
        {
            if (!s.CrewAdd[k]) continue;
            var (ci, pi, isPilot) = ctx.CrewCandidates[k];
            var camp = work.Campaigns[ci];
            (isPilot ? camp.AssignedPilotIds : camp.AssignedEngineerIds).Add(people[pi].Id);
        }

        for (int k = 0; k < ctx.ExtraDayCandidates.Count; k++)
            if (s.ExtraDay[k])
                work.Calendar.ExtraWorkdays.Add(ctx.ExtraDayCandidates[k]);

        if (ctx.AnyResource)
            AddNewResources(work, ctx.ResourceIds, s.ResAdd[0], s.ResAdd[1], s.ResAdd[2], s.ResAdd[3]);

        return work;
    }

    private static string[] AllocateIds(IEnumerable<string> existing, string prefix, int count)
    {
        var used = existing.ToHashSet();
        var ids = new List<string>();
        for (int i = 1; ids.Count < count; i++)
            if (used.Add(prefix + i)) ids.Add(prefix + i);
        return ids.ToArray();
    }

    private static void AddNewResources(Scenario scn, string[][] ids, int pilots, int engineers, int aircraft, int simulators = 0)
    {
        for (int k = 1; k <= pilots; k++)
        {
            var p = new Pilot { Id = ids[0][k - 1], Name = $"New pilot {k}" };
            if (scn.Pilots.Any(x => x.Id == p.Id)) continue;
            scn.Pilots.Add(p);
            foreach (var camp in scn.Campaigns) camp.AssignedPilotIds.Add(p.Id);
        }
        for (int k = 1; k <= engineers; k++)
        {
            var eng = new Engineer { Id = ids[1][k - 1], Name = $"New engineer {k}" };
            if (scn.Engineers.Any(x => x.Id == eng.Id)) continue;
            scn.Engineers.Add(eng);
            foreach (var camp in scn.Campaigns) camp.AssignedEngineerIds.Add(eng.Id);
        }
        for (int k = 1; k <= aircraft; k++)
        {
            var ac = new Aircraft { Id = ids[2][k - 1], Name = $"New aircraft {k}" };
            if (scn.Aircraft.Any(x => x.Id == ac.Id)) continue;
            scn.Aircraft.Add(ac);
            foreach (var camp in scn.Campaigns) camp.CompatibleAircraftIds.Add(ac.Id);
        }
        for (int k = 1; k <= simulators; k++)
        {
            var sim = new Simulator { Id = ids[3][k - 1], Name = $"New simulator {k}", LocationId = "" };
            if (scn.Simulators.Any(x => x.Id == sim.Id)) continue;
            scn.Simulators.Add(sim);
            foreach (var camp in scn.Campaigns.Where(c => c.CompatibleSimulatorIds.Count > 0))
                camp.CompatibleSimulatorIds.Add(sim.Id);
        }
    }

    private static DateOnly EffectiveStart(Scenario sc, Campaign c) =>
        c.StartDate < sc.HorizonStart ? sc.HorizonStart : c.StartDate;

    private static IEnumerable<Resource> People(Scenario sc) =>
        sc.Pilots.Cast<Resource>().Concat(sc.Engineers);

    private static double[] BasePriority(Scenario sc)
    {
        var ranks = DeadlineRanks(sc);
        var p = new double[sc.Campaigns.Count];
        for (int i = 0; i < p.Length; i++) p[i] = sc.Campaigns[i].PriorityKey ?? ranks[i];
        return p;
    }

    private static double[] DeadlineRanks(Scenario sc)
    {
        DateOnly Key(Campaign c) =>
            CampaignDeadlines.EffectiveTarget(sc, c) ?? DateOnly.MaxValue;
        var ordered = sc.Campaigns
            .Select((c, i) => (i, k: Key(c).DayNumber))
            .OrderBy(x => x.k).ToList();
        var ranks = new double[sc.Campaigns.Count];
        for (int r = 0; r < ordered.Count; r++) ranks[ordered[r].i] = r;
        return ranks;
    }

    private static double Disruption(State s, double[] baseRank, Context ctx)
    {
        int nC = s.Priority.Length;
        int maxShift = Math.Max(1, ctx.Opts.MaxShiftDays);
        double order = 0;
        if (nC > 1)
        {
            var rankOf = Rank(s.Priority);
            double sum = 0;
            for (int i = 0; i < nC; i++) sum += Math.Abs(rankOf[i] - baseRank[i]);
            order = sum / (nC * nC / 2.0);
        }
        double maint = s.MaintOffset.Length == 0 ? 0
            : (double)s.MaintOffset.Sum(Math.Abs) / (maxShift * s.MaintOffset.Length);
        double leave = s.LeaveOffset.Length == 0 ? 0
            : (double)s.LeaveOffset.Sum(Math.Abs) / (maxShift * s.LeaveOffset.Length);
        double start = !ctx.StartOn || nC == 0 ? 0
            : (double)s.StartDelay.Sum() / (Math.Max(1, ctx.Opts.MaxStartDelayDays) * nC);
        double crew = s.CrewAdd.Length == 0 ? 0
            : (double)s.CrewAdd.Count(x => x) / s.CrewAdd.Length;
        double extra = s.ExtraDay.Length == 0 ? 0
            : (double)s.ExtraDay.Count(x => x) / Math.Max(1, ctx.Opts.MaxExtraWorkdays);
        return order + maint + leave + start + crew + extra;
    }

    private static double[] Rank(double[] values)
    {
        var order = values.Select((v, i) => (v, i)).OrderBy(x => x.v).ToList();
        var rank = new double[values.Length];
        for (int r = 0; r < order.Count; r++) rank[order[r].i] = r;
        return rank;
    }

    private static List<Change> DescribeChanges(Scenario original, State s, Context ctx)
    {
        var changes = new List<Change>();

        if (ctx.OrderingOn)
        {
            var baseRank = Rank(BasePriority(original));
            var newRank = Rank(s.Priority);
            if (Enumerable.Range(0, original.Campaigns.Count).Any(i => (int)newRank[i] != (int)baseRank[i]))
            {
                var order = Enumerable.Range(0, original.Campaigns.Count)
                    .OrderBy(i => s.Priority[i])
                    .Select(i => original.Campaigns[i].Name);
                var priorities = original.Campaigns
                    .Select((c, i) => (c.Id, Key: s.Priority[i])).ToList();
                changes.Add(new Change
                {
                    Kind = "Ordering",
                    Description = "New campaign order: " + string.Join(" → ", order),
                    Apply = scn =>
                    {
                        foreach (var (id, key) in priorities)
                            if (scn.Campaigns.FirstOrDefault(x => x.Id == id) is { } c)
                                c.PriorityKey = key;
                    },
                });
            }
        }

        for (int k = 0; k < ctx.MaintTargets.Count; k++)
        {
            var a = original.Aircraft[ctx.MaintTargets[k].Ac];
            var w = a.MaintenanceWindows[ctx.MaintTargets[k].W];
            int d = ClampShift(w.Start, s.MaintOffset[k], original.FreezeDate);
            if (d == 0) continue;
            var (acId, winId, shift) = (a.Id, w.Id, d);
            changes.Add(new Change
            {
                Kind = "Maintenance",
                Description = $"{a.Id} maintenance moved {Math.Abs(d)} day(s) {(d > 0 ? "later" : "earlier")} " +
                              $"({w.Start:yyyy-MM-dd} → {w.Start.AddDays(d):yyyy-MM-dd})",
                Apply = scn =>
                {
                    var win = scn.Aircraft.FirstOrDefault(x => x.Id == acId)?
                        .MaintenanceWindows.FirstOrDefault(x => x.Id == winId);
                    if (win is null) return;
                    win.Start = win.Start.AddDays(shift);
                    win.End = win.End.AddDays(shift);
                },
            });
        }

        var people = People(original).ToList();
        for (int k = 0; k < ctx.LeaveTargets.Count; k++)
        {
            var p = people[ctx.LeaveTargets[k].P];
            var w = p.LeaveWindows[ctx.LeaveTargets[k].W];
            int d = ClampShift(w.Start, s.LeaveOffset[k], original.FreezeDate);
            if (d == 0) continue;
            var (personId, winId, shift) = (p.Id, w.Id, d);
            changes.Add(new Change
            {
                Kind = "Leave",
                Description = $"{p.Name} leave moved {Math.Abs(d)} day(s) {(d > 0 ? "later" : "earlier")} " +
                              $"({w.Start:yyyy-MM-dd} → {w.Start.AddDays(d):yyyy-MM-dd})",
                Apply = scn =>
                {
                    var win = People(scn).FirstOrDefault(x => x.Id == personId)?
                        .LeaveWindows.FirstOrDefault(x => x.Id == winId);
                    if (win is null) return;
                    win.Start = win.Start.AddDays(shift);
                    win.End = win.End.AddDays(shift);
                },
            });
        }

        if (ctx.StartOn)
            for (int i = 0; i < original.Campaigns.Count; i++)
            {
                int d = s.StartDelay[i];
                if (d == 0) continue;
                var eff = EffectiveStart(original, original.Campaigns[i]);
                var (campId, newStart) = (original.Campaigns[i].Id, eff.AddDays(d));
                changes.Add(new Change
                {
                    Kind = "Start date",
                    Description = $"{original.Campaigns[i].Name} start delayed {d} day(s) " +
                                  $"({eff:yyyy-MM-dd} → {eff.AddDays(d):yyyy-MM-dd})",
                    Apply = scn =>
                    {
                        if (scn.Campaigns.FirstOrDefault(x => x.Id == campId) is { } c)
                            c.StartDate = newStart;
                    },
                });
            }

        for (int k = 0; k < ctx.CrewCandidates.Count; k++)
        {
            if (!s.CrewAdd[k]) continue;
            var (ci, pi, isPilot) = ctx.CrewCandidates[k];
            var (campId, personId, pilot) = (original.Campaigns[ci].Id, people[pi].Id, isPilot);
            changes.Add(new Change
            {
                Kind = "Crew",
                Description = $"{people[pi].Name} ({(isPilot ? "pilot" : "engineer")}) added to " +
                              $"{original.Campaigns[ci].Name}'s crew pool",
                Apply = scn =>
                {
                    if (scn.Campaigns.FirstOrDefault(x => x.Id == campId) is not { } c) return;
                    var pool = pilot ? c.AssignedPilotIds : c.AssignedEngineerIds;
                    if (!pool.Contains(personId)) pool.Add(personId);
                },
            });
        }

        for (int k = 0; k < ctx.ExtraDayCandidates.Count; k++)
            if (s.ExtraDay[k])
            {
                var day = ctx.ExtraDayCandidates[k];
                changes.Add(new Change
                {
                    Kind = "Workday",
                    Description = $"Authorize {day:yyyy-MM-dd} ({day.DayOfWeek}) as a working day",
                    Apply = scn =>
                    {
                        if (!scn.Calendar.ExtraWorkdays.Contains(day))
                            scn.Calendar.ExtraWorkdays.Add(day);
                    },
                });
            }

        if (ctx.AnyResource && s.ResAdd.Sum() > 0)
        {
            var (addP, addE, addA, addS) = (s.ResAdd[0], s.ResAdd[1], s.ResAdd[2], s.ResAdd[3]);
            changes.Add(new Change
            {
                Kind = "Resource",
                Description = $"Add {DescribeResources(s)} (made available to every campaign)",
                Apply = scn => AddNewResources(scn, ctx.ResourceIds, addP, addE, addA, addS),
            });
        }

        return changes;
    }
}
