using System.Text;
using MOIRAI.Persistence;
using MOIRAI.Reporting;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;

namespace MOIRAI.Analysis;

public static class MonteCarlo
{
    [Flags]
    public enum UncertaintySources
    {
        Weather = 1,
        Yield = 2,
        Range = 4,
        Failures = 8,
        ExternalSupport = 16,
        All = Weather | Yield | Range | Failures | ExternalSupport,
    }

    public sealed class CampaignRisk
    {
        public required string CampaignId { get; init; }
        public required string CampaignName { get; init; }
        public double PMeet { get; init; }
        public double PIncomplete { get; init; }
        public DateOnly? P10 { get; init; }
        public DateOnly? P50 { get; init; }
        public DateOnly? P90 { get; init; }
        public int P10Run { get; init; } = -1;
        public int P50Run { get; init; } = -1;
        public int P90Run { get; init; } = -1;

        public int SortiesP10 { get; init; }
        public int SortiesP50 { get; init; }
        public int SortiesP90 { get; init; }

        public double AbortedMean { get; init; }
    }

    public sealed class Result
    {
        public required int Runs { get; init; }
        public required List<CampaignRisk> Campaigns { get; init; }

        public double PDelivery { get; init; }

        public double PProgramIncomplete { get; init; }

        public DateOnly? FinishP10 { get; init; }
        public DateOnly? FinishP50 { get; init; }
        public DateOnly? FinishP90 { get; init; }
        public int FinishP10Run { get; init; } = -1;
        public int FinishP50Run { get; init; } = -1;
        public int FinishP90Run { get; init; } = -1;

        public double SortiesMean { get; init; }
        public int SortiesP10 { get; init; }
        public int SortiesP50 { get; init; }
        public int SortiesP90 { get; init; }

        public double AbortedSortiesMean { get; init; }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Monte Carlo · {Runs} runs · P(delivery) = {PDelivery:P0}");
            foreach (var c in Campaigns)
                sb.AppendLine(
                    $"{c.CampaignName}: P(meet) = {c.PMeet:P0}, " +
                    $"P50 end = {(c.P50.HasValue ? c.P50.Value.ToString("yyyy-MM-dd") : "—")} " +
                    $"(P10 {(c.P10.HasValue ? c.P10.Value.ToString("yyyy-MM-dd") : "—")}, " +
                    $"P90 {(c.P90.HasValue ? c.P90.Value.ToString("yyyy-MM-dd") : "—")}), " +
                    $"P(incomplete) = {c.PIncomplete:P0}");
            return sb.ToString();
        }
    }

    public static List<ExplicitFlyableDays> SampleRealizations(
        ProbabilisticFlyableDays model, DateOnly start, DateOnly end, int runs, int seed)
    {
        var rng = new Random(seed);
        var list = new List<ExplicitFlyableDays>(runs);
        for (int k = 0; k < runs; k++) list.Add(model.Sample(start, end, rng));
        return list;
    }

    public static Result Analyze(Scenario scenario, int runs = 500, int seed = 12345,
        IProgress<int>? progress = null, CancellationToken cancellation = default,
        UncertaintySources sources = UncertaintySources.All)
        => Analyze(scenario, runs, seed, progress, cancellation, sources, FrozenOrder(scenario));

    public static Result Analyze(Scenario scenario, int runs, int seed,
        IProgress<int>? progress, CancellationToken cancellation,
        UncertaintySources sources, IReadOnlyList<string>? frozenOrder, int maxDop = -1)
    {
        if (scenario.FlyableModel is not ProbabilisticFlyableDays prob)
            throw new InvalidOperationException(
                "Monte Carlo requires the scenario's flyable model to be probabilistic.");

        bool weatherOn = sources.HasFlag(UncertaintySources.Weather);
        bool yieldOn = sources.HasFlag(UncertaintySources.Yield);
        bool rangeOn = sources.HasFlag(UncertaintySources.Range);
        bool failuresOn = sources.HasFlag(UncertaintySources.Failures);
        bool extOn = sources.HasFlag(UncertaintySources.ExternalSupport);
        double? abortOverride = scenario.Config.AbortOverrideOrNull;

        var nominalWeather = weatherOn ? null : NominalRealization(scenario, scenario.FlyableModel);
        var nominalLocW = weatherOn ? null : NominalLocationPart(scenario, range: false);
        var nominalLocR = rangeOn ? null : NominalLocationPart(scenario, range: true);
        var nominalExt = extOn ? null : NominalExternalPart(scenario);

        return EvaluateCore(scenario, runs,
            k => RunInputs(scenario, prob, seed, k, weatherOn, rangeOn, yieldOn, failuresOn, extOn,
                abortOverride, nominalWeather, nominalLocW, nominalLocR, nominalExt),
            progress, cancellation, frozenOrder, maxDop);
    }

    private readonly record struct LocRealization(ExplicitFlyableDays? Weather, ExplicitFlyableDays? Range);

    private static Dictionary<string, LocRealization>? BuildLocationEnv(
        Scenario scenario, Random weatherRng, Random rangeRng, bool weatherOn, bool rangeOn,
        Dictionary<string, ExplicitFlyableDays>? nominalWeather,
        Dictionary<string, ExplicitFlyableDays>? nominalRange)
    {
        Dictionary<string, LocRealization>? map = null;
        foreach (var loc in scenario.Locations)
        {
            var w = loc.FlyableModel is ProbabilisticFlyableDays p
                ? (weatherOn
                    ? p.Sample(scenario.HorizonStart, scenario.HorizonEnd, weatherRng)
                    : nominalWeather![loc.Id])
                : null;
            var r = loc.RangeModel is ProbabilisticFlyableDays q
                ? (rangeOn
                    ? q.Sample(scenario.HorizonStart, scenario.HorizonEnd, rangeRng)
                    : nominalRange![loc.Id])
                : null;
            if (w is not null || r is not null) (map ??= new())[loc.Id] = new(w, r);
        }
        foreach (var c in scenario.Campaigns)
            if (c.OwnFlyableModel() is ProbabilisticFlyableDays cp)
                (map ??= new())[c.Id] = new(weatherOn
                    ? cp.Sample(scenario.HorizonStart, scenario.HorizonEnd, weatherRng)
                    : nominalWeather![c.Id], null);
        return map;
    }

    private static void ApplyCampaignWeather(Scenario work, Dictionary<string, LocRealization> env)
    {
        foreach (var c in work.Campaigns)
            if (!c.UseGlobalFlyable && env.TryGetValue(c.Id, out var cw) && cw.Weather is not null)
                c.FlyableModel = cw.Weather;
    }

    private static Dictionary<string, LocRealization>? SampleLocationWeather(Scenario scenario, Random rng) =>
        BuildLocationEnv(scenario, rng, rng, weatherOn: true, rangeOn: true, null, null);

    private static Dictionary<string, ExplicitFlyableDays>? BuildExternalEnv(
        Scenario scenario, Random rng, bool extOn, Dictionary<string, ExplicitFlyableDays>? nominal)
    {
        Dictionary<string, ExplicitFlyableDays>? map = null;
        foreach (var a in scenario.ExternalSupports)
            if (a.AvailabilityModel is ProbabilisticFlyableDays p)
                (map ??= new())[a.Id] = extOn
                    ? p.Sample(scenario.HorizonStart, scenario.HorizonEnd, rng)
                    : nominal![a.Id];
        return map;
    }

    private static Dictionary<string, ExplicitFlyableDays>? NominalExternalPart(Scenario scenario)
    {
        Dictionary<string, ExplicitFlyableDays>? map = null;
        foreach (var a in scenario.ExternalSupports)
            if (a.AvailabilityModel is ProbabilisticFlyableDays p)
                (map ??= new())[a.Id] = NominalRealization(scenario, p);
        return map;
    }

    private static Dictionary<string, ExplicitFlyableDays>? NominalLocationPart(Scenario scenario, bool range)
    {
        Dictionary<string, ExplicitFlyableDays>? map = null;
        foreach (var loc in scenario.Locations)
        {
            var model = range ? loc.RangeModel : loc.FlyableModel;
            if (model is ProbabilisticFlyableDays p)
                (map ??= new())[loc.Id] = NominalRealization(scenario, p);
        }
        if (!range)
            foreach (var c in scenario.Campaigns)
                if (c.OwnFlyableModel() is ProbabilisticFlyableDays cp)
                    (map ??= new())[c.Id] = NominalRealization(scenario, cp);
        return map;
    }

    private static ExplicitFlyableDays NominalRealization(Scenario scenario, FlyableDayModel model)
    {
        var days = new HashSet<DateOnly>();
        for (var d = scenario.HorizonStart; d <= scenario.HorizonEnd; d = d.AddDays(1))
            if (model.IsFlyable(d))
                days.Add(d);
        return new ExplicitFlyableDays { Days = days };
    }

    public static Result Evaluate(Scenario scenario, IReadOnlyList<ExplicitFlyableDays> realizations,
        IProgress<int>? progress = null, CancellationToken cancellation = default, int seed = 12345)
    {
        double? abortOverride = scenario.Config.AbortOverrideOrNull;
        return EvaluateCore(scenario, realizations.Count,
            k =>
            {
                var locWeather = SampleLocationWeather(scenario, new Random(MixSeed(seed + 211, k)));
                var extAvail = BuildExternalEnv(scenario,
                    new Random(MixSeed(seed + ExternalSalt, k)), extOn: true, nominal: null);
                var yield = (ISortieYieldModel)new StochasticYieldModel(
                    new Random(MixSeed(seed + 7919, k)), abortOverride);
                var failures = (IFailureModel)new StochasticFailureModel(
                    new Random(MixSeed(seed + 104729, k)));
                return (realizations[k], locWeather, extAvail, yield, failures);
            },
            progress, cancellation, FrozenOrder(scenario));
    }

    private static Result EvaluateCore(Scenario scenario, int n,
        Func<int, (ExplicitFlyableDays Weather, Dictionary<string, LocRealization>? LocWeather,
            Dictionary<string, ExplicitFlyableDays>? ExtAvail,
            ISortieYieldModel Yield, IFailureModel Failures)> runInputs,
        IProgress<int>? progress, CancellationToken cancellation, IReadOnlyList<string>? frozenOrder,
        int maxDop = -1)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), n, "Monte Carlo needs at least one run.");

        var campaigns = scenario.Campaigns;
        int nC = campaigns.Count;

        var ends = new int[nC][];
        var meet = new bool[nC][];
        for (int c = 0; c < nC; c++) { ends[c] = new int[n]; meet[c] = new bool[n]; }
        var delivered = new bool[n];
        var progEnd = new int[n];
        var sorties = new int[n];
        var abortedSorties = new int[n];

        var campSorties = new int[nC][];
        var campAborted = new int[nC][];
        for (int c = 0; c < nC; c++) { campSorties[c] = new int[n]; campAborted[c] = new int[n]; }
        var campIndex = new Dictionary<string, int>(nC);
        for (int c = 0; c < nC; c++) campIndex[campaigns[c].Id] = c;

        int done = 0;
        int step = Math.Max(1, n / 200);

        bool greedyEngine = PlanUsesGreedy(scenario, frozenOrder);
        Parallel.For(0, n,
            new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = maxDop },
            () =>
            {
                var work = ScenarioJson.Clone(scenario);
                work.Config.SimulateYieldInNominal = false;
                ApplyFrozenOrder(work, frozenOrder);
                return work;
            },
            (k, _, work) =>
            {
                var (weather, locWeather, extAvail, yield, failures) = runInputs(k);
                work.FlyableModel = weather;
                if (locWeather is not null)
                    foreach (var loc in work.Locations)
                        if (locWeather.TryGetValue(loc.Id, out var lw))
                        {
                            if (lw.Weather is not null) loc.FlyableModel = lw.Weather;
                            if (lw.Range is not null) loc.RangeModel = lw.Range;
                        }
                if (locWeather is not null) ApplyCampaignWeather(work, locWeather);
                if (extAvail is not null)
                    foreach (var a in work.ExternalSupports)
                        if (extAvail.TryGetValue(a.Id, out var av))
                            a.AvailabilityModel = av;
                var sch = NewMcEngine(greedyEngine).Run(work, yield, failures);
                for (int c = 0; c < nC; c++)
                {
                    var camp = work.Campaigns[c];
                    meet[c][k] = sch.MeetsDeadline(camp.Id,
                        CampaignDeadlines.EffectiveMilestone(work, camp) ?? DateOnly.MaxValue);
                    ends[c][k] =
                        sch.CampaignCompleted.GetValueOrDefault(camp.Id)
                        && sch.CampaignEndDates.TryGetValue(camp.Id, out var end) && end.HasValue
                            ? end.Value.DayNumber
                            : -1;
                }
                delivered[k] = ProgramReporter.IsDelivered(work, sch);
                progEnd[k] = sch.AllCampaignsCompleted() && sch.ProgramFinish is { } mk
                    ? mk.DayNumber
                    : -1;
                sorties[k] = sch.Sorties.Count + sch.SimulatorSorties.Count;
                abortedSorties[k] = sch.Sorties.Count(s => s.Aborted) + sch.SimulatorSorties.Count(s => s.Aborted);
                foreach (var s in sch.Sorties)
                    if (campIndex.TryGetValue(s.CampaignId, out int ci))
                    {
                        campSorties[ci][k]++;
                        if (s.Aborted) campAborted[ci][k]++;
                    }
                foreach (var s in sch.SimulatorSorties)
                    if (campIndex.TryGetValue(s.CampaignId, out int ci))
                    {
                        campSorties[ci][k]++;
                        if (s.Aborted) campAborted[ci][k]++;
                    }

                int d = Interlocked.Increment(ref done);
                if (progress is not null && (d % step == 0 || d == n)) progress.Report(d);
                return work;
            },
            _ => { });

        var rows = new List<CampaignRisk>(nC);
        for (int c = 0; c < nC; c++)
        {
            var (p10, p10Run) = PercentileRun(ends[c], 0.10);
            var (p50, p50Run) = PercentileRun(ends[c], 0.50);
            var (p90, p90Run) = PercentileRun(ends[c], 0.90);
            var (s10, s50, s90) = IntPercentiles(campSorties[c]);
            rows.Add(new CampaignRisk
            {
                CampaignId = campaigns[c].Id,
                CampaignName = campaigns[c].Name,
                PMeet = (double)meet[c].Count(m => m) / n,
                PIncomplete = (double)ends[c].Count(e => e < 0) / n,
                P10 = p10, P50 = p50, P90 = p90,
                P10Run = p10Run, P50Run = p50Run, P90Run = p90Run,
                SortiesP10 = s10, SortiesP50 = s50, SortiesP90 = s90,
                AbortedMean = campAborted[c].Average(),
            });
        }

        var (m10, m10Run) = PercentileRun(progEnd, 0.10);
        var (m50, m50Run) = PercentileRun(progEnd, 0.50);
        var (m90, m90Run) = PercentileRun(progEnd, 0.90);
        var (totS10, totS50, totS90) = IntPercentiles(sorties);

        return new Result
        {
            Runs = n,
            Campaigns = rows,
            PDelivery = (double)delivered.Count(d => d) / n,
            PProgramIncomplete = (double)progEnd.Count(e => e < 0) / n,
            FinishP10 = m10, FinishP50 = m50, FinishP90 = m90,
            FinishP10Run = m10Run, FinishP50Run = m50Run, FinishP90Run = m90Run,
            SortiesMean = sorties.Average(),
            SortiesP10 = totS10, SortiesP50 = totS50, SortiesP90 = totS90,
            AbortedSortiesMean = abortedSorties.Average(),
        };
    }

    private static (int P10, int P50, int P90) IntPercentiles(int[] values)
    {
        var sorted = (int[])values.Clone();
        Array.Sort(sorted);
        int At(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
        return (At(0.10), At(0.50), At(0.90));
    }

    public static (Scenario Scenario, Schedule Schedule) ReplayRun(
        Scenario scenario, int runIndex, int seed = 12345) =>
        ReplayRun(scenario, runIndex, seed, FrozenOrder(scenario));

    private static (Scenario Scenario, Schedule Schedule) ReplayRun(
        Scenario scenario, int runIndex, int seed, List<string>? frozenOrder)
    {
        if (scenario.FlyableModel is not ProbabilisticFlyableDays prob)
            throw new InvalidOperationException(
                "Monte Carlo requires the scenario's flyable model to be probabilistic.");

        var (weather, locWeather, extAvail, yield, failures) = RunInputs(
            scenario, prob, seed, runIndex,
            weatherOn: true, rangeOn: true, yieldOn: true, failuresOn: true, extOn: true,
            scenario.Config.AbortOverrideOrNull, null, null, null, null);
        var work = ScenarioJson.Clone(scenario);
        work.Config.SimulateYieldInNominal = false;
        ApplyFrozenOrder(work, frozenOrder);
        work.FlyableModel = weather;
        if (locWeather is not null)
            foreach (var loc in work.Locations)
                if (locWeather.TryGetValue(loc.Id, out var lw))
                {
                    if (lw.Weather is not null) loc.FlyableModel = lw.Weather;
                    if (lw.Range is not null) loc.RangeModel = lw.Range;
                }
        if (locWeather is not null) ApplyCampaignWeather(work, locWeather);
        if (extAvail is not null)
            foreach (var a in work.ExternalSupports)
                if (extAvail.TryGetValue(a.Id, out var av))
                    a.AvailabilityModel = av;
        return (work, NewMcEngine(PlanUsesGreedy(scenario, frozenOrder)).Run(work, yield, failures));
    }

    public static List<(string Label, Scenario Scenario, Schedule Schedule)> ReplayFinishPercentiles(
        Scenario scenario, Result mc, int seed = 12345)
    {
        var result = new List<(string, Scenario, Schedule)>();
        if (scenario.FlyableModel is not ProbabilisticFlyableDays) return result;
        var order = FrozenOrder(scenario);
        var byRun = new Dictionary<int, (Scenario Scenario, Schedule Schedule)>();
        foreach (var (label, runIndex) in new[]
                 {
                     ("P10", mc.FinishP10Run), ("P50", mc.FinishP50Run), ("P90", mc.FinishP90Run),
                 })
        {
            if (runIndex < 0) continue;
            if (!byRun.TryGetValue(runIndex, out var replay))
                byRun[runIndex] = replay = ReplayRun(scenario, runIndex, seed, order);
            result.Add((label, replay.Scenario, replay.Schedule));
        }
        return result;
    }

    private static ISchedulerService NewMcEngine(bool greedy) =>
        greedy ? new Scheduler() : new RevisingScheduler(McOptions);

    private static readonly RevisingOptions McOptions = new()
    {
        EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
        EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = false,
    };

    internal static bool PlanUsesGreedy(Scenario scenario, IReadOnlyList<string>? frozenOrder)
    {
        var work = ScenarioJson.Clone(scenario);
        work.Config.SimulateYieldInNominal = false;
        ApplyFrozenOrder(work, frozenOrder);
        var core = new CoreForwardScheduler(McOptions);
        core.Run(work);
        return core.FlooredToGreedy;
    }

    public static List<string>? FrozenOrder(Scenario scenario)
    {
        if (!scenario.Config.McUseAdvancedEngine || scenario.Campaigns.Count < 2) return null;
        var nominal = ScenarioJson.Clone(scenario);
        nominal.Config.SimulateYieldInNominal = false;
        var sch = new RevisingScheduler(new RevisingOptions
        {
            EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
            EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
        }).Run(nominal);
        return sch.PlannedOrder;
    }

    private static void ApplyFrozenOrder(Scenario work, IReadOnlyList<string>? order)
    {
        if (order is null) return;
        var rank = new Dictionary<string, int>(order.Count);
        for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
        foreach (var c in work.Campaigns)
            if (rank.TryGetValue(c.Id, out int r)) c.PriorityKey = r;
    }

    private static int MixSeed(int seed, int k)
    {
        unchecked
        {
            uint z = (uint)seed + (uint)k * 0x9E3779B9u;
            z ^= z >> 16; z *= 0x85EBCA6Bu;
            z ^= z >> 13; z *= 0xC2B2AE35u;
            z ^= z >> 16;
            return (int)z;
        }
    }

    private const int LocWeatherSalt = 211, RangeSalt = 433, YieldSalt = 7919, FailureSalt = 104729,
        ExternalSalt = 1889;

    private static (ExplicitFlyableDays Weather, Dictionary<string, LocRealization>? LocWeather,
        Dictionary<string, ExplicitFlyableDays>? ExtAvail,
        ISortieYieldModel Yield, IFailureModel Failures) RunInputs(
        Scenario scenario, ProbabilisticFlyableDays prob, int seed, int k,
        bool weatherOn, bool rangeOn, bool yieldOn, bool failuresOn, bool extOn, double? abortOverride,
        ExplicitFlyableDays? nominalWeather,
        Dictionary<string, ExplicitFlyableDays>? nominalLocW,
        Dictionary<string, ExplicitFlyableDays>? nominalLocR,
        Dictionary<string, ExplicitFlyableDays>? nominalExt)
    {
        var weather = weatherOn
            ? prob.Sample(scenario.HorizonStart, scenario.HorizonEnd, new Random(MixSeed(seed, k)))
            : nominalWeather!;
        var locEnv = BuildLocationEnv(scenario,
            new Random(MixSeed(seed + LocWeatherSalt, k)),
            new Random(MixSeed(seed + RangeSalt, k)),
            weatherOn, rangeOn, nominalLocW, nominalLocR);
        var extEnv = BuildExternalEnv(scenario,
            new Random(MixSeed(seed + ExternalSalt, k)), extOn, nominalExt);
        var yield = yieldOn
            ? (ISortieYieldModel)new StochasticYieldModel(new Random(MixSeed(seed + YieldSalt, k)), abortOverride)
            : LosslessYieldModel.Instance;
        var failures = failuresOn
            ? (IFailureModel)new StochasticFailureModel(new Random(MixSeed(seed + FailureSalt, k)))
            : NoFailuresModel.Instance;
        return (weather, locEnv, extEnv, yield, failures);
    }

    private static (DateOnly? Date, int Run) PercentileRun(int[] perRun, double q)
    {
        var valid = new List<(int Day, int Run)>();
        for (int k = 0; k < perRun.Length; k++)
            if (perRun[k] >= 0) valid.Add((perRun[k], k));
        if (valid.Count == 0) return (null, -1);

        valid.Sort((a, b) => a.Day.CompareTo(b.Day));
        int idx = Math.Clamp((int)Math.Ceiling(q * valid.Count) - 1, 0, valid.Count - 1);
        return (DateOnly.FromDayNumber(valid[idx].Day), valid[idx].Run);
    }

    public sealed class SensitivityFactor
    {
        public required string Name { get; init; }
        public required string LowLabel { get; init; }
        public required string HighLabel { get; init; }
        public required double LowPDelivery { get; init; }
        public required double HighPDelivery { get; init; }
        public double Swing => Math.Abs(HighPDelivery - LowPDelivery);

        public double LowPDeliveryLo { get; init; }
        public double LowPDeliveryHi { get; init; }
        public double HighPDeliveryLo { get; init; }
        public double HighPDeliveryHi { get; init; }
        public bool HasBand { get; init; }
    }

    public sealed class SweepPoint
    {
        public required double Value { get; init; }
        public string Label { get; init; } = "";
        public required double PDelivery { get; init; }
        public DateOnly? FinishP50 { get; init; }

        public double PDeliveryLo { get; init; }
        public double PDeliveryHi { get; init; }
        public bool HasBand { get; init; }
    }

    public sealed class ParameterSweep
    {
        public required string Name { get; init; }
        public required List<SweepPoint> Points { get; init; }
    }

    public sealed class SobolFactor
    {
        public required string Name { get; init; }
        public required double DirectShare { get; init; }
        public required double TotalShare { get; init; }
        public double InteractionShare => Math.Max(0, TotalShare - DirectShare);
    }

    public sealed class SensitivityResult
    {
        public required int Runs { get; init; }
        public required double BaselinePDelivery { get; init; }
        public required List<SensitivityFactor> Factors { get; init; }
        public required List<ParameterSweep> Sweeps { get; init; }
        public required List<SweepPoint> PersistenceSweep { get; init; }

        public required List<SobolFactor> GlobalImportance { get; init; }
        public int GlobalEvaluations { get; init; }
        public double JointInteractionShare =>
            HasGlobalVariation ? Math.Clamp(1 - GlobalImportance.Sum(f => f.DirectShare), 0, 1) : 0;

        public bool HasGlobalVariation { get; init; }
    }

    private sealed record FactorDef(
        string Name, string LowLabel, string HighLabel, Action<Scenario> Low, Action<Scenario> High);

    private sealed record SweepDef(string Name, double[] Values, Func<double, string> Label, Action<Scenario, double> Apply);

    private static string ScaleLabel(double f) =>
        Math.Abs(f - 1.0) < 1e-9 ? "base" : (f > 1 ? "+" : "−") + $"{Math.Abs(f - 1) * 100:0}%";

    public static SensitivityResult Sensitivity(Scenario scenario, int runs = 150, int seed = 12345,
        IProgress<(string Phase, double Fraction)>? progress = null, CancellationToken cancellation = default,
        int sobolSamples = 256, bool orderSpread = false)
    {
        if (scenario.FlyableModel is not ProbabilisticFlyableDays prob)
            throw new InvalidOperationException(
                "Sensitivity analysis requires the scenario's flyable model to be probabilistic.");

        var frozenOrder = FrozenOrder(scenario);

        IReadOnlyList<IReadOnlyList<string>?> orders = orderSpread && frozenOrder is not null
            ? DerivePercentileOrders(scenario, runs, seed, cancellation)
            : new IReadOnlyList<string>?[] { frozenOrder };
        int centralIdx = orders.Count == 3 ? 1 : 0;
        var sobolOrder = orders[centralIdx];
        bool hasBand = orders.Count > 1;

        bool efficiencyActive =
            scenario.Campaigns.Any(c => c.TestEfficiencyEnabled) || scenario.Config.OverrideSortieAbort;
        bool retestActive = scenario.Campaigns.Any(c => c.TestEfficiencyEnabled);

        bool rangeActive = RangeModels(scenario).Any();
        bool externalActive = ExternalModels(scenario).Any();
        bool failureActive = scenario.Aircraft.Any(a => a.FailuresPer100FlightHours > 0)
            || scenario.Simulators.Any(s => s.FailuresPer100Hours > 0);

        double phi = prob.Persistence;
        double phiLo = Math.Clamp(phi - 0.2, 0, 0.98);
        double phiHi = Math.Clamp(phi + 0.2, 0, 0.98);

        var defs = new List<FactorDef>
        {
            new("Weather persistence φ", $"φ={phiLo:0.##}", $"φ={phiHi:0.##}",
                s => SetPhi(s, phiLo), s => SetPhi(s, phiHi)),
            new("Weather flyable rate", "−15%", "+15%",
                s => ScaleRates(s, 0.85), s => ScaleRates(s, 1.15)),
        };
        if (rangeActive)
            defs.Add(new FactorDef("Range availability", "−15%", "+15%",
                s => ScaleRangeRates(s, 0.85), s => ScaleRangeRates(s, 1.15)));
        if (externalActive)
            defs.Add(new FactorDef("External support availability", "−15%", "+15%",
                s => ScaleExternalRates(s, 0.85), s => ScaleExternalRates(s, 1.15)));
        if (efficiencyActive)
            defs.Add(new FactorDef("Abort probability", "−50%", "+50%",
                s => ScaleAbort(s, 0.5), s => ScaleAbort(s, 1.5)));
        if (retestActive)
            defs.Add(new FactorDef("Retest probability", "−25%", "+25%",
                s => ScaleRetest(s, 0.75), s => ScaleRetest(s, 1.25)));
        if (failureActive)
            defs.Add(new FactorDef("Failure rate (aircraft + sim)", "−50%", "+50%",
                s => ScaleFailureRates(s, 0.5), s => ScaleFailureRates(s, 1.5)));

        var phiSweep = new[] { 0.2, 0.5, 0.8, 0.95 };
        var rateSweep = new[] { 0.7, 0.85, 1.0, 1.15, 1.3 };
        var abortSweep = new[] { 0.5, 0.75, 1.0, 1.5, 2.0 };
        var retestSweep = new[] { 0.5, 0.75, 1.0, 1.25, 1.5 };
        var failSweep = new[] { 0.5, 0.75, 1.0, 1.5, 2.0 };

        var sweepDefs = new List<SweepDef>
        {
            new("Weather persistence φ", phiSweep, v => v.ToString("0.##"), SetPhi),
            new("Weather flyable rate", rateSweep, ScaleLabel, ScaleRates),
        };
        if (rangeActive)
            sweepDefs.Add(new("Range availability", rateSweep, ScaleLabel, ScaleRangeRates));
        if (externalActive)
            sweepDefs.Add(new("External support availability", rateSweep, ScaleLabel, ScaleExternalRates));
        if (efficiencyActive)
            sweepDefs.Add(new("Abort probability", abortSweep, ScaleLabel, ScaleAbort));
        if (retestActive)
            sweepDefs.Add(new("Retest probability", retestSweep, ScaleLabel, ScaleRetest));
        if (failureActive)
            sweepDefs.Add(new("Failure rate (aircraft + sim)", failSweep, ScaleLabel, ScaleFailureRates));

        int baseN = Math.Max(16, sobolSamples);
        int sobolEvals = baseN * (sweepDefs.Count + 2);
        int total = 1 + defs.Count * 2 + sweepDefs.Sum(d => d.Values.Length) + sobolEvals;
        int done = 0;

        (Result Central, double Lo, double Hi) Eval(Action<Scenario>? mutate, string phase)
        {
            cancellation.ThrowIfCancellationRequested();
            var work = ScenarioJson.Clone(scenario);
            mutate?.Invoke(work);
            var results = new Result[orders.Count];
            for (int o = 0; o < orders.Count; o++)
                results[o] = Analyze(work, runs, seed, null, cancellation, UncertaintySources.All, orders[o]);
            progress?.Report((phase, (double)++done / total));
            double lo = results[0].PDelivery, hi = lo;
            foreach (var r in results) { lo = Math.Min(lo, r.PDelivery); hi = Math.Max(hi, r.PDelivery); }
            return (results[centralIdx], lo, hi);
        }

        double baseline = Eval(null, "Baseline").Central.PDelivery;

        var factors = new List<SensitivityFactor>();
        foreach (var d in defs)
        {
            var (lowC, lowLo, lowHi) = Eval(d.Low, $"{d.Name} (low)");
            var (highC, highLo, highHi) = Eval(d.High, $"{d.Name} (high)");
            factors.Add(new SensitivityFactor
            {
                Name = d.Name, LowLabel = d.LowLabel, HighLabel = d.HighLabel,
                LowPDelivery = lowC.PDelivery, HighPDelivery = highC.PDelivery,
                LowPDeliveryLo = lowLo, LowPDeliveryHi = lowHi,
                HighPDeliveryLo = highLo, HighPDeliveryHi = highHi,
                HasBand = hasBand,
            });
        }
        factors = factors.OrderByDescending(f => f.Swing).ToList();

        var sweeps = new List<ParameterSweep>();
        foreach (var sd in sweepDefs)
        {
            var points = new List<SweepPoint>();
            foreach (double v in sd.Values)
            {
                var (c, lo, hi) = Eval(w => sd.Apply(w, v), $"{sd.Name} sweep ({sd.Label(v)})");
                points.Add(new SweepPoint
                {
                    Value = v, Label = sd.Label(v), PDelivery = c.PDelivery, FinishP50 = c.FinishP50,
                    PDeliveryLo = lo, PDeliveryHi = hi, HasBand = hasBand,
                });
            }
            sweeps.Add(new ParameterSweep { Name = sd.Name, Points = points });
        }

        var sobolDefs = sweepDefs;
        int k = sobolDefs.Count;
        var ranges = sobolDefs.Select(d => (d.Values.Min(), d.Values.Max())).ToArray();
        double EvalSobol(double[] sampled)
        {
            cancellation.ThrowIfCancellationRequested();
            var work = ScenarioJson.Clone(scenario);
            for (int d = 0; d < k; d++) sobolDefs[d].Apply(work, sampled[d]);
            double y = Analyze(work, runs, seed, null, cancellation, UncertaintySources.All, sobolOrder, maxDop: 1).PDelivery;
            progress?.Report(("Global importance", (double)Interlocked.Increment(ref done) / total));
            return y;
        }

        var (first, totalIdx, evaluations) = ComputeSobol(k, ranges, EvalSobol, baseN, cancellation);
        var global = new List<SobolFactor>();
        for (int i = 0; i < k; i++)
        {
            double direct = Math.Clamp(first[i], 0, 1);
            double tot = Math.Clamp(Math.Max(totalIdx[i], direct), 0, 1);
            global.Add(new SobolFactor { Name = sobolDefs[i].Name, DirectShare = direct, TotalShare = tot });
        }
        global = global.OrderByDescending(g => g.TotalShare).ToList();

        return new SensitivityResult
        {
            Runs = runs,
            BaselinePDelivery = baseline,
            Factors = factors,
            Sweeps = sweeps,
            PersistenceSweep = sweeps[0].Points,
            GlobalImportance = global,
            GlobalEvaluations = evaluations,
            HasGlobalVariation = evaluations > baseN * 2,
        };
    }

    private static IReadOnlyList<IReadOnlyList<string>?> DerivePercentileOrders(
        Scenario scenario, int runs, int seed, CancellationToken cancellation)
    {
        var baseResult = Analyze(scenario, runs, seed, null, cancellation);
        var nominal = FrozenOrder(scenario);

        IReadOnlyList<string>? OrderForRun(int runIndex)
        {
            if (runIndex < 0) return nominal;
            var (work, _) = ReplayRun(scenario, runIndex, seed);
            foreach (var c in work.Campaigns) c.PriorityKey = null;
            work.Config.SimulateYieldInNominal = false;
            return new RevisingScheduler(new RevisingOptions
            {
                EnableLocalRepair = true, EnableTightnessOrder = true, EnableRestStagger = true,
                EnableScarcityCrew = true, EnableBottleneckSteer = true, EnableGlobalSearch = true,
            }).Run(work).PlannedOrder;
        }

        return new IReadOnlyList<string>?[]
        {
            OrderForRun(baseResult.FinishP10Run),
            OrderForRun(baseResult.FinishP50Run),
            OrderForRun(baseResult.FinishP90Run),
        };
    }

    public static (double[] First, double[] Total, int Evaluations) ComputeSobol(
        int k, (double Lo, double Hi)[] ranges, Func<double[], double> f, int baseN,
        CancellationToken cancellation = default)
    {
        var first = new double[k];
        var total = new double[k];
        if (k <= 0 || baseN <= 0) return (first, total, 0);

        var pts = SobolPoints(baseN, 2 * k);
        double[] Map(double[] p, int off)
        {
            var x = new double[k];
            for (int d = 0; d < k; d++) x[d] = ranges[d].Lo + p[off + d] * (ranges[d].Hi - ranges[d].Lo);
            return x;
        }

        var A = new double[baseN][];
        var B = new double[baseN][];
        var yA = new double[baseN];
        var yB = new double[baseN];
        for (int j = 0; j < baseN; j++) { A[j] = Map(pts[j], 0); B[j] = Map(pts[j], k); }
        var po = new ParallelOptions { CancellationToken = cancellation };
        Parallel.For(0, baseN, po, j => yA[j] = f(A[j]));
        Parallel.For(0, baseN, po, j => yB[j] = f(B[j]));

        double mean = 0;
        for (int j = 0; j < baseN; j++) mean += yA[j] + yB[j];
        mean /= 2 * baseN;
        double varc = 0;
        for (int j = 0; j < baseN; j++) varc += Sq(yA[j] - mean) + Sq(yB[j] - mean);
        varc /= 2 * baseN;
        if (varc < 1e-12) return (first, total, baseN * 2);

        var yAB = new double[k][];
        for (int i = 0; i < k; i++) yAB[i] = new double[baseN];
        Parallel.For(0, k * baseN, po, idx =>
        {
            int i = idx / baseN, j = idx % baseN;
            var ab = (double[])A[j].Clone();
            ab[i] = B[j][i];
            yAB[i][j] = f(ab);
        });
        for (int i = 0; i < k; i++)
        {
            double sFirst = 0, sTotal = 0;
            for (int j = 0; j < baseN; j++)
            {
                double y = yAB[i][j];
                sFirst += yB[j] * (y - yA[j]);
                sTotal += Sq(yA[j] - y);
            }
            first[i] = sFirst / baseN / varc;
            total[i] = sTotal / (2 * baseN) / varc;
        }
        return (first, total, baseN * (k + 2));
    }

    private static double Sq(double x) => x * x;

    private static double[][] SobolPoints(int n, int dim)
    {
        const int bits = 30;
        var V = new uint[dim][];
        V[0] = new uint[bits + 1];
        for (int i = 1; i <= bits; i++) V[0][i] = 1u << (bits - i);
        for (int j = 1; j < dim; j++)
        {
            var (s, a, m) = SobolDirection(j);
            var v = new uint[bits + 1];
            if (bits <= s)
                for (int i = 1; i <= bits; i++) v[i] = (uint)m[i] << (bits - i);
            else
            {
                for (int i = 1; i <= s; i++) v[i] = (uint)m[i] << (bits - i);
                for (int i = s + 1; i <= bits; i++)
                {
                    v[i] = v[i - s] ^ (v[i - s] >> s);
                    for (int c = 1; c <= s - 1; c++)
                        v[i] ^= ((uint)(a >> (s - 1 - c)) & 1u) * v[i - c];
                }
            }
            V[j] = v;
        }

        double scale = 1.0 / (1u << bits);
        var x = new uint[dim];
        var pts = new double[n][];
        for (int idx = 1; idx <= n; idx++)
        {
            int c = RightmostZeroBit((uint)(idx - 1));
            var p = new double[dim];
            for (int j = 0; j < dim; j++) { x[j] ^= V[j][c]; p[j] = x[j] * scale; }
            pts[idx - 1] = p;
        }
        return pts;
    }

    private static int RightmostZeroBit(uint n)
    {
        int c = 1;
        while ((n & 1u) == 1u) { n >>= 1; c++; }
        return c;
    }

    private static (int s, int a, int[] m) SobolDirection(int dimIndex) => dimIndex switch
    {
        1 => (1, 0, M(1)),
        2 => (2, 1, M(1, 3)),
        3 => (3, 1, M(1, 3, 1)),
        4 => (3, 2, M(1, 1, 1)),
        5 => (4, 1, M(1, 1, 3, 3)),
        6 => (4, 4, M(1, 3, 5, 13)),
        7 => (5, 2, M(1, 1, 5, 5, 17)),
        8 => (5, 4, M(1, 1, 5, 5, 5)),
        9 => (5, 7, M(1, 1, 7, 11, 19)),
        10 => (5, 11, M(1, 1, 5, 1, 1)),
        11 => (5, 13, M(1, 1, 1, 3, 11)),
        12 => (5, 14, M(1, 3, 5, 5, 31)),
        13 => (6, 1, M(1, 3, 3, 9, 7, 49)),
        _ => throw new InvalidOperationException(
            $"Sobol design supports up to 14 dimensions (7 parameters); requested column {dimIndex + 1}."),
    };

    private static int[] M(params int[] m)
    {
        var a = new int[m.Length + 1];
        Array.Copy(m, 0, a, 1, m.Length);
        return a;
    }

    private static IEnumerable<ProbabilisticFlyableDays> WeatherModels(Scenario s)
    {
        if (s.FlyableModel is ProbabilisticFlyableDays main) yield return main;
        foreach (var loc in s.Locations)
            if (loc.FlyableModel is ProbabilisticFlyableDays p) yield return p;
        foreach (var c in s.Campaigns)
            if (c.OwnFlyableModel() is ProbabilisticFlyableDays cp) yield return cp;
    }

    private static void SetPhi(Scenario s, double phi)
    {
        foreach (var p in WeatherModels(s)) p.Persistence = Math.Clamp(phi, 0, 0.98);
    }

    private static void ScaleRates(Scenario s, double factor)
    {
        foreach (var p in WeatherModels(s))
            if (p.MonthlyRates is { Length: 12 })
                for (int i = 0; i < 12; i++) p.MonthlyRates[i] = Math.Clamp(p.MonthlyRates[i] * factor, 0, 1);
    }

    private static IEnumerable<ProbabilisticFlyableDays> RangeModels(Scenario s)
    {
        foreach (var loc in s.Locations)
            if (loc.RangeModel is ProbabilisticFlyableDays p) yield return p;
    }

    private static void ScaleRangeRates(Scenario s, double factor)
    {
        foreach (var p in RangeModels(s))
            if (p.MonthlyRates is { Length: 12 })
                for (int i = 0; i < 12; i++) p.MonthlyRates[i] = Math.Clamp(p.MonthlyRates[i] * factor, 0, 1);
    }

    private static IEnumerable<ProbabilisticFlyableDays> ExternalModels(Scenario s)
    {
        foreach (var a in s.ExternalSupports)
            if (a.AvailabilityModel is ProbabilisticFlyableDays p) yield return p;
    }

    private static void ScaleExternalRates(Scenario s, double factor)
    {
        foreach (var p in ExternalModels(s))
            if (p.MonthlyRates is { Length: 12 })
                for (int i = 0; i < 12; i++) p.MonthlyRates[i] = Math.Clamp(p.MonthlyRates[i] * factor, 0, 1);
    }

    private static void ScaleAbort(Scenario s, double factor)
    {
        foreach (var c in s.Campaigns)
            c.AbortProbability = Math.Clamp(c.AbortProbability * factor, 0, 0.95);
        if (s.Config.OverrideSortieAbort)
            s.Config.SortieAbortOverride = Math.Clamp(s.Config.SortieAbortOverride * factor, 0, 0.95);
    }

    private static void ScaleRetest(Scenario s, double factor)
    {
        foreach (var c in s.Campaigns)
        {
            c.RetestProbabilityStart = Math.Clamp(c.RetestProbabilityStart * factor, 0, 0.95);
            c.RetestProbabilityFloor = Math.Clamp(c.RetestProbabilityFloor * factor, 0, 0.95);
        }
    }

    private static void ScaleFailureRates(Scenario s, double factor)
    {
        foreach (var a in s.Aircraft)
            a.FailuresPer100FlightHours = Math.Max(0, a.FailuresPer100FlightHours * factor);
        foreach (var sim in s.Simulators)
            sim.FailuresPer100Hours = Math.Max(0, sim.FailuresPer100Hours * factor);
    }
}
