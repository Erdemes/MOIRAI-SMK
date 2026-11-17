using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

public readonly record struct SortieOutcome(bool Aborted, double GoodPoints);

public interface ISortieYieldModel
{
    SortieOutcome Evaluate(Campaign campaign, double attemptedPoints, double goodPointsDone);
}

public sealed class LosslessYieldModel : ISortieYieldModel
{
    public static readonly LosslessYieldModel Instance = new();

    public SortieOutcome Evaluate(Campaign campaign, double attemptedPoints, double goodPointsDone) =>
        new(Aborted: false, GoodPoints: attemptedPoints);
}

public sealed class StochasticYieldModel : ISortieYieldModel
{
    private readonly Random _rng;
    private readonly double? _abortOverride;

    public StochasticYieldModel(Random rng, double? abortOverride = null)
    {
        _rng = rng;
        _abortOverride = abortOverride;
    }

    public SortieOutcome Evaluate(Campaign campaign, double attemptedPoints, double goodPointsDone)
    {
        double abort = Math.Clamp(
            _abortOverride ?? (campaign.TestEfficiencyEnabled ? campaign.AbortProbability : 0), 0, 0.95);
        if (abort > 0 && _rng.NextDouble() < abort)
            return new SortieOutcome(Aborted: true, GoodPoints: 0);

        if (!campaign.TestEfficiencyEnabled)
            return new SortieOutcome(Aborted: false, GoodPoints: attemptedPoints);

        double good = 0, left = attemptedPoints;
        while (left > 1e-9)
        {
            double point = Math.Min(1.0, left);
            left -= point;
            double p = campaign.RetestProbabilityAt(goodPointsDone + good);
            if (p <= 0 || _rng.NextDouble() >= p) good += point;
        }
        return new SortieOutcome(Aborted: false, GoodPoints: good);
    }
}
