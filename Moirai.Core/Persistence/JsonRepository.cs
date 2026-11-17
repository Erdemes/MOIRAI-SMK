using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Persistence;

public sealed class JsonRepository : IRepository
{
    private readonly string _path;
    private Session _session;

    public string? RecoveryMessage { get; }
    public string? RecoveryPath { get; }

    public JsonRepository() : this(Path.Combine(AppContext.BaseDirectory, "moirai.json")) { }

    public JsonRepository(string path)
    {
        _path = Path.GetFullPath(path);
        var loaded = TryLoad(_path);
        if (loaded is null && File.Exists(_path))
        {
            RecoveryPath = _path + $".corrupt-{Guid.NewGuid():N}";
            File.Move(_path, RecoveryPath);
            loaded = TryLoad(_path + ".bak");
            RecoveryMessage = (loaded is null ? "Started a new session." : "Restored the previous backup.")
                + $" The unreadable file was preserved at {RecoveryPath}";
        }
        _session = loaded ?? FreshSession();
        EnsureActive();
        Save();
    }

    private Scenario Active => _session.FindActive()!;

    private static Session FreshSession() => new()
    {
        Scenarios = { NewScenarioObject("Scenario 1") },
    };

    private static Scenario NewScenarioObject(string name) => new()
    {
        Id = IRepository.NewId(),
        Name = name,
        CreatedAt = DateTime.Now.ToString("HH:mm"),
    };

    private void EnsureActive()
    {
        if (_session.Scenarios.Count == 0)
            _session.Scenarios.Add(NewScenarioObject("Scenario 1"));
        foreach (var s in _session.Scenarios.Where(s => string.IsNullOrEmpty(s.Id)))
            s.Id = IRepository.NewId();
        if (_session.Scenarios.All(s => s.Id != _session.ActiveScenarioId))
            _session.ActiveScenarioId = _session.Scenarios[0].Id;
        foreach (var s in _session.Scenarios)
            if (s.Config is { } cfg) cfg.SimulateYieldInNominal = false;
    }

    private static Session? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Session Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new JsonException("Empty file.");
        if (root["scenarios"] is not null)
            return root.Deserialize<Session>(ScenarioJson.Options)
                   ?? throw new JsonException("The file does not contain a valid session.");
        return MigrateLegacy(root);
    }

    private static Session MigrateLegacy(JsonNode root)
    {
        var baseScenario = root.Deserialize<Scenario>(ScenarioJson.Options)
                           ?? throw new JsonException("The file does not contain a valid scenario.");
        var legacyPlans = root["plans"]?.Deserialize<List<SavedPlan>>(ScenarioJson.Options) ?? new();

        string sessionName = baseScenario.Name;
        baseScenario.Id = IRepository.NewId();

        var session = new Session { Name = sessionName };
        session.Scenarios.Add(baseScenario);

        foreach (var plan in legacyPlans)
        {
            var sc = ScenarioJson.Clone(baseScenario);
            sc.Id = IRepository.NewId();
            sc.Name = plan.Name;
            sc.CreatedAt = plan.CreatedAt;
            sc.Status = plan.Status;
            sc.IsSelected = plan.IsSelected;
            sc.RunSnapshot = plan.RunScenario;
            sc.RunSchedule = plan.RunSchedule;
            if (plan.RunScenario is not null)
                sc.ScopeCampaignIds = plan.RunScenario.Campaigns.Select(c => c.Id).ToList();
            sc.CampaignResults = plan.CampaignResults?.Select(r => new CampaignOutcome
            {
                CampaignId = r.CampaignId,
                CampaignName = r.CampaignName,
                Status = r.Status,
                EndText = r.EndText,
            }).ToList();
            session.Scenarios.Add(sc);
        }

        session.ActiveScenarioId = session.Scenarios[0].Id;
        return session;
    }

    private void Save() => SaveSessionTo(_path);

    private static void AtomicWrite(string path, string json)
    {
        path = Path.GetFullPath(path);
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                file.Write(System.Text.Encoding.UTF8.GetBytes(json));
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public IReadOnlyList<Pilot> GetPilots() => Active.Pilots;

    public void AddPilot(Pilot pilot)
    {
        if (string.IsNullOrEmpty(pilot.Id)) pilot.Id = IRepository.NewId();
        Active.Pilots.Add(pilot);
        Save();
    }

    public void UpdatePilot(Pilot pilot)
    {
        Replace(Active.Pilots, pilot, p => p.Id == pilot.Id);
        Save();
    }

    public void DeletePilot(string id)
    {
        GuardResourceInUse(
            id,
            c => c.AssignedPilotIds.Contains(id),
            "pilot");
        Active.Pilots.RemoveAll(p => p.Id == id);
        Save();
    }

    public IReadOnlyList<Engineer> GetEngineers() => Active.Engineers;

    public void AddEngineer(Engineer engineer)
    {
        if (string.IsNullOrEmpty(engineer.Id)) engineer.Id = IRepository.NewId();
        Active.Engineers.Add(engineer);
        Save();
    }

    public void UpdateEngineer(Engineer engineer)
    {
        Replace(Active.Engineers, engineer, e => e.Id == engineer.Id);
        Save();
    }

    public void DeleteEngineer(string id)
    {
        GuardResourceInUse(
            id,
            c => c.AssignedEngineerIds.Contains(id),
            "engineer");
        Active.Engineers.RemoveAll(e => e.Id == id);
        Save();
    }

    public IReadOnlyList<Aircraft> GetAircraft() => Active.Aircraft;

    public void AddAircraft(Aircraft aircraft)
    {
        Active.Aircraft.Add(aircraft);
        Save();
    }

    public void UpdateAircraft(Aircraft aircraft)
    {
        Replace(Active.Aircraft, aircraft, a => a.Id == aircraft.Id);
        Save();
    }

    public void DeleteAircraft(string id)
    {
        GuardResourceInUse(
            id,
            c => c.CompatibleAircraftIds.Contains(id) || c.SupportAircraftIds.Contains(id),
            "aircraft");
        Active.Aircraft.RemoveAll(a => a.Id == id);
        Save();
    }

    public IReadOnlyList<Campaign> GetCampaigns() => Active.Campaigns;

    public void AddCampaign(Campaign campaign)
    {
        if (string.IsNullOrEmpty(campaign.Id)) campaign.Id = IRepository.NewId();
        Active.Campaigns.Add(campaign);
        Save();
    }

    public void UpdateCampaign(Campaign campaign)
    {
        Replace(Active.Campaigns, campaign, c => c.Id == campaign.Id);
        Save();
    }

    public void DeleteCampaign(string id)
    {
        Active.Campaigns.RemoveAll(c => c.Id == id);
        Save();
    }

    public IReadOnlyList<CampaignGroup> GetCampaignGroups() => Active.CampaignGroups;

    public void AddCampaignGroup(CampaignGroup group)
    {
        if (string.IsNullOrEmpty(group.Id)) group.Id = IRepository.NewId();
        Active.CampaignGroups.Add(group);
        Save();
    }

    public void UpdateCampaignGroup(CampaignGroup group)
    {
        Replace(Active.CampaignGroups, group, g => g.Id == group.Id);
        Save();
    }

    public void DeleteCampaignGroup(string id)
    {
        GuardResourceInUse(id, c => c.GroupId == id, "A-Level campaign");
        foreach (var g in Active.CampaignGroups)
            g.PrerequisiteGroupIds.RemoveAll(pid => pid == id);
        Active.CampaignGroups.RemoveAll(g => g.Id == id);
        Save();
    }

    public IReadOnlyList<Location> GetLocations() => Active.Locations;

    public void AddLocation(Location location)
    {
        if (string.IsNullOrEmpty(location.Id)) location.Id = IRepository.NewId();
        Active.Locations.Add(location);
        Save();
    }

    public void UpdateLocation(Location location)
    {
        Replace(Active.Locations, location, l => l.Id == location.Id);
        Save();
    }

    public void DeleteLocation(string id)
    {
        GuardResourceInUse(
            id,
            c => c.LocationId == id,
            "location");
        var hostedSims = Active.Simulators.Where(s => s.LocationId == id).Select(s => s.Name).ToList();
        if (hostedSims.Count > 0)
            throw new InvalidOperationException(
                "This location hosts the following simulators: " +
                $"{string.Join(", ", hostedSims)}. Move or delete them first.");
        Active.Locations.RemoveAll(l => l.Id == id);
        Save();
    }

    public IReadOnlyList<Simulator> GetSimulators() => Active.Simulators;

    public void AddSimulator(Simulator simulator)
    {
        if (string.IsNullOrEmpty(simulator.Id)) simulator.Id = IRepository.NewId();
        Active.Simulators.Add(simulator);
        Save();
    }

    public void UpdateSimulator(Simulator simulator)
    {
        Replace(Active.Simulators, simulator, s => s.Id == simulator.Id);
        Save();
    }

    public void DeleteSimulator(string id)
    {
        var sim = Active.Simulators.FirstOrDefault(s => s.Id == id);
        bool lastMainDevice = sim is { LocationId.Length: 0 }
            && Active.Simulators.Count(s => s.LocationId.Length == 0) == 1;
        GuardResourceInUse(
            id,
            c => c.CompatibleSimulatorIds.Contains(id)
                 || (lastMainDevice && c.Venue == CampaignVenue.Simulator && c.CompatibleSimulatorIds.Count == 0),
            "simulator");
        Active.Simulators.RemoveAll(s => s.Id == id);
        Save();
    }

    public IReadOnlyList<ExternalSupport> GetExternalSupports() => Active.ExternalSupports;

    public void AddExternalSupport(ExternalSupport asset)
    {
        if (string.IsNullOrEmpty(asset.Id)) asset.Id = IRepository.NewId();
        Active.ExternalSupports.Add(asset);
        Save();
    }

    public void UpdateExternalSupport(ExternalSupport asset)
    {
        Replace(Active.ExternalSupports, asset, a => a.Id == asset.Id);
        Save();
    }

    public void DeleteExternalSupport(string id)
    {
        GuardResourceInUse(id, c => c.RequiredExternalSupportIds.Contains(id), "external support");
        Active.ExternalSupports.RemoveAll(a => a.Id == id);
        Save();
    }

    public Session GetSession() => _session;

    public void SaveSessionTo(string path) =>
        AtomicWrite(path, JsonSerializer.Serialize(_session, ScenarioJson.Options));

    public void LoadSessionFrom(string path)
    {
        _session = Parse(File.ReadAllText(path));
        EnsureActive();
        Save();
    }

    public void NewSession()
    {
        _session = FreshSession();
        EnsureActive();
        Save();
    }

    public void SetSessionName(string name)
    {
        _session.Name = name;
        Save();
    }

    public void SaveSession() => Save();

    public Scenario GetScenario() => Active;

    public IReadOnlyList<Scenario> GetScenarios() => _session.Scenarios;

    public Scenario AddScenarioCopy(string name)
    {
        var copy = ScenarioJson.Clone(Active);
        copy.Id = IRepository.NewId();
        copy.Name = name;
        copy.CreatedAt = DateTime.Now.ToString("HH:mm");
        copy.Status = "Not run yet";
        copy.IsSelected = false;
        copy.ParentScenarioId = null;
        copy.RunSnapshot = null;
        copy.RunSchedule = null;
        copy.CampaignResults = null;
        copy.McResult = null;
        copy.McReplay = null;

        _session.Scenarios.Add(copy);
        _session.ActiveScenarioId = copy.Id;
        Save();
        return copy;
    }

    public void DeleteScenario(string id)
    {
        _session.Scenarios.RemoveAll(s => s.Id == id);
        EnsureActive();
        Save();
    }

    public void SetActiveScenario(string id)
    {
        if (_session.Scenarios.All(s => s.Id != id)) return;
        _session.ActiveScenarioId = id;
        Save();
    }

    public void SaveSettings(
        ScenarioConfig config,
        FlightCalendar calendar,
        FlyableDayModel flyableModel,
        DateOnly horizonStart,
        DateOnly horizonEnd)
    {
        var sc = Active;
        sc.Config = config;
        sc.Calendar = calendar;
        sc.FlyableModel = flyableModel;
        sc.HorizonStart = horizonStart;
        sc.HorizonEnd = horizonEnd;
        Save();
    }

    private static void Replace<T>(List<T> list, T item, Predicate<T> match)
    {
        int i = list.FindIndex(match);
        if (i >= 0) list[i] = item;
    }

    private void GuardResourceInUse(string id, Func<Campaign, bool> usedBy, string resourceLabel)
    {
        var usingCampaigns = Active.Campaigns
            .Where(usedBy)
            .Select(c => c.Name)
            .ToList();

        if (usingCampaigns.Count > 0)
        {
            throw new InvalidOperationException(
                $"This {resourceLabel} is used by the following campaigns: " +
                $"{string.Join(", ", usingCampaigns)}. Remove it from them first.");
        }
    }
}
