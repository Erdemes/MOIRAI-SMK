using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Persistence;

public interface IRepository
{
    IReadOnlyList<Pilot> GetPilots();
    void AddPilot(Pilot pilot);
    void UpdatePilot(Pilot pilot);
    void DeletePilot(string id);

    IReadOnlyList<Engineer> GetEngineers();
    void AddEngineer(Engineer engineer);
    void UpdateEngineer(Engineer engineer);
    void DeleteEngineer(string id);

    IReadOnlyList<Aircraft> GetAircraft();
    void AddAircraft(Aircraft aircraft);
    void UpdateAircraft(Aircraft aircraft);
    void DeleteAircraft(string id);

    IReadOnlyList<Campaign> GetCampaigns();
    void AddCampaign(Campaign campaign);
    void UpdateCampaign(Campaign campaign);
    void DeleteCampaign(string id);

    IReadOnlyList<CampaignGroup> GetCampaignGroups();
    void AddCampaignGroup(CampaignGroup group);
    void UpdateCampaignGroup(CampaignGroup group);

    void DeleteCampaignGroup(string id);

    IReadOnlyList<Location> GetLocations();
    void AddLocation(Location location);
    void UpdateLocation(Location location);
    void DeleteLocation(string id);

    IReadOnlyList<Simulator> GetSimulators();
    void AddSimulator(Simulator simulator);
    void UpdateSimulator(Simulator simulator);
    void DeleteSimulator(string id);

    IReadOnlyList<ExternalSupport> GetExternalSupports();
    void AddExternalSupport(ExternalSupport asset);
    void UpdateExternalSupport(ExternalSupport asset);
    void DeleteExternalSupport(string id);

    Session GetSession();

    void SaveSessionTo(string path);

    void LoadSessionFrom(string path);

    void NewSession();

    void SetSessionName(string name);

    void SaveSession();

    Scenario GetScenario();

    IReadOnlyList<Scenario> GetScenarios();

    Scenario AddScenarioCopy(string name);

    void DeleteScenario(string id);

    void SetActiveScenario(string id);

    void SaveSettings(
        ScenarioConfig config,
        FlightCalendar calendar,
        FlyableDayModel flyableModel,
        DateOnly horizonStart,
        DateOnly horizonEnd);

    static string NewId() => Guid.NewGuid().ToString("N");
}
