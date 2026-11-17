using MOIRAI.Analysis;
using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

public sealed class Scenario
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "Untitled scenario";

    public string CreatedAt { get; set; } = string.Empty;

    public List<Pilot> Pilots { get; set; } = new();
    public List<Engineer> Engineers { get; set; } = new();
    public List<Aircraft> Aircraft { get; set; } = new();
    public List<Campaign> Campaigns { get; set; } = new();

    public List<CampaignGroup> CampaignGroups { get; set; } = new();

    public List<Location> Locations { get; set; } = new();

    public List<Simulator> Simulators { get; set; } = new();

    public List<ExternalSupport> ExternalSupports { get; set; } = new();

    public ScenarioConfig Config { get; set; } = new();
    public FlightCalendar Calendar { get; set; } = new();

    public FlyableDayModel FlyableModel { get; set; } =
        new RateBasedFlyableDays { Rate = 0.7, PatternLength = 7, Anchor = default };

    public DateOnly HorizonStart { get; set; }
    public DateOnly HorizonEnd { get; set; }

    public DateOnly? MasterDeadline { get; set; }

    public DateOnly? FreezeDate { get; set; }

    public List<Sortie> ActualSorties { get; set; } = new();

    public List<Rehearsal> ActualRehearsals { get; set; } = new();

    public List<SimulatorSortie> ActualSimulatorSorties { get; set; } = new();

    public List<SortiePin> SortiePins { get; set; } = new();

    public string? ParentScenarioId { get; set; }

    public List<string> ScopeCampaignIds { get; set; } = new();

    public string Status { get; set; } = "Not run yet";

    public bool IsSelected { get; set; }

    public Scenario? RunSnapshot { get; set; }

    public Schedule? RunSchedule { get; set; }

    public List<CampaignOutcome>? CampaignResults { get; set; }

    public MonteCarlo.Result? McResult { get; set; }

    public McReplayInfo? McReplay { get; set; }

    public bool IsUsableDay(DateOnly date) =>
        Calendar.IsWorkday(date) && FlyableModel.IsFlyable(date);

    public bool IsUsableDay(Campaign c, DateOnly date) =>
        Calendar.IsWorkday(c, date) && FlyableModel.IsFlyable(date);

    public CampaignGroup? GroupOf(Campaign c) =>
        c.GroupId.Length == 0 ? null : CampaignGroups.FirstOrDefault(g => g.Id == c.GroupId);
}

public sealed class McReplayInfo
{
    public int Seed { get; set; }
    public int FinishP10Run { get; set; } = -1;
    public int FinishP50Run { get; set; } = -1;
    public int FinishP90Run { get; set; } = -1;
}
