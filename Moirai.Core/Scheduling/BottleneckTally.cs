namespace MOIRAI.Scheduling;

public sealed class BottleneckTally
{
    public int Prereq { get; set; }

    public int NotUsable { get; set; }

    public int Range { get; set; }

    public int Aircraft { get; set; }

    public int Pilot { get; set; }

    public int Engineer { get; set; }

    public int Rehearsal { get; set; }

    public int Review { get; set; }

    public int Simulator { get; set; }

    public int External { get; set; }

    public int Pinned { get; set; }

    public int Placed { get; set; }

    public (string Reason, int Days)? Dominant()
    {
        var pairs = new (string Reason, int Days)[]
        {
            ("prerequisite", Prereq),
            ("weather/calendar", NotUsable),
            ("range availability", Range),
            ("aircraft", Aircraft),
            ("pilot", Pilot),
            ("engineer", Engineer),
            ("rehearsal", Rehearsal),
            ("data review", Review),
            ("simulator", Simulator),
            ("external support", External),
            ("pinned resources", Pinned),
        };
        var max = pairs.MaxBy(p => p.Days);
        return max.Days > 0 ? max : null;
    }
}
