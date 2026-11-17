namespace MOIRAI.ViewModels;

public static class WeekdaySet
{
    private static readonly DayOfWeek[] MonFirst =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    public static HashSet<DayOfWeek> ToSet(bool[] monToSun)
    {
        var set = new HashSet<DayOfWeek>();
        if (monToSun is not { Length: 7 }) return set;
        for (int i = 0; i < 7; i++)
            if (monToSun[i]) set.Add(MonFirst[i]);
        return set;
    }

    public static bool[] ToArray(ISet<DayOfWeek> days)
    {
        var arr = new bool[7];
        for (int i = 0; i < 7; i++)
            arr[i] = days.Contains(MonFirst[i]);
        return arr;
    }
}
