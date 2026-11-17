using System.Text.Json.Serialization;

namespace MOIRAI.Domain;

public abstract class Resource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string ShortName { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayShort =>
        !string.IsNullOrWhiteSpace(ShortName)
            ? ShortName
            : (Name.Length <= 4 ? Name : Name[..4]);

    public HashSet<string> Qualifications { get; set; } = new();

    public List<string> TypeRatings { get; set; } = new();

    public List<DateRange> LeaveWindows { get; set; } = new();

    public bool IsOnLeave(DateOnly day) => LeaveWindows.Any(w => w.Contains(day));

    public bool IsQualifiedFor(string qualification) => Qualifications.Contains(qualification);
}
