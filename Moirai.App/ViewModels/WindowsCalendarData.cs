using Avalonia.Media;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public enum WindowsCalendarKind { Maintenance, Leave, Campaign }

public sealed class WindowsCalendarData
{
    public required WindowsCalendarKind Kind { get; init; }

    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }

    public required FlightCalendar Calendar { get; init; }

    public required List<WindowsCalendarRow> Rows { get; init; }

    public string? HighlightId { get; init; }

    public static WindowsCalendarData Build(
        WindowsCalendarKind kind, Scenario scenario,
        List<WindowsCalendarRow> rows, string? highlightId)
    {
        DateOnly start, end;
        if (scenario.HorizonEnd > scenario.HorizonStart)
        {
            start = scenario.HorizonStart;
            end = scenario.HorizonEnd;
        }
        else
        {
            start = DateOnly.FromDateTime(DateTime.Today);
            end = start.AddDays(89);
        }

        var all = rows.SelectMany(r => r.Windows).ToList();
        if (all.Count > 0)
        {
            var min = all.Min(w => w.Start);
            var max = all.Max(w => w.End);
            if (min < start) start = min;
            if (max > end) end = max;
        }
        start = start.AddDays(-3);
        end = end.AddDays(3);
        if (end.DayNumber - start.DayNumber + 1 > 750) end = start.AddDays(749);

        return new WindowsCalendarData
        {
            Kind = kind,
            Start = start,
            End = end,
            Calendar = scenario.Calendar,
            Rows = rows,
            HighlightId = highlightId,
        };
    }
}

public sealed class WindowsCalendarRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required List<DateRange> Windows { get; init; }

    public Color? Accent { get; init; }
}

public sealed record WindowChange(string RowId, string WindowId, DateOnly NewStart, DateOnly NewEnd);
