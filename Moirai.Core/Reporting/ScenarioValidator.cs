using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Reporting;

public enum IssueSeverity { Error, Warning, Info }

public sealed class ValidationIssue
{
    public required IssueSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public string? CampaignName { get; init; }
}

public static class ScenarioValidator
{
    private static void ValidatePins(Scenario scenario, List<Campaign> campaigns,
        Action<IssueSeverity, string, string, string?> add)
    {
        if (scenario.SortiePins.Count == 0) return;
        const string cat = "Pins";
        var inScope = campaigns.ToDictionary(c => c.Id);
        var allIds = scenario.Campaigns.Select(c => c.Id).ToHashSet();
        var nameOf = scenario.Pilots.Cast<Resource>().Concat(scenario.Engineers)
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var tailById = scenario.Aircraft.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var from = scenario.FreezeDate is { } fz && fz > scenario.HorizonStart ? fz : scenario.HorizonStart;

        string PersonName(string id) => nameOf.TryGetValue(id, out var r) ? r.Name : id;

        foreach (var dup in scenario.SortiePins.GroupBy(p => (p.CampaignId, p.SortieNo)).Where(g => g.Count() > 1))
            add(IssueSeverity.Warning, cat,
                $"Sortie #{dup.Key.SortieNo} is pinned {dup.Count()} times · the last pin wins.",
                inScope.TryGetValue(dup.Key.CampaignId, out var dc) ? dc.Name : null);

        foreach (var pin in scenario.SortiePins)
        {
            if (!allIds.Contains(pin.CampaignId))
            {
                add(IssueSeverity.Warning, cat, $"A pin on sortie #{pin.SortieNo} refers to a deleted campaign · it is ignored.", null);
                continue;
            }
            if (!inScope.TryGetValue(pin.CampaignId, out var c) || pin.IsEmpty) continue;
            string n = c.Name;
            string s = $"Pinned sortie #{pin.SortieNo}";
            if (pin.SortieNo < 1) { add(IssueSeverity.Warning, cat, $"{s}: invalid sortie number · ignored.", n); continue; }

            int flown = c.Venue == CampaignVenue.Simulator
                ? scenario.ActualSimulatorSorties.Count(a => a.CampaignId == c.Id)
                : scenario.ActualSorties.Count(a => a.CampaignId == c.Id);
            if (pin.SortieNo <= flown)
            {
                add(IssueSeverity.Info, cat, $"{s} is already recorded as flown · the pin is ignored.", n);
                continue;
            }

            if (pin.Date is { } pd)
            {
                string day = $"{s} is pinned to {pd:yyyy-MM-dd}";
                if (pd < from || pd > scenario.HorizonEnd)
                    add(IssueSeverity.Warning, cat, $"{day}, outside the planning window · it cannot fly then.", n);
                else if (pd < c.StartDate)
                    add(IssueSeverity.Warning, cat, $"{day}, before the campaign starts · it flies once the campaign opens.", n);
                else if (!scenario.Calendar.IsWorkday(c, pd))
                    add(IssueSeverity.Warning, cat, $"{day}, a non-working day for the campaign · it flies on the next feasible day.", n);
                else if (c.Venue != CampaignVenue.Simulator
                         && !(c.OwnFlyableModel() ?? scenario.FlyableModel).IsFlyable(pd))
                    add(IssueSeverity.Info, cat, $"{day}, a nominally non-flyable day · it flies on the next feasible day.", n);
                var earlier = scenario.SortiePins.Where(o => o.CampaignId == c.Id && o.SortieNo > pin.SortieNo
                                                             && o.Date is { } od && od < pd).ToList();
                if (earlier.Count > 0)
                    add(IssueSeverity.Warning, cat,
                        $"{day}, but a later sortie (#{earlier[0].SortieNo}) is pinned to an earlier day · sorties fly in order.", n);
            }

            int needEngs = c.EngineersPerSortie > 0 ? c.EngineersPerSortie : scenario.Config.EngineersPerSortie;
            int needPilots;
            if (c.Venue == CampaignVenue.Simulator)
            {
                needPilots = Math.Max(0, c.PilotsPerSortie);
                if (!string.IsNullOrEmpty(pin.SimulatorId))
                {
                    var sim = scenario.Simulators.FirstOrDefault(x => x.Id == pin.SimulatorId);
                    if (sim is null)
                        add(IssueSeverity.Error, cat, $"{s}: its pinned simulator no longer exists · the campaign cannot continue.", n);
                    else if (c.CompatibleSimulatorIds.Count > 0 && !c.CompatibleSimulatorIds.Contains(sim.Id))
                        add(IssueSeverity.Error, cat, $"{s}: simulator '{sim.Name}' is not compatible with the campaign.", n);
                }
            }
            else
            {
                needPilots = Math.Max(1, c.PilotsPerSortie > 0 ? c.PilotsPerSortie : scenario.Config.PilotsPerSortie);
                if (!string.IsNullOrEmpty(pin.AircraftId))
                {
                    if (!tailById.TryGetValue(pin.AircraftId, out var tail))
                        add(IssueSeverity.Error, cat, $"{s}: its pinned aircraft {pin.AircraftId} no longer exists · the campaign cannot continue.", n);
                    else
                    {
                        needPilots = Math.Max(needPilots, Math.Max(1, tail.MinPilots));
                        if (!c.CompatibleAircraftIds.Contains(tail.Id))
                            add(IssueSeverity.Error, cat, $"{s}: aircraft {tail.Id} is not compatible with the campaign.", n);
                        foreach (var id in pin.PilotIds.Concat(pin.EngineerIds))
                            if (nameOf.TryGetValue(id, out var r) && r.TypeRatings.Count > 0 && !r.TypeRatings.Contains(tail.Id))
                                add(IssueSeverity.Error, cat, $"{s}: {r.Name} is not type-rated on {tail.Id}.", n);
                        foreach (var w in tail.MaintenanceWindows)
                            if (w.Start <= from && w.End >= scenario.HorizonEnd)
                                add(IssueSeverity.Warning, cat, $"{s}: aircraft {tail.Id} is in maintenance for the rest of the horizon.", n);
                    }
                }
                if (pin.SupportAircraftIds.Count > 0 || pin.SupportPilotIds.Count > 0)
                {
                    if (c.SupportAircraftPerSortie <= 0)
                        add(IssueSeverity.Warning, cat, $"{s}: pins support ships but the campaign is single-ship · the support pins are ignored.", n);
                    foreach (var id in pin.SupportAircraftIds.Where(id => !c.SupportAircraftIds.Contains(id)))
                        add(IssueSeverity.Error, cat, $"{s}: support aircraft {id} is not in the campaign's support pool.", n);
                }
            }

            foreach (var id in pin.PilotIds.Concat(pin.SupportPilotIds).Where(id => !c.AssignedPilotIds.Contains(id)))
                add(IssueSeverity.Error, cat, $"{s}: pilot {PersonName(id)} is no longer assigned to the campaign.", n);
            foreach (var id in pin.EngineerIds.Where(id => !c.AssignedEngineerIds.Contains(id)))
                add(IssueSeverity.Error, cat, $"{s}: FTE {PersonName(id)} is no longer assigned to the campaign.", n);
            foreach (var id in pin.PilotIds.Concat(pin.EngineerIds).Concat(pin.SupportPilotIds)
                         .GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                add(IssueSeverity.Error, cat, $"{s}: {PersonName(id)} is pinned to two seats.", n);

            if (pin.PilotIds.Count > needPilots)
                add(IssueSeverity.Info, cat, $"{s}: {pin.PilotIds.Count} pilots pinned, {needPilots} needed · all of them fly.", n);
            if (pin.EngineerIds.Count > needEngs)
                add(IssueSeverity.Info, cat, $"{s}: {pin.EngineerIds.Count} FTEs pinned, {needEngs} needed · all of them fly.", n);

            foreach (var id in pin.PilotIds.Concat(pin.EngineerIds).Concat(pin.SupportPilotIds).Distinct())
                if (nameOf.TryGetValue(id, out var r) && from <= scenario.HorizonEnd
                    && r.LeaveWindows.Any(w => w.Start <= from && w.End >= scenario.HorizonEnd))
                    add(IssueSeverity.Warning, cat, $"{s}: {r.Name} is on leave for the rest of the horizon.", n);
        }
    }

    public static List<ValidationIssue> Validate(Scenario scenario, IEnumerable<string>? scopeCampaignIds = null)
    {
        var issues = new List<ValidationIssue>();

        var scope = scopeCampaignIds?.ToHashSet();
        var campaigns = (scope is { Count: > 0 }
            ? scenario.Campaigns.Where(c => scope.Contains(c.Id))
            : scenario.Campaigns).ToList();

        var tailIds = scenario.Aircraft.Select(a => a.Id).ToHashSet();
        var tailById = scenario.Aircraft.ToDictionary(a => a.Id);
        var locationIds = scenario.Locations.Select(l => l.Id).ToHashSet();
        var pilotById = scenario.Pilots.ToDictionary(p => p.Id);
        var engById = scenario.Engineers.ToDictionary(e => e.Id);
        var simsByBase = scenario.Simulators.GroupBy(s => s.LocationId).ToDictionary(g => g.Key, g => g.ToList());
        int bpd = Math.Max(1, scenario.Config.BlocksPerDay);
        int cfgPilots = Math.Max(1, scenario.Config.PilotsPerSortie);
        int cfgEngs = scenario.Config.EngineersPerSortie;

        void Add(IssueSeverity sev, string cat, string msg, string? camp = null) =>
            issues.Add(new ValidationIssue { Severity = sev, Category = cat, Message = msg, CampaignName = camp });

        if (scenario.HorizonEnd < scenario.HorizonStart)
            Add(IssueSeverity.Error, "Horizon", "The planning horizon ends before it starts.");
        if (scenario.MasterDeadline is { } md && (md < scenario.HorizonStart || md > scenario.HorizonEnd))
            Add(IssueSeverity.Warning, "Horizon",
                $"The master deadline ({md:yyyy-MM-dd}) is outside the planning horizon.");
        if (campaigns.Any(c => c.Venue != CampaignVenue.Simulator) && scenario.Aircraft.Count == 0)
            Add(IssueSeverity.Error, "Aircraft", "No aircraft defined.");

        foreach (var c in campaigns)
        {
            string n = c.Name;

            if (c.TestPointsPerSortie <= 0)
                Add(IssueSeverity.Warning, "Work", "Zero test points per sortie; no sorties scheduled.", n);
            if (c.TotalTestPoints <= 0)
                Add(IssueSeverity.Info, "Work", "Total test points is 0 · nothing to schedule.", n);

            int needEngs = c.EngineersPerSortie > 0 ? c.EngineersPerSortie : cfgEngs;
            int pilots = c.AssignedPilotIds.Count(pilotById.ContainsKey);
            int engs = c.AssignedEngineerIds.Count(engById.ContainsKey);

            if (c.Venue == CampaignVenue.Simulator)
            {
                var mainSims = simsByBase.GetValueOrDefault(string.Empty, new List<Simulator>());
                bool hasDevice = c.CompatibleSimulatorIds.Count == 0
                    ? mainSims.Count > 0
                    : c.CompatibleSimulatorIds.Any(id => mainSims.Any(s => s.Id == id));
                if (!hasDevice)
                    Add(IssueSeverity.Error, "Simulators",
                        "No compatible simulator at the main base.", n);

                int needPilots = Math.Max(0, c.PilotsPerSortie);
                if (pilots < needPilots)
                    Add(IssueSeverity.Error, "Crew",
                        $"Needs {needPilots} pilot(s) per session but has {pilots} assigned.", n);
                if (engs < needEngs)
                    Add(IssueSeverity.Error, "Crew",
                        $"Needs {needEngs} engineer(s) per session but has {engs} assigned.", n);
                if (needPilots == 0 && needEngs == 0)
                    Add(IssueSeverity.Warning, "Crew", "The session has no assigned crew (0 pilots, 0 engineers).", n);
                if (c.LocationId.Length > 0)
                    Add(IssueSeverity.Info, "Simulators",
                        "Simulator campaigns use the main base; location is ignored.", n);
            }
            else
            {
                var compat = c.CompatibleAircraftIds.Where(tailIds.Contains).ToList();
                if (c.CompatibleAircraftIds.Count == 0)
                    Add(IssueSeverity.Error, "Aircraft", "No compatible aircraft selected.", n);
                else if (compat.Count == 0)
                    Add(IssueSeverity.Error, "Aircraft",
                        "Its compatible aircraft no longer exist in the scenario.", n);

                int campPilots = Math.Max(1, c.PilotsPerSortie > 0 ? c.PilotsPerSortie : cfgPilots);
                int NeedP(string tailId) => Math.Max(campPilots, Math.Max(1, tailById[tailId].MinPilots));
                int minNeedP = compat.Count > 0 ? compat.Min(NeedP) : campPilots;

                if (pilots < minNeedP)
                    Add(IssueSeverity.Error, "Crew",
                        $"Needs at least {minNeedP} pilots per sortie; {pilots} assigned.", n);
                if (engs < needEngs)
                    Add(IssueSeverity.Error, "Crew",
                        $"Needs {needEngs} engineer(s) per sortie but has {engs} assigned.", n);

                if (compat.Count > 0)
                {
                    if (pilots >= minNeedP &&
                        !compat.Any(t => c.AssignedPilotIds.Count(id => RatedOn(pilotById, id, t)) >= NeedP(t)))
                        Add(IssueSeverity.Error, "Type ratings",
                            "No compatible aircraft has enough type-rated assigned pilots.", n);
                    if (engs >= needEngs &&
                        !compat.Any(t => c.AssignedEngineerIds.Count(id => RatedOn(engById, id, t)) >= needEngs))
                        Add(IssueSeverity.Error, "Type ratings",
                            "No compatible aircraft has enough type-rated assigned engineers.", n);
                }

                if (c.RehearsalRequired
                    && !simsByBase.ContainsKey(c.LocationId) && !simsByBase.ContainsKey(string.Empty))
                    Add(IssueSeverity.Error, "Simulators",
                        "Required rehearsals have no simulator at the campaign or main base.", n);
            }

            int bps = Math.Max(1, c.BlocksPerSortie);
            if (bps > bpd)
                Add(IssueSeverity.Error, "Blocks",
                    $"Blocks per sortie ({bps}) exceeds the day's {bpd} block(s).", n);
            else if (c.AllowedBlocks.Count > 0 && !c.AllowedBlocks.Any(b => b >= 1 && b + bps - 1 <= bpd))
                Add(IssueSeverity.Error, "Blocks",
                    "No allowed start block leaves room for the sortie's block span.", n);

            if (c.TargetEndDate != DateOnly.MaxValue && c.TargetEndDate < c.StartDate)
                Add(IssueSeverity.Warning, "Schedule", "Target date is before the start date.", n);
            if (c.StartDate > scenario.HorizonEnd)
                Add(IssueSeverity.Warning, "Schedule", "Starts after the planning horizon.", n);

            if (c.TargetEndDate != DateOnly.MaxValue && scenario.GroupOf(c)?.TargetEndDate is { } phaseEnd
                && c.TargetEndDate > phaseEnd)
                Add(IssueSeverity.Warning, "A-Level",
                    $"Its target ({c.TargetEndDate:yyyy-MM-dd}) is after its A-Level phase target ({phaseEnd:yyyy-MM-dd}).", n);

            if (c.TestPointsPerSortie > 0 && c.TargetEndDate != DateOnly.MaxValue)
            {
                int required = (int)Math.Ceiling(c.TotalTestPoints / c.TestPointsPerSortie);
                var from = c.StartDate > scenario.HorizonStart ? c.StartDate : scenario.HorizonStart;
                var to = c.TargetEndDate < scenario.HorizonEnd ? c.TargetEndDate : scenario.HorizonEnd;
                int workdays = WorkdaysBetween(scenario.Calendar, c, from, to);
                if (required > 0 && workdays < required)
                    Add(IssueSeverity.Warning, "Schedule",
                        $"{required} sorties in {workdays} working days requires more than one sortie per day.", n);
            }
            if (!c.UseGlobalCalendar && c.WeekendDays.Count >= 7)
                Add(IssueSeverity.Warning, "Calendar",
                    "Its own working calendar has no working weekday — it flies only on authorized extra workdays.", n);

            foreach (var pid in c.PrerequisiteCampaignIds)
                if (scenario.Campaigns.All(x => x.Id != pid))
                    Add(IssueSeverity.Warning, "Prerequisites", "References a prerequisite campaign that does not exist.", n);

            if (c.LocationId.Length > 0 && !locationIds.Contains(c.LocationId))
                Add(IssueSeverity.Error, "Location", "Assigned to a location that does not exist.", n);

            if (c.Venue == CampaignVenue.Aircraft)
                foreach (var extId in c.RequiredExternalSupportIds.Distinct())
                {
                    var asset = scenario.ExternalSupports.FirstOrDefault(a => a.Id == extId);
                    if (asset is null)
                    {
                        Add(IssueSeverity.Error, "External support",
                            "Required external support asset does not exist.", n);
                        continue;
                    }
                    if (asset.AvailabilityModel is { } model
                        && c.TargetEndDate != DateOnly.MaxValue && c.TargetEndDate >= c.StartDate)
                    {
                        bool anyDay = false;
                        for (var d = c.StartDate; d <= c.TargetEndDate && !anyDay; d = d.AddDays(1))
                            anyDay = model.IsFlyable(d);
                        if (!anyDay)
                            Add(IssueSeverity.Warning, "External support",
                                $"'{asset.Name}' is unavailable throughout the campaign window.", n);
                    }
                }

            if (c.CompatibleSimulatorIds.Count > 0 && simsByBase.TryGetValue(c.LocationId, out var baseSims)
                && !c.CompatibleSimulatorIds.Any(id => baseSims.Any(s => s.Id == id)))
                Add(IssueSeverity.Warning, "Simulators",
                    "Selected simulators are at other bases; no rehearsals can be scheduled.", n);

            if (pilots > 0 && c.TargetEndDate != DateOnly.MaxValue && c.TargetEndDate >= c.StartDate &&
                c.AssignedPilotIds.Where(pilotById.ContainsKey)
                    .All(id => OnLeaveWhole(pilotById[id], c.StartDate, c.TargetEndDate)))
                Add(IssueSeverity.Warning, "Crew", "Every assigned pilot is on leave for the entire campaign window.", n);
        }

        ValidatePins(scenario, campaigns, Add);

        foreach (var name in PrerequisiteCycles(campaigns))
            Add(IssueSeverity.Error, "Prerequisites", "Prerequisite cycle prevents scheduling.", name);

        foreach (var g in scenario.CampaignGroups)
        {
            if (g.TargetStartDate is { } gs && g.TargetEndDate is { } ge && ge < gs)
                Add(IssueSeverity.Error, "A-Level", $"A-Level phase '{g.Name}' ends before it starts.");
            if (g.TargetEndDate is { } gend && scenario.MasterDeadline is { } master && gend > master)
                Add(IssueSeverity.Warning, "A-Level",
                    $"A-Level phase '{g.Name}' target ({gend:yyyy-MM-dd}) is after the master deadline ({master:yyyy-MM-dd}).");
        }
        foreach (var name in GroupPrerequisiteCycles(scenario.CampaignGroups))
            Add(IssueSeverity.Error, "A-Level", $"Phase '{name}' has a prerequisite cycle.");

        return issues.OrderBy(i => i.Severity).ToList();
    }

    private static bool RatedOn<T>(Dictionary<string, T> by, string id, string tail) where T : Resource =>
        by.TryGetValue(id, out var r) && (r.TypeRatings.Count == 0 || r.TypeRatings.Contains(tail));

    private static bool OnLeaveWhole(Resource r, DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (!r.IsOnLeave(d)) return false;
        return true;
    }

    private static int WorkdaysBetween(FlightCalendar cal, Campaign c, DateOnly from, DateOnly to)
    {
        if (to < from) return 0;
        int n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (cal.IsWorkday(c, d)) n++;
        return n;
    }

    private static IEnumerable<string> PrerequisiteCycles(List<Campaign> campaigns)
    {
        var byId = campaigns.ToDictionary(c => c.Id);
        var state = new Dictionary<string, int>();
        var onCycle = new HashSet<string>();

        bool Dfs(string id)
        {
            if (!byId.ContainsKey(id)) return false;
            state[id] = 1;
            bool found = false;
            foreach (var pid in byId[id].PrerequisiteCampaignIds)
            {
                if (!byId.ContainsKey(pid)) continue;
                if (state.GetValueOrDefault(pid) == 1) { onCycle.Add(pid); found = true; }
                else if (state.GetValueOrDefault(pid) == 0 && Dfs(pid)) found = true;
            }
            state[id] = 2;
            if (found) onCycle.Add(id);
            return found;
        }

        foreach (var c in campaigns)
            if (state.GetValueOrDefault(c.Id) == 0) Dfs(c.Id);

        return campaigns.Where(c => onCycle.Contains(c.Id)).Select(c => c.Name);
    }

    private static IEnumerable<string> GroupPrerequisiteCycles(List<CampaignGroup> groups)
    {
        var byId = new Dictionary<string, CampaignGroup>();
        foreach (var g in groups) byId[g.Id] = g;
        var state = new Dictionary<string, int>();
        var onCycle = new HashSet<string>();

        bool Dfs(string id)
        {
            if (!byId.ContainsKey(id)) return false;
            state[id] = 1;
            bool found = false;
            foreach (var pid in byId[id].PrerequisiteGroupIds)
            {
                if (!byId.ContainsKey(pid)) continue;
                if (state.GetValueOrDefault(pid) == 1) { onCycle.Add(pid); found = true; }
                else if (state.GetValueOrDefault(pid) == 0 && Dfs(pid)) found = true;
            }
            state[id] = 2;
            if (found) onCycle.Add(id);
            return found;
        }

        foreach (var g in groups)
            if (state.GetValueOrDefault(g.Id) == 0) Dfs(g.Id);

        return groups.Where(g => onCycle.Contains(g.Id)).Select(g => g.Name);
    }
}
