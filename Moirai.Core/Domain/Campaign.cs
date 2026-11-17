using MOIRAI.Scheduling;

namespace MOIRAI.Domain;

public enum RetestModel
{
    Exponential = 0,

    PowerLaw = 1,
}

public enum CampaignVenue
{
    Aircraft = 0,

    Simulator = 1,
}

public sealed class Campaign
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public double TotalTestPoints { get; set; }

    public double TestPointsPerSortie { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly TargetEndDate { get; set; }

    public List<string> CompatibleAircraftIds { get; set; } = new();

    public string LocationId { get; set; } = string.Empty;

    public CampaignVenue Venue { get; set; } = CampaignVenue.Aircraft;

    public int SupportAircraftPerSortie { get; set; }

    public List<string> SupportAircraftIds { get; set; } = new();

    public bool PreferSingleAircraft { get; set; }

    public double? PriorityKey { get; set; }

    public List<string> AssignedPilotIds { get; set; } = new();

    public List<string> AssignedEngineerIds { get; set; } = new();

    public List<string> PrerequisiteCampaignIds { get; set; } = new();

    public List<string> PrerequisiteMaintenanceIds { get; set; } = new();

    public int BlocksPerSortie { get; set; } = 1;

    public List<int> AllowedBlocks { get; set; } = new();

    public int MinWorkdaysBetweenSorties { get; set; }

    public int MaxSortiesPerDay { get; set; }

    public int PostCompletionReviewWorkdays { get; set; }

    public List<string> RequiredExternalSupportIds { get; set; } = new();

    public int RestDaysAfterCompletion { get; set; }

    public bool UseGlobalCalendar { get; set; } = true;

    public HashSet<DayOfWeek> WeekendDays { get; set; } = new() { DayOfWeek.Saturday, DayOfWeek.Sunday };

    public bool WorkOnHolidays { get; set; }

    public bool UseGlobalFlyable { get; set; } = true;

    public FlyableDayModel? FlyableModel { get; set; }

    public FlyableDayModel? OwnFlyableModel() => UseGlobalFlyable ? null : FlyableModel;

    public bool UseGlobalLimits { get; set; } = true;

    public CrewLimits? Limits { get; set; }

    public ScenarioConfig EffectiveConfig(ScenarioConfig scenario) =>
        UseGlobalLimits || Limits is null ? scenario : Limits.ApplyTo(scenario);

    public List<string> CompatibleSimulatorIds { get; set; } = new();

    public bool RehearsalRequired { get; set; } = true;

    public int PilotsPerSortie { get; set; }
    public int EngineersPerSortie { get; set; }

    public double ExpectedSortieFlightHours { get; set; } = 1;

    public double ExpectedSortieDutyHours { get; set; } = 4;

    public double ExpectedSortieFlightDutyHours { get; set; }

    public double ExpectedRehearsalDutyHours { get; set; } = 2;

    public bool TestEfficiencyEnabled { get; set; }

    public double RetestProbabilityStart { get; set; }

    public double RetestProbabilityFloor { get; set; }

    public double RetestLearningScale { get; set; }

    public RetestModel RetestCurve { get; set; } = RetestModel.Exponential;

    public double RetestExponent { get; set; } = 1;

    public double AbortProbability { get; set; }

    public double RetestProbabilityAt(double goodPointsDone)
    {
        if (!TestEfficiencyEnabled) return 0;
        double start = Math.Clamp(RetestProbabilityStart, 0, 0.95);
        double floor = Math.Clamp(RetestProbabilityFloor, 0, start);
        if (RetestLearningScale <= 0 || goodPointsDone <= 0) return start;
        double decay = RetestCurve switch
        {
            RetestModel.PowerLaw => Math.Pow(1 + goodPointsDone / RetestLearningScale, -Math.Max(0, RetestExponent)),
            _ => Math.Exp(-goodPointsDone / RetestLearningScale),
        };
        return floor + (start - floor) * decay;
    }

    public double ExpectedInflation(double? abortOverride = null)
    {
        double abort = Math.Clamp(
            abortOverride ?? (TestEfficiencyEnabled ? AbortProbability : 0), 0, 0.95);
        double pbar = TestEfficiencyEnabled ? Math.Clamp(MeanRetestProbability(), 0, 0.95) : 0;
        return 1.0 / ((1.0 - pbar) * (1.0 - abort));
    }

    private double MeanRetestProbability()
    {
        double start = Math.Clamp(RetestProbabilityStart, 0, 0.95);
        double floor = Math.Clamp(RetestProbabilityFloor, 0, start);
        if (start <= 0) return 0;
        double n = TotalTestPoints, s = RetestLearningScale;
        if (s <= 0 || n <= 0) return start;

        double meanDecay = RetestCurve switch
        {
            RetestModel.PowerLaw => PowerLawMeanDecay(n, s, Math.Max(0, RetestExponent)),
            _ => s / n * (1 - Math.Exp(-n / s)),
        };
        return floor + (start - floor) * meanDecay;
    }

    private static double PowerLawMeanDecay(double n, double s, double beta) =>
        Math.Abs(beta - 1) < 1e-9
            ? s / n * Math.Log(1 + n / s)
            : s / (n * (1 - beta)) * (Math.Pow(1 + n / s, 1 - beta) - 1);

    public int RequiredSorties() =>
        TestPointsPerSortie <= 0
            ? 0
            : (int)Math.Ceiling(TotalTestPoints / TestPointsPerSortie);
}
