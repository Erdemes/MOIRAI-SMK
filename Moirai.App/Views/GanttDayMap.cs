using MOIRAI.ViewModels;

namespace MOIRAI.Views;

internal static class GanttDayMap
{
    public static List<DateOnly> VisibleDays(GanttData d, bool hideEmpty)
    {
        var sc = d.Scenario;
        int n = sc.HorizonEnd.DayNumber - sc.HorizonStart.DayNumber + 1;
        var all = new List<DateOnly>(Math.Max(0, n));
        for (var day = sc.HorizonStart; day <= sc.HorizonEnd; day = day.AddDays(1))
            all.Add(day);
        if (!hideEmpty) return all;

        var content = new HashSet<DateOnly>();
        foreach (var s in d.Schedule.Sorties) content.Add(s.Date);
        foreach (var ss in d.Schedule.SimulatorSorties) content.Add(ss.Date);
        foreach (var r in d.Schedule.Rehearsals) content.Add(r.Date);
        foreach (var f in d.Schedule.RelocationFlights) content.Add(f.Date);

        return all.Where(day => sc.IsUsableDay(day) || content.Contains(day)).ToList();
    }
}
