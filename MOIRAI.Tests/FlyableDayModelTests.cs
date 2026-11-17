using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class FlyableDayModelTests
{
    private static readonly RateBasedFlyableDays Model =
        new() { Rate = 3.0 / 7.0, PatternLength = 7, Anchor = new DateOnly(2026, 6, 1) };

    [Theory]
    [InlineData("2026-06-01", true)]
    [InlineData("2026-06-02", true)]
    [InlineData("2026-06-03", true)]
    [InlineData("2026-06-04", false)]
    [InlineData("2026-06-07", false)]
    [InlineData("2026-06-08", true)]
    public void RateBased_pattern_repeats(string date, bool expected) =>
        Assert.Equal(expected, Model.IsFlyable(DateOnly.Parse(date)));

    [Theory]
    [InlineData("2026-05-31", false)]
    [InlineData("2026-05-30", false)]
    [InlineData("2026-05-26", true)]
    [InlineData("2026-05-25", true)]
    public void RateBased_negative_modulo_is_correct(string date, bool expected) =>
        Assert.Equal(expected, Model.IsFlyable(DateOnly.Parse(date)));

    [Theory]
    [InlineData("2025-06-02", true)]
    [InlineData("2025-06-04", true)]
    [InlineData("2025-06-05", false)]
    [InlineData("2025-06-06", false)]
    [InlineData("2025-06-07", true)]
    public void RateBased_fraction_threshold_rounds(string date, bool expected)
    {
        var m = new RateBasedFlyableDays { Rate = 0.6, PatternLength = 5, Anchor = new DateOnly(2025, 6, 2) };
        Assert.Equal(expected, m.IsFlyable(DateOnly.Parse(date)));
    }

    [Fact]
    public void RateBased_zero_patternLength_is_always_false()
    {
        var m = new RateBasedFlyableDays { Rate = 0.5, PatternLength = 0 };
        Assert.False(m.IsFlyable(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void Explicit_only_days_in_the_list()
    {
        var m = new ExplicitFlyableDays { Days = { new DateOnly(2026, 6, 8) } };
        Assert.True(m.IsFlyable(new DateOnly(2026, 6, 8)));
        Assert.False(m.IsFlyable(new DateOnly(2026, 6, 9)));
    }
}
