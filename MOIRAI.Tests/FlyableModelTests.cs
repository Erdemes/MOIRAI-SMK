using MOIRAI.Scheduling;
using Xunit;

namespace MOIRAI.Tests;

public class FlyableModelTests
{
    private static double Realized(double rate)
    {
        int len = RateBasedFlyableDays.IdealPatternLength(rate);
        int k = (int)System.Math.Round(rate * len, System.MidpointRounding.AwayFromZero);
        return (double)k / len;
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.6)]
    [InlineData(0.7)]
    [InlineData(0.75)]
    [InlineData(0.8)]
    [InlineData(0.9)]
    [InlineData(0.25)]
    public void Common_rates_are_realised_exactly(double rate)
    {
        Assert.Equal(rate, Realized(rate), 6);
        int len = RateBasedFlyableDays.IdealPatternLength(rate);
        Assert.True(len == 1 || len % 7 != 0, $"pattern length {len} is a weekly-resonant multiple of 7");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Degenerate_rates_use_length_one(double rate)
    {
        Assert.Equal(1, RateBasedFlyableDays.IdealPatternLength(rate));
        Assert.Equal(rate, Realized(rate), 6);
    }

    [Fact]
    public void Across_all_rates_never_resonant_and_close()
    {
        for (int pct = 1; pct <= 99; pct++)
        {
            double rate = pct / 100.0;
            int len = RateBasedFlyableDays.IdealPatternLength(rate);
            Assert.InRange(len, 1, 20);
            Assert.True(len == 1 || len % 7 != 0, $"rate {rate}: length {len} is a multiple of 7");
            Assert.True(System.Math.Abs(Realized(rate) - rate) <= 0.05 + 1e-9,
                $"rate {rate}: realised {Realized(rate)} too far");
        }
    }
}
