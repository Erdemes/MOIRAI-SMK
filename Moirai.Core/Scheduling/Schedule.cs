using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

public sealed class Schedule
{
    public List<Sortie> Sorties { get; set; } = new();
    public List<Rehearsal> Rehearsals { get; set; } = new();

    public List<SimulatorSortie> SimulatorSorties { get; set; } = new();

    public Dictionary<string, DateOnly?> CampaignEndDates { get; set; } = new();

    public Dictionary<string, bool> CampaignCompleted { get; set; } = new();

    public Dictionary<string, BottleneckTally> Bottlenecks { get; set; } = new();

    public Dictionary<string, string> DayLog { get; set; } = new();

    public Dictionary<string, string> DayStopLog { get; set; } = new();

    public Dictionary<string, List<DateOnly>> VoluntaryRest { get; set; } = new();

    public Dictionary<string, List<DateRange>> AutoMaintenance { get; set; } = new();

    public Dictionary<string, List<DateRange>> UnscheduledRepairs { get; set; } = new();

    public Dictionary<string, List<DateRange>> SimulatorRepairs { get; set; } = new();

    public List<DeploymentInfo> Deployments { get; set; } = new();

    public List<RelocationFlight> RelocationFlights { get; set; } = new();

    public DateOnly? ProgramFinish { get; set; }

    public List<string> Diagnostics { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<DecisionEntry> DecisionLog { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<string>? PlannedOrder { get; set; }

    public bool AllCampaignsCompleted() => CampaignCompleted.Values.All(done => done);

    public bool MeetsMaster(DateOnly? masterDeadline) =>
        AllCampaignsCompleted()
        && (masterDeadline is not { } m || ProgramFinish is not { } mk || mk <= m);

    public bool MeetsDeadline(string campaignId, DateOnly target) =>
        CampaignCompleted.GetValueOrDefault(campaignId)
        && CampaignEndDates.TryGetValue(campaignId, out var end)
        && end.HasValue
        && end.Value <= target;
}
