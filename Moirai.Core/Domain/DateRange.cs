namespace MOIRAI.Domain;

public sealed class DateRange
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }

    public string Note { get; set; } = string.Empty;

    public int Days => End.DayNumber - Start.DayNumber + 1;

    public bool Contains(DateOnly day) => day >= Start && day <= End;

    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";
}
