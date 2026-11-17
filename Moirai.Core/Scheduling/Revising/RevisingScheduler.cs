using MOIRAI.Persistence;

namespace MOIRAI.Scheduling.Revising;

public sealed class RevisingScheduler : ISchedulerService
{
    private readonly RevisingOptions _options;

    public RevisingScheduler(RevisingOptions? options = null) => _options = options ?? RevisingOptions.Off;

    public bool CaptureDecisions { get; init; }

    public Schedule Run(Scenario scenario) => Run(scenario, LosslessYieldModel.Instance);

    public Schedule Run(Scenario scenario, ISortieYieldModel yieldModel) =>
        Run(scenario, yieldModel, NoFailuresModel.Instance);

    public Schedule Run(Scenario scenario, ISortieYieldModel yieldModel, IFailureModel failureModel)
    {
        if (scenario.Config.SimulateYieldInNominal)
            return new Scheduler { CaptureDecisions = CaptureDecisions }.Run(scenario, yieldModel, failureModel);
        return new CoreForwardScheduler(_options) { CaptureDecisions = CaptureDecisions }.Run(scenario, yieldModel, failureModel);
    }

    public Schedule Run(Scenario scenario, IProgress<double>? progress, CancellationToken ct)
    {
        if (CanHandle(scenario, LosslessYieldModel.Instance, NoFailuresModel.Instance))
            return new CoreForwardScheduler(_options) { CaptureDecisions = CaptureDecisions }.Run(scenario, progress, ct);
        var reorder = new GreedyReorderSearch(_options).Run(scenario, progress, ct);
        return CaptureDecisions ? CaptureGreedyReorder(scenario, reorder) : reorder;
    }

    private static Schedule CaptureGreedyReorder(Scenario scenario, Schedule reorder)
    {
        if (reorder.PlannedOrder is not { } order)
            return new Scheduler { CaptureDecisions = true }.Run(scenario);
        var clone = ScenarioJson.Clone(scenario);
        var pos = new Dictionary<string, double>();
        for (int i = 0; i < order.Count; i++) pos[order[i]] = i;
        foreach (var c in clone.Campaigns)
            if (pos.TryGetValue(c.Id, out var k)) c.PriorityKey = k;
        return new Scheduler { CaptureDecisions = true }.Run(clone);
    }

    internal static bool CanHandle(Scenario s, ISortieYieldModel yield, IFailureModel failure) =>
        yield is LosslessYieldModel
        && failure is NoFailuresModel
        && !s.Config.SimulateYieldInNominal;
}

public sealed class RevisingOptions
{
    public bool EnableLocalRepair { get; init; }

    public bool EnableLookForward { get; init; }

    public bool EnableTightnessOrder { get; init; }

    public bool EnableScarcityCrew { get; init; }

    public bool EnableBottleneckSteer { get; init; }

    public bool EnableRestStagger { get; init; }

    public bool EnableGlobalSearch { get; init; }

    public int GlobalSearchIterations { get; init; } = 4000;

    public int MaxOrderRuin { get; init; } = 3;

    public double GlobalSearchStartTemp { get; init; } = 4.0;

    public int GlobalSearchStall { get; init; } = 600;

    public int SearchSeed { get; init; } = 0x5EED;

    public int SearchParallelism { get; init; }

    public int MaxRuinSize { get; init; } = 4;

    public int MaxRepairsPerDay { get; init; } = 2;

    public int MaxRepairsPerRun { get; init; } = 200;

    public bool AnyEnabled => EnableLocalRepair || EnableLookForward || EnableGlobalSearch;

    public bool HasSteering =>
        EnableLocalRepair || EnableTightnessOrder || EnableScarcityCrew || EnableBottleneckSteer
        || EnableRestStagger;

    public static RevisingOptions Off { get; } = new();
}
