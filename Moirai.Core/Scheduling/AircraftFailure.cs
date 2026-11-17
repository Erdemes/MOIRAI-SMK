namespace MOIRAI.Scheduling;

public interface IFailureModel
{
    int? Evaluate(double ratePer100Hours, int repairDaysMin, int repairDaysMax, double hours);
}

public sealed class NoFailuresModel : IFailureModel
{
    public static readonly NoFailuresModel Instance = new();

    public int? Evaluate(double ratePer100Hours, int repairDaysMin, int repairDaysMax, double hours) => null;
}

public sealed class StochasticFailureModel : IFailureModel
{
    private readonly Random _rng;

    public StochasticFailureModel(Random rng) => _rng = rng;

    public int? Evaluate(double ratePer100Hours, int repairDaysMin, int repairDaysMax, double hours)
    {
        if (ratePer100Hours <= 0 || hours <= 0) return null;

        double p = 1 - Math.Exp(-ratePer100Hours / 100.0 * hours);
        if (_rng.NextDouble() >= p) return null;

        int lo = Math.Max(1, repairDaysMin);
        int hi = Math.Max(lo, repairDaysMax);
        return _rng.Next(lo, hi + 1);
    }
}
