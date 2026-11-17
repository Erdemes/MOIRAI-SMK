using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.Tests;

public class ReferenceScenarioTests
{
    private static readonly DateOnly HStart = new(2025, 6, 2);
    private static readonly DateOnly HEnd = new(2025, 8, 31);
    private static readonly DateOnly FlutTarget = new(2025, 6, 30);

    private static Scenario Reference(double flyRate = 0.7)
    {
        var s = new Scenario
        {
            Config = new ScenarioConfig(),
            Calendar = new FlightCalendar { Holidays = { new DateOnly(2025, 7, 20) } },
            FlyableModel = new RateBasedFlyableDays { Rate = flyRate, PatternLength = 5, Anchor = HStart },
            HorizonStart = HStart,
            HorizonEnd = HEnd,
        };

        s.Pilots.AddRange(new[] { "P1", "P2", "P3" }.Select(id => new Pilot { Id = id, Name = id }));
        for (int i = 1; i <= 6; i++) s.Engineers.Add(new Engineer { Id = $"E{i}", Name = $"E{i}" });
        foreach (var t in new[] { "TC-001", "TC-002", "TC-003" }) s.Aircraft.Add(new Aircraft { Id = t, Name = t });
        s.Simulators.Add(new Simulator { Id = "SIM", Name = "Main sim", LocationId = "" });

        s.Aircraft.Single(a => a.Id == "TC-003").MaintenanceWindows.Add(
            new DateRange { Start = new DateOnly(2025, 6, 9), End = new DateOnly(2025, 6, 30) });

        s.Campaigns.Add(new Campaign
        {
            Id = "FT-PERF", Name = "FT-PERF",
            TotalTestPoints = 30, TestPointsPerSortie = 5,
            CompatibleAircraftIds = { "TC-001" },
            AssignedPilotIds = { "P1", "P2" }, AssignedEngineerIds = { "E1", "E2", "E3" },
            StartDate = HStart, TargetEndDate = new DateOnly(2025, 6, 30),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5, ExpectedRehearsalDutyHours = 2,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "FT-ENV", Name = "FT-ENV",
            TotalTestPoints = 24, TestPointsPerSortie = 4,
            CompatibleAircraftIds = { "TC-001", "TC-002" },
            AssignedPilotIds = { "P1", "P2" }, AssignedEngineerIds = { "E1", "E2", "E3" },
            PrerequisiteCampaignIds = { "FT-PERF" },
            StartDate = HStart, TargetEndDate = new DateOnly(2025, 7, 31),
            ExpectedSortieFlightHours = 2, ExpectedSortieDutyHours = 5, ExpectedRehearsalDutyHours = 2,
        });
        s.Campaigns.Add(new Campaign
        {
            Id = "FT-FLUT", Name = "FT-FLUT",
            TotalTestPoints = 18, TestPointsPerSortie = 3,
            CompatibleAircraftIds = { "TC-003" },
            AssignedPilotIds = { "P3" }, AssignedEngineerIds = { "E4", "E5", "E6" },
            StartDate = new DateOnly(2025, 6, 9), TargetEndDate = FlutTarget,
            ExpectedSortieFlightHours = 1.5, ExpectedSortieDutyHours = 4, ExpectedRehearsalDutyHours = 2,
            BlocksPerSortie = 3,
        });

        return s;
    }

    private static Scenario ReferenceMc()
    {
        var s = Reference();
        s.FlyableModel = ProbabilisticFlyableDays.Uniform(0.7, 0.5, HStart);
        return s;
    }

    private static IEnumerable<Sortie> ForCampaign(Schedule sch, string campId) =>
        sch.Sorties.Where(x => x.CampaignId == campId);

    [Fact]
    public void FT_PERF_and_FT_ENV_meet_their_deadlines()
    {
        var s = Reference();
        var sch = new Scheduler().Run(s);

        foreach (var id in new[] { "FT-PERF", "FT-ENV" })
        {
            var c = s.Campaigns.Single(x => x.Id == id);
            Assert.True(sch.CampaignCompleted[id], $"{id} should complete");
            Assert.Equal(c.RequiredSorties(), ForCampaign(sch, id).Count());
            Assert.True(sch.MeetsDeadline(id, c.TargetEndDate), $"{id} should meet its deadline");
        }
    }

    [Fact]
    public void FT_ENV_starts_only_after_its_prerequisite_FT_PERF_completes()
    {
        var sch = new Scheduler().Run(Reference());

        var perfEnd = sch.CampaignEndDates["FT-PERF"]!.Value;
        var envFirst = ForCampaign(sch, "FT-ENV").Min(x => x.Date);
        Assert.True(envFirst > perfEnd, "FT-ENV must not fly until FT-PERF is complete");
    }

    [Fact]
    public void FT_FLUT_slips_past_its_target_because_its_only_tail_is_in_maintenance()
    {
        var s = Reference();
        var sch = new Scheduler().Run(s);

        Assert.True(sch.CampaignCompleted["FT-FLUT"]);
        Assert.False(sch.MeetsDeadline("FT-FLUT", FlutTarget));
        Assert.Equal(CampaignStatus.Late, sch.StatusOf(s, s.Campaigns.Single(c => c.Id == "FT-FLUT")));

        Assert.All(ForCampaign(sch, "FT-FLUT"), x => Assert.True(x.Date > new DateOnly(2025, 6, 30)));

        var dom = sch.Bottlenecks["FT-FLUT"].Dominant();
        Assert.NotNull(dom);
        Assert.Equal("aircraft", dom!.Value.Reason);
    }

    [Fact]
    public void FT_FLUT_flies_at_most_one_whole_day_sortie_per_day()
    {
        var sch = new Scheduler().Run(Reference());

        var sorties = ForCampaign(sch, "FT-FLUT").ToList();
        Assert.Equal(6, sorties.Count);
        Assert.All(sorties, x => Assert.Equal("TC-003", x.AircraftId));
        Assert.All(sorties, x => Assert.Equal(1, x.Block));
        Assert.Equal(sorties.Count, sorties.Select(x => x.Date).Distinct().Count());
    }

    [Fact]
    public void No_rehearsal_falls_on_a_weekend_or_holiday_and_each_sits_in_a_crew_block()
    {
        var s = Reference();
        var sch = new Scheduler().Run(s);

        Assert.All(sch.Rehearsals, r => Assert.True(s.Calendar.IsWorkday(r.Date),
            $"rehearsal on {r.Date:yyyy-MM-dd} must be a workday"));
        Assert.All(sch.Rehearsals, r => Assert.InRange(r.Block, 1, s.Config.BlocksPerDay));

        Assert.Equal(sch.Sorties.Count, sch.Rehearsals.Count);
    }

    [Fact]
    public void Weekly_load_balancing_spreads_FT_ENV_across_both_pilots()
    {
        var sch = new Scheduler().Run(Reference());

        var byPilot = ForCampaign(sch, "FT-ENV").GroupBy(x => x.PilotId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, byPilot.Count);
        Assert.True(Math.Abs(byPilot["P1"] - byPilot["P2"]) <= 2,
            $"FT-ENV pilot load unbalanced: {string.Join(", ", byPilot.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    [Fact]
    public void SuggestResources_proposes_an_extra_aircraft_and_then_all_campaigns_meet()
    {
        var result = ResourceSuggester.Suggest(Reference(), maxAdditions: 3);

        Assert.True(result.AllMet, result.Summary);
        Assert.Contains(result.Steps, st => st.Addition.Contains("aircraft"));

        var sch = new Scheduler().Run(result.FinalScenario);
        Assert.True(sch.MeetsDeadline("FT-FLUT", FlutTarget));
    }

    [Fact]
    public void MonteCarlo_reports_FT_FLUT_confidence_far_below_the_others()
    {
        var result = MonteCarlo.Analyze(ReferenceMc(), runs: 200, seed: 42);

        double P(string id) => result.Campaigns.Single(c => c.CampaignId == id).PMeet;

        Assert.True(P("FT-FLUT") < 0.05, $"FT-FLUT PMeet={P("FT-FLUT")}");
        Assert.True(P("FT-PERF") > 0.5, $"FT-PERF PMeet={P("FT-PERF")}");
        Assert.True(P("FT-PERF") - P("FT-FLUT") > 0.4, "FT-FLUT must be far more fragile than FT-PERF");
    }

    [Fact]
    public void SuggestResourcesMC_raises_FT_FLUT_confidence_with_an_extra_aircraft()
    {
        var before = MonteCarlo.Analyze(ReferenceMc(), runs: 100, seed: 7);
        double flutBefore = before.Campaigns.Single(c => c.CampaignId == "FT-FLUT").PMeet;

        var result = ResourceSuggester.SuggestMc(
            ReferenceMc(), runs: 100, targetConfidence: 0.90, maxAdditions: 3, seed: 7);

        Assert.NotEmpty(result.Steps);
        Assert.Contains("aircraft", result.Steps[0].Addition);

        var after = MonteCarlo.Analyze(result.FinalScenario, runs: 100, seed: 7);
        double flutAfter = after.Campaigns.Single(c => c.CampaignId == "FT-FLUT").PMeet;
        Assert.True(flutAfter > flutBefore + 0.5,
            $"FT-FLUT confidence should jump: {flutBefore:P0} → {flutAfter:P0}");
    }

    [Fact]
    public void Recommend_uses_only_the_enabled_levers()
    {
        var withAircraft = ResourceSuggester.Recommend(
            ReferenceMc(), ResourceSuggester.RecommenderLevers.AddAircraft,
            runs: 100, targetConfidence: 0.90, maxSteps: 3, seed: 7);
        Assert.Contains(withAircraft.Steps, s => s.Addition.Contains("aircraft"));

        const ResourceSuggester.RecommenderLevers noAircraft =
            ResourceSuggester.RecommenderLevers.AddPilot
            | ResourceSuggester.RecommenderLevers.AddEngineer
            | ResourceSuggester.RecommenderLevers.ReassignPilot
            | ResourceSuggester.RecommenderLevers.ReassignEngineer
            | ResourceSuggester.RecommenderLevers.RaiseDailySorties
            | ResourceSuggester.RecommenderLevers.AddBlock
            | ResourceSuggester.RecommenderLevers.RaiseTestPoints;
        var without = ResourceSuggester.Recommend(
            ReferenceMc(), noAircraft, runs: 100, targetConfidence: 0.90, maxSteps: 3, seed: 7);
        Assert.DoesNotContain(without.Steps, s => s.Addition.Contains("aircraft"));
        Assert.False(without.AllMet);
    }
}
