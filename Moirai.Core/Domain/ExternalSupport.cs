using MOIRAI.Scheduling;

namespace MOIRAI.Domain;

public sealed class ExternalSupport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    public FlyableDayModel? AvailabilityModel { get; set; }

    public int MaxSortiesPerDay { get; set; }
}
