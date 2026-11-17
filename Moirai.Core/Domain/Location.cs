using MOIRAI.Scheduling;

namespace MOIRAI.Domain;

public sealed class Location
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    public string? Icao { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public int? BlocksPerDayCap { get; set; }

    public int FerryDays { get; set; } = 1;

    public double FerryFlightHours { get; set; } = 2;

    public double FerryDutyHours { get; set; } = 6;

    public int SetupDays { get; set; } = 2;

    public int TeardownDays { get; set; } = 1;

    public int PostDeploymentRestDays { get; set; }

    public FlyableDayModel? FlyableModel { get; set; }

    public FlyableDayModel? RangeModel { get; set; }
}
