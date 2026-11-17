using MOIRAI.Analysis;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Scheduling.Revising;
using MOIRAI.Reporting;
using Xunit.Abstractions;

namespace MOIRAI.Tests;

public class AuditRegressionTests(ITestOutputHelper output)
{
    static readonly DateOnly D = new(2026, 6, 1);
    static Scenario Make()
    {
        var s = new Scenario {
            HorizonStart = D, HorizonEnd = D.AddDays(20),
            Calendar = new FlightCalendar { WeekendDays = new() },
            FlyableModel = ProbabilisticFlyableDays.Uniform(1, 0, D),
            Config = new ScenarioConfig { PilotsPerSortie = 1, EngineersPerSortie = 1, BlocksPerDay = 1 }
        };
        s.Pilots.Add(new Pilot { Id = "p1", Name = "P1" });
        s.Engineers.Add(new Engineer { Id = "e1", Name = "E1" });
        s.Aircraft.Add(new Aircraft { Id = "a1", Name = "A1" });
        s.Simulators.Add(new Simulator { Id = "s1", Name = "S1" });
        s.Campaigns.Add(new Campaign { Id = "c1", Name = "C1", StartDate = D,
            TargetEndDate = DateOnly.MaxValue, TotalTestPoints = 1, TestPointsPerSortie = 1,
            RehearsalRequired = false, CompatibleAircraftIds = { "a1" },
            AssignedPilotIds = { "p1" }, AssignedEngineerIds = { "e1" } });
        return s;
    }
    static ISchedulerService Engine(bool advanced) => advanced ? new RevisingScheduler(new RevisingOptions {
        EnableLocalRepair = true, EnableTightnessOrder = true, EnableScarcityCrew = true,
        EnableBottleneckSteer = true, EnableRestStagger = true
    }) : new Scheduler();
    [Theory][InlineData(false)][InlineData(true)] public void DutyWindows(bool advanced)
    {
        var s = Make(); s.Campaigns[0].RehearsalRequired = true;
        s.Config.MaxDutyHoursPer7Days = 5;
        foreach (var engine in new ISchedulerService[] { Engine(advanced) }) {
            var r = engine.Run(s);
            double duty = r.Sorties.Count * 4 + r.Rehearsals.Count * 2;
            output.WriteLine($"{engine.GetType().Name}: sorties={r.Sorties.Count}, rehearsals={r.Rehearsals.Count}, duty={duty}, cap=5");
            Assert.True(duty <= 5, $"7-day duty {duty} exceeds cap 5");
        }
    }
    [Theory][InlineData(false)][InlineData(true)] public void BankedCrew(bool advanced)
    {
        var s = Make(); s.Campaigns[0].RehearsalRequired = true; s.FreezeDate = D.AddDays(2);
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" }); s.Campaigns[0].AssignedPilotIds.Add("p2");
        s.Pilots[0].LeaveWindows.Add(new DateRange { Start = s.FreezeDate.Value, End = s.HorizonEnd });
        s.ActualRehearsals.Add(new Rehearsal { CampaignId = "c1", Date = D.AddDays(1), Block = 1,
            SortieNo = 1, SortieDate = D.AddDays(3), PilotId = "p1", EngineerIds = new() { "e1" }, IsActual = true });
        var r = Engine(advanced).Run(s);
        output.WriteLine($"sortie pilot={r.Sorties.Single().PilotId}; rehearsal pilots={string.Join(',', r.Rehearsals.Select(x=>x.PilotId))}");
        Assert.Contains(r.Rehearsals, x => x.PilotId == r.Sorties.Single().PilotId);
    }
    [Theory][InlineData(false)][InlineData(true)] public void DeploymentPrerequisite(bool advanced)
    {
        var s = Make(); s.Locations.Add(new Location { Id = "remote", Name = "Remote", SetupDays = 0 });
        s.Campaigns[0].LocationId = "remote";
        var c2 = ScenarioJson.Clone(s).Campaigns[0]; c2.Id = "c2"; c2.Name = "C2";
        c2.PrerequisiteCampaignIds.Add("c1"); s.Campaigns.Add(c2);
        var r = Engine(advanced).Run(s);
        output.WriteLine($"remote: completed={r.AllCampaignsCompleted()}, sorties={r.Sorties.Count}, deployments={r.Deployments.Count}");
        foreach (var c in s.Campaigns) c.LocationId = "";
        output.WriteLine($"main: completed={new Scheduler().Run(s).AllCampaignsCompleted()}");
        Assert.True(r.AllCampaignsCompleted());
        Assert.True(r.CampaignEndDates["c1"] < r.Sorties.Single(x => x.CampaignId == "c2").Date);
    }
    [Fact] public void CriticalSimulator()
    {
        var s = Make(); s.Campaigns[0].Venue = CampaignVenue.Simulator;
        s.Campaigns[0].CompatibleSimulatorIds.Add("s1");
        s.Simulators.Add(new Simulator { Id = "s2", Name = "S2" });
        var r = CriticalityAnalysis.Analyze(s, maxFloatDays: 1);
        var device = r.Resources.Single(x => x.ResourceId == "s1");
        output.WriteLine($"Only compatible device S1: critical={device.IsCritical}, newlyIncomplete={device.NewlyIncomplete}");
        Assert.True(device.IsCritical);
    }
    [Fact] public void OptimizerMasterDeadline()
    {
        var s = Make(); s.MasterDeadline = D.AddDays(1); s.Campaigns[0].TotalTestPoints = 2;
        s.Aircraft[0].MaintenanceWindows.Add(new DateRange { Start = D, End = D });
        var r = Optimizer.Optimize(s, new Optimizer.Options { Levers = Optimizer.Levers.Maintenance,
            MaxShiftDays = 5, Iterations = 80, SearchRuns = 2, FinalRuns = 2, DisruptionWeight = 0 });
        s.Aircraft[0].MaintenanceWindows[0].Start = D.AddDays(5); s.Aircraft[0].MaintenanceWindows[0].End = D.AddDays(5);
        var known = MonteCarlo.Analyze(s, runs: 2);
        output.WriteLine($"optimizer: improved={r.Improved}, score={r.BaselineScore}->{r.OptimizedScore}, delivery={r.BaselinePDelivery}->{r.OptimizedPDelivery}; feasible shift delivery={known.PDelivery}");
        Assert.True(r.OptimizedPDelivery >= known.PDelivery);
    }
    [Fact] public void SensitivityAllFactors()
    {
        var s = Make(); s.MasterDeadline = D.AddDays(4);
        s.Locations.Add(new Location { Id = "remote", RangeModel = ProbabilisticFlyableDays.Uniform(.8, .2, D) });
        s.Campaigns[0].LocationId = "remote";
        s.ExternalSupports.Add(new ExternalSupport { Id = "x", AvailabilityModel = ProbabilisticFlyableDays.Uniform(.8, .2, D) });
        s.Campaigns[0].RequiredExternalSupportIds.Add("x");
        s.Campaigns[0].TestEfficiencyEnabled = true; s.Campaigns[0].AbortProbability = .1;
        s.Campaigns[0].RetestProbabilityStart = .2; s.Aircraft[0].FailuresPer100FlightHours = 5;
        var r = MonteCarlo.Sensitivity(s, runs: 2, sobolSamples: 16);
        output.WriteLine($"sweeps={r.Sweeps.Count}, global={r.GlobalImportance.Count}; missing={string.Join(',', r.Sweeps.Select(x=>x.Name).Except(r.GlobalImportance.Select(x=>x.Name)))}");
        Assert.Equal(r.Sweeps.Count, r.GlobalImportance.Count);
    }
    [Fact] public void RepeatedResourceAddition()
    {
        Scenario Problem(string id) {
            var s = Make(); s.HorizonEnd = D; s.MasterDeadline = D;
            s.Config.EngineersPerSortie = 0; s.Campaigns[0].AssignedEngineerIds.Clear();
            s.Pilots[0].Id = id; s.Campaigns[0].AssignedPilotIds = new() { id };
            var c2 = ScenarioJson.Clone(s).Campaigns[0]; c2.Id = "c2"; c2.Name = "C2";
            s.Aircraft.Add(new Aircraft { Id = "a2", Name = "A2" }); c2.CompatibleAircraftIds = new() { "a2" }; s.Campaigns.Add(c2);
            return s;
        }
        var opts = new Optimizer.Options { Levers = Optimizer.Levers.AddPilots, MaxAddedPilots = 1,
            Iterations = 20, SearchRuns = 2, FinalRuns = 2, ResourceCostWeight = 0, DisruptionWeight = 0 };
        var a = Optimizer.Optimize(Problem("p1"), opts);
        var b = Optimizer.Optimize(Problem("opt-pilot-1"), opts);
        output.WriteLine($"ordinary pilot ID: score {a.BaselineScore}->{a.OptimizedScore}, pilots={a.Optimized.Pilots.Count}; prior optimizer ID: score {b.BaselineScore}->{b.OptimizedScore}, pilots={b.Optimized.Pilots.Count}");
        Assert.Equal(a.OptimizedScore, b.OptimizedScore);
        var applied = Problem("opt-pilot-1");
        foreach (var change in b.Changes) change.Apply(applied);
        var ids = applied.Pilots.Select(p => p.Id).OrderBy(id => id).ToArray();
        Assert.Equal(b.Optimized.Pilots.Select(p => p.Id).OrderBy(id => id), ids);
        foreach (var change in b.Changes) change.Apply(applied);
        Assert.Equal(ids, applied.Pilots.Select(p => p.Id).OrderBy(id => id));
    }
    [Fact] public void FloatBreaksOtherCampaign()
    {
        var s = Make();
        var c2 = ScenarioJson.Clone(s).Campaigns[0]; c2.Id = "c2"; c2.Name = "C2";
        c2.StartDate = D.AddDays(1); c2.CompatibleAircraftIds = new() { "a2" };
        s.Aircraft.Add(new Aircraft { Id = "a2", Name = "A2", MaintenanceWindows = {
            new DateRange { Start = D.AddDays(2), End = s.HorizonEnd } } }); s.Campaigns.Add(c2);
        var r = CriticalityAnalysis.Analyze(s, maxFloatDays: 3);
        var a = r.Campaigns.Single(x => x.CampaignId == "c1");
        s.Campaigns[0].StartDate = D.AddDays(1); var delayed = new Scheduler().Run(s);
        output.WriteLine($"baseline complete={r.BaselineComplete}, reported C1 float={a.FloatDays}; after one day delay C2 complete={delayed.CampaignCompleted["c2"]}");
        Assert.Equal(0, a.FloatDays);
    }
    [Fact] public void ConstantSensitivityInteraction()
    {
        var s = Make(); s.Campaigns[0].Venue = CampaignVenue.Simulator;
        s.MasterDeadline = D.AddDays(10);
        var r = MonteCarlo.Sensitivity(s, runs: 2, sobolSamples: 16);
        output.WriteLine($"all sweep PDelivery={string.Join(',',r.Sweeps.SelectMany(x=>x.Points).Select(x=>x.PDelivery).Distinct())}; direct={r.GlobalImportance.Sum(x=>x.DirectShare)}; total={r.GlobalImportance.Sum(x=>x.TotalShare)}; interactions={r.JointInteractionShare}");
        Assert.Equal(0, r.JointInteractionShare);
        Assert.False(r.HasGlobalVariation);
        Assert.Equal(32, r.GlobalEvaluations);
    }
    [Theory][InlineData(false)][InlineData(true)] public void ReplayedAdditionalPilotDuty(bool advanced)
    {
        var s = Make(); s.Config.EngineersPerSortie = 0; s.Campaigns[0].AssignedEngineerIds.Clear();
        s.Config.MaxDutyHoursPer7Days = 6; s.Campaigns[0].PilotsPerSortie = 2;
        s.Campaigns[0].RehearsalRequired = true;
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" }); s.Campaigns[0].AssignedPilotIds.Add("p2");
        var first = Engine(advanced).Run(s);
        s.ActualSorties = first.Sorties; s.ActualRehearsals = first.Rehearsals;
        foreach (var x in s.ActualSorties) x.IsActual = true;
        foreach (var x in s.ActualRehearsals) x.IsActual = true;
        s.FreezeDate = D.AddDays(2);
        var c2 = ScenarioJson.Clone(s).Campaigns[0]; c2.Id = "c2"; c2.Name = "C2";
        c2.PilotsPerSortie = 1; c2.AssignedPilotIds = new() { "p2" }; c2.RehearsalRequired = false;
        c2.ExpectedSortieDutyHours = 1; c2.StartDate = s.FreezeDate.Value; s.Campaigns.Add(c2);
        var next = Engine(advanced).Run(s); var flight = next.Sorties.Single(x=>x.CampaignId == "c2");
        output.WriteLine($"first sortie additional pilot={string.Join(',', first.Sorties[0].AdditionalPilotIds)}; first duty=6; second flight={flight.Date}; 7-day cap=6");
        Assert.True(flight.Date >= D.AddDays(7));
    }
    [Fact] public void MultiShipResourceReport()
    {
        var s = Make(); s.Campaigns[0].SupportAircraftPerSortie = 1;
        s.Campaigns[0].SupportAircraftIds.Add("a2");
        s.Aircraft.Add(new Aircraft { Id = "a2", Name = "A2" });
        s.Pilots.Add(new Pilot { Id = "p2", Name = "P2" }); s.Campaigns[0].AssignedPilotIds.Add("p2");
        var r = new Scheduler().Run(s); var report = ProgramReporter.Build(s, r);
        output.WriteLine($"support aircraft={string.Join(',',r.Sorties.Single().SupportAircraftIds)}; support pilots={string.Join(',',r.Sorties.Single().SupportPilotIds)}; reported aircraft={report.AircraftUsed}, crew={report.CrewUsed}, flight hours={report.TotalFlightHours}");
        Assert.Equal(2, report.AircraftUsed);
        Assert.Equal(3, report.CrewUsed);
        Assert.Equal(2, report.TotalFlightHours);
    }
    [Fact] public void CorruptSessionIsPreserved()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"moirai-audit-{Guid.NewGuid():N}.json");
        string? preserved = null;
        const string corrupt = "{\"scenarios\":[{\"name\":\"Recoverable work\"},";
        try {
            System.IO.File.WriteAllText(path, corrupt);
            var repo = new JsonRepository(path);
            preserved = repo.RecoveryPath;
            Assert.NotNull(preserved);
            Assert.NotNull(repo.RecoveryMessage);
            var after = System.IO.File.ReadAllText(preserved);
            Assert.Equal(corrupt, after);
        } finally {
            System.IO.File.Delete(path);
            if (preserved is not null) System.IO.File.Delete(preserved);
        }
    }

    [Fact]
    public void Corrupt_session_recovers_the_last_valid_backup()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"moirai-backup-{Guid.NewGuid():N}.json");
        string? preserved = null;
        try
        {
            var repo = new JsonRepository(path);
            repo.SetSessionName("Saved work");
            repo.SaveSession();
            const string corrupt = "{broken";
            System.IO.File.WriteAllText(path, corrupt);
            var recovered = new JsonRepository(path);
            preserved = recovered.RecoveryPath;
            Assert.Equal("Saved work", recovered.GetSession().Name);
            Assert.Equal(corrupt, System.IO.File.ReadAllText(preserved!));
            Assert.Equal("Saved work", new JsonRepository(path).GetSession().Name);
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(path + ".bak");
            if (preserved is not null) System.IO.File.Delete(preserved);
        }
    }

    [Theory]
    [InlineData(7)] [InlineData(14)] [InlineData(28)]
    public void Past_insertion_checks_the_last_affected_duty_window(int width)
    {
        var cfg = new ScenarioConfig {
            MaxDutyHoursPer7Days = width == 7 ? 5 : 100,
            MaxDutyHoursPer14Days = width == 14 ? 5 : 100,
            MaxDutyHoursPer28Days = width == 28 ? 5 : 100
        };
        var person = new PersonIndex(0);
        var ledger = new CrewLedger(new Resource[] { new Pilot { Id = "p" } },
            Enumerable.Range(0, 40).Select(i => D.AddDays(i)).ToArray(), 3, cfg);
        ledger.AddSortie(new[] { person }, width - 1, 1, 4, 0);
        Assert.False(ledger.CanRehearse(person, 0, 2));
        Assert.True(ledger.CanRehearse(person, 0, 1));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void An_expired_banked_rehearsal_requires_a_new_one(bool advanced)
    {
        var s = Make(); s.Campaigns[0].RehearsalRequired = true; s.FreezeDate = D.AddDays(8);
        s.ActualRehearsals.Add(new Rehearsal { CampaignId = "c1", Date = D, SortieNo = 1,
            SortieDate = D.AddDays(1), PilotId = "p1", EngineerIds = new() { "e1" }, IsActual = true });
        var schedule = Engine(advanced).Run(s);
        var rehearsal = Assert.Single(schedule.Rehearsals, r => !r.IsActual);
        Assert.True(rehearsal.Date >= s.FreezeDate);
        Assert.Equal(rehearsal.Date, Assert.Single(schedule.Sorties).RehearsalDate);
    }

    [Fact]
    public void Sobol_includes_the_seventh_parameter_numerically()
    {
        var ranges = Enumerable.Repeat((0.0, 1.0), 7).ToArray();
        var (first, total, evaluations) = MonteCarlo.ComputeSobol(7, ranges, x => x[6], 4096);
        Assert.InRange(first[6], .98, 1.02);
        Assert.InRange(total[6], .98, 1.02);
        Assert.All(first.Take(6), x => Assert.InRange(x, -.01, .01));
        Assert.Equal(4096 * 9, evaluations);
    }
}
