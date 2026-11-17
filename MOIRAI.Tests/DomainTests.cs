using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class DomainTests
{
    [Theory]
    [InlineData(10, 3, 4)]
    [InlineData(9, 3, 3)]
    [InlineData(1, 3, 1)]
    [InlineData(0, 3, 0)]
    [InlineData(10, 0, 0)]
    public void RequiredSorties_computes_ceil(double ttp, double tps, int expected)
    {
        var c = new Campaign { TotalTestPoints = ttp, TestPointsPerSortie = tps };
        Assert.Equal(expected, c.RequiredSorties());
    }

    [Fact]
    public void IsWorkday_excludes_weekend_and_holiday()
    {
        var cal = new FlightCalendar();
        cal.Holidays.Add(new DateOnly(2026, 6, 8));

        Assert.True(cal.IsWorkday(new DateOnly(2026, 6, 9)));
        Assert.False(cal.IsWorkday(new DateOnly(2026, 6, 6)));
        Assert.False(cal.IsWorkday(new DateOnly(2026, 6, 7)));
        Assert.False(cal.IsWorkday(new DateOnly(2026, 6, 8)));
    }

    [Fact]
    public void IsUsableDay_requires_workday_AND_flyable()
    {
        var s = new Scenario
        {
            Calendar = new FlightCalendar(),
            FlyableModel = new ExplicitFlyableDays { Days = { new DateOnly(2026, 6, 9) } },
        };

        Assert.True(s.IsUsableDay(new DateOnly(2026, 6, 9)));
        Assert.False(s.IsUsableDay(new DateOnly(2026, 6, 10)));
        Assert.False(s.IsUsableDay(new DateOnly(2026, 6, 6)));
    }
}
