namespace MOIRAI.Scheduling;

public static class DayLogCodes
{
    public const char Idle = '-';

    public const char Placed = '.';

    public const char NotUsable = 'w';

    public const char Prereq = 'q';

    public const char Range = 'g';

    public const char Aircraft = 'a';
    public const char Pilot = 'p';
    public const char Engineer = 'e';
    public const char Rehearsal = 'r';

    public const char Review = 'd';

    public const char Simulator = 's';

    public const char External = 'x';

    public const char Pinned = 'k';

    public const char DailyCap = 'c';

    public const char Finished = 'f';

    public const char Unknown = '?';

    public static bool IsBlocked(char c) => c is NotUsable or Prereq or Range
        or Aircraft or Pilot or Engineer or Rehearsal or Review or Simulator or External or Pinned or Unknown;

    public static string Describe(char c) => c switch
    {
        Idle => "not open",
        Placed => "flew",
        NotUsable => "not usable (weather/calendar)",
        Prereq => "waiting on a prerequisite",
        Range => "range/airspace not available",
        Aircraft => "no compatible aircraft free",
        Pilot => "no eligible pilot",
        Engineer => "too few engineers",
        Rehearsal => "no rehearsal slot",
        Review => "data review (inter-sortie gap or prerequisite review)",
        Simulator => "no simulator slot",
        External => "external support (chase/tanker) not available",
        Pinned => "pinned crew/aircraft not available",
        _ => "blocked",
    };

    public static string PhaseSummary(string log, int maxSegments = 4)
    {
        var runs = new List<(char Code, int Days)>();
        foreach (char c in log)
        {
            if (!IsBlocked(c)) continue;
            if (runs.Count > 0 && runs[^1].Code == c) runs[^1] = (c, runs[^1].Days + 1);
            else runs.Add((c, 1));
        }

        runs.RemoveAll(r => r.Code == NotUsable && r.Days <= 2);

        var merged = new List<(char Code, int Days)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && merged[^1].Code == r.Code)
                merged[^1] = (r.Code, merged[^1].Days + r.Days);
            else merged.Add(r);
        }
        if (merged.Count == 0) return string.Empty;

        var kept = merged
            .Select((r, i) => (r.Code, r.Days, Index: i))
            .OrderByDescending(r => r.Days)
            .Take(maxSegments)
            .OrderBy(r => r.Index)
            .ToList();

        string text = string.Join(" → ", kept.Select(r => $"{Label(r.Code)} {r.Days}d"));
        return kept.Count < merged.Count ? text + " → …" : text;
    }

    private static string Label(char c) => c switch
    {
        NotUsable => "weather",
        Prereq => "prereq",
        Range => "range",
        Aircraft => "aircraft",
        Pilot => "pilot",
        Engineer => "engineer",
        Rehearsal => "rehearsal",
        Review => "data review",
        Simulator => "simulator",
        External => "external support",
        Pinned => "pinned",
        _ => "blocked",
    };
}
