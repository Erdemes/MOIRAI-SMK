namespace MOIRAI.Scheduling;

public sealed class ScenarioConfig
{
    public int BlocksPerDay { get; set; } = 3;

    public int MaxSortiesPerPersonPerDay { get; set; } = 6;

    public int MaxRehearsalsPerPersonPerDay { get; set; } = 1;

    public int EngineersPerSortie { get; set; } = 2;

    public int PilotsPerSortie { get; set; } = 1;

    public int MaxRehearsalGapDays { get; set; } = 2;

    public bool RehearsalOnWorkdaysOnly { get; set; } = true;

    public double MaxFlightHoursPerDay { get; set; } = 6;
    public double MaxFlightHoursPer7Days { get; set; } = 30;
    public double MaxFlightHoursPer28Days { get; set; } = 100;
    public double MaxFlightHoursPerYear { get; set; } = 900;

    public double MaxDutyHoursPerDay { get; set; } = 12;
    public double MaxDutyHoursPer7Days { get; set; } = 60;
    public double MaxDutyHoursPer14Days { get; set; } = 110;
    public double MaxDutyHoursPer28Days { get; set; } = 190;

    public double MaxFlightDutyHoursPerDay { get; set; } = double.PositiveInfinity;

    public double MinRestHours { get; set; } = 12;

    public int MaxConsecutiveFlyingDays { get; set; } = 6;

    public int LoadBalanceWindowDays { get; set; } = 7;

    public bool SimulateYieldInNominal { get; set; }

    public bool OverrideSortieAbort { get; set; }

    public double SortieAbortOverride { get; set; } = 0.05;

    public double? AbortOverrideOrNull => OverrideSortieAbort ? SortieAbortOverride : null;

    public bool UseRevisingEngine { get; set; }

    public bool McUseAdvancedEngine { get; set; }

    public ScenarioConfig Clone() => (ScenarioConfig)MemberwiseClone();
}
