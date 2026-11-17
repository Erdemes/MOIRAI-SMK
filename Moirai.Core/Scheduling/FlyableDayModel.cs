using System.Text.Json.Serialization;

namespace MOIRAI.Scheduling;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RateBasedFlyableDays), "rate")]
[JsonDerivedType(typeof(ExplicitFlyableDays), "explicit")]
[JsonDerivedType(typeof(ProbabilisticFlyableDays), "probabilistic")]
public abstract class FlyableDayModel
{
    public abstract bool IsFlyable(DateOnly date);
}

public sealed class RateBasedFlyableDays : FlyableDayModel
{
    public double Rate { get; set; }

    public int PatternLength { get; set; }

    public DateOnly Anchor { get; set; }

    public override bool IsFlyable(DateOnly date)
    {
        if (PatternLength <= 0) return false;
        int offset = date.DayNumber - Anchor.DayNumber;
        int pos = ((offset % PatternLength) + PatternLength) % PatternLength;
        int threshold = (int)Math.Round(Rate * PatternLength, MidpointRounding.AwayFromZero);
        return pos < threshold;
    }

    public static int IdealPatternLength(double rate)
    {
        rate = Math.Clamp(rate, 0, 1);
        const int maxLen = 20;
        const double tie = 0.015;

        double Err(int len) =>
            Math.Abs((double)(int)Math.Round(rate * len, MidpointRounding.AwayFromZero) / len - rate);

        double bestErr = double.MaxValue;
        for (int len = 1; len <= maxLen; len++)
            if (len == 1 || len % 7 != 0) bestErr = Math.Min(bestErr, Err(len));

        int best = 1, bestBlock = int.MaxValue;
        double pickErr = double.MaxValue;
        for (int len = 1; len <= maxLen; len++)
        {
            if (len > 1 && len % 7 == 0) continue;
            double err = Err(len);
            if (err > bestErr + tie) continue;
            int block = len - (int)Math.Round(rate * len, MidpointRounding.AwayFromZero);
            bool better = block < bestBlock
                || (block == bestBlock && err < pickErr - 1e-12)
                || (block == bestBlock && Math.Abs(err - pickErr) <= 1e-12 && len < best);
            if (better) { bestBlock = block; pickErr = err; best = len; }
        }
        return best;
    }
}

public sealed class ExplicitFlyableDays : FlyableDayModel
{
    public HashSet<DateOnly> Days { get; set; } = new();

    public override bool IsFlyable(DateOnly date) => Days.Contains(date);
}

public sealed class ProbabilisticFlyableDays : FlyableDayModel
{
    public double[] MonthlyRates { get; set; } = new double[12];

    public double Persistence { get; set; }

    public int PatternLength { get; set; } = 10;

    public DateOnly Anchor { get; set; }

    public static ProbabilisticFlyableDays Uniform(double rate, double persistence, DateOnly anchor) => new()
    {
        MonthlyRates = Enumerable.Repeat(rate, 12).ToArray(),
        Persistence = persistence,
        Anchor = anchor,
    };

    public double RateFor(DateOnly date) =>
        MonthlyRates is { Length: 12 } ? MonthlyRates[date.Month - 1] : 0;

    public override bool IsFlyable(DateOnly date)
    {
        if (PatternLength <= 0) return false;
        int offset = date.DayNumber - Anchor.DayNumber;
        int pos = ((offset % PatternLength) + PatternLength) % PatternLength;
        int threshold = (int)Math.Round(RateFor(date) * PatternLength, MidpointRounding.AwayFromZero);
        return pos < threshold;
    }

    public ExplicitFlyableDays Sample(DateOnly start, DateOnly end, Random rng)
    {
        var days = new HashSet<DateOnly>();
        bool prevFlyable = false;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            double r = Math.Clamp(RateFor(d), 0, 1);
            double p = d == start
                ? r
                : prevFlyable ? r + Persistence * (1 - r) : r * (1 - Persistence);
            prevFlyable = rng.NextDouble() < p;
            if (prevFlyable) days.Add(d);
        }
        return new ExplicitFlyableDays { Days = days };
    }
}
