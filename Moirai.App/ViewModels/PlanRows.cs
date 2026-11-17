using Avalonia.Media;
using MOIRAI.Reporting;

namespace MOIRAI.ViewModels;

public sealed class ValidationRow
{
    public required IssueSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Text { get; init; }

    public string Glyph => Severity switch
    {
        IssueSeverity.Error => "⛔",
        IssueSeverity.Warning => "⚠",
        _ => "ℹ",
    };

    public IBrush Brush => Severity switch
    {
        IssueSeverity.Error => ErrorBrush,
        IssueSeverity.Warning => WarnBrush,
        _ => InfoBrush,
    };

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFBF1F"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#9AA4B2"));
}

public static class BlockNames
{
    public static string Name(int block, int blocksPerDay) =>
        blocksPerDay == 3
            ? block switch
            {
                1 => "morning",
                2 => "midday",
                3 => "afternoon",
                _ => $"block {block}",
            }
            : $"block {block}";
}

public sealed class ScheduleRow
{
    public DateOnly Date { get; init; }
    public string Block { get; init; } = string.Empty;
    public string Campaign { get; init; } = string.Empty;
    public string SortieNo { get; init; } = string.Empty;
    public string Aircraft { get; init; } = string.Empty;
    public string Pilot { get; init; } = string.Empty;
    public string Engineers { get; init; } = string.Empty;
    public DateOnly? RehearsalDate { get; init; }
}

public sealed class WorkloadRow
{
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public int Sorties { get; init; }
    public double FlightHours { get; init; }
    public double DutyHours { get; init; }
    public int Rehearsals { get; init; }
    public int IdleDays { get; init; }
    public string IdleBreakdown { get; init; } = string.Empty;
}

public sealed class AircraftUtilizationRow
{
    public string Name { get; init; } = string.Empty;
    public int Sorties { get; init; }
    public int DaysUsed { get; init; }
    public string CampaignBreakdown { get; init; } = string.Empty;
    public int IdleDays { get; init; }
    public string IdleBreakdown { get; init; } = string.Empty;
}

public sealed class CampaignDetailRow
{
    public string Name { get; init; } = string.Empty;
    public string Dates { get; init; } = string.Empty;
    public string ActualEnd { get; init; } = string.Empty;
    public string Aircraft { get; init; } = string.Empty;
    public string Pilots { get; init; } = string.Empty;
    public string Engineers { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    public IBrush StatusBrush { get; init; } = Brushes.Gray;

    public bool IsSlipping { get; init; }

    public string Glyph { get; init; } = string.Empty;
    public string Headline { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Advice { get; init; } = string.Empty;
    public string Breakdown { get; init; } = string.Empty;
    public string Phases { get; init; } = string.Empty;

    public bool HasAdvice => Advice.Length > 0;
    public bool HasBreakdown => Breakdown.Length > 0;
    public bool HasPhases => Phases.Length > 0;
}

public sealed class PhaseRollupRow
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IBrush StatusBrush { get; init; } = Brushes.Gray;
    public string Detail { get; init; } = string.Empty;

    public bool IsSlipping { get; init; }
    public string WhyGlyph { get; init; } = string.Empty;
    public string WhyHeadline { get; init; } = string.Empty;
    public string WhyReason { get; init; } = string.Empty;
    public string WhyBreakdown { get; init; } = string.Empty;
    public bool HasWhyBreakdown => WhyBreakdown.Length > 0;
}

public sealed class CampaignResultGroup
{
    public PhaseRollupRow? Header { get; init; }
    public bool HasHeader => Header is not null;
    public List<CampaignDetailRow> Campaigns { get; init; } = new();
}
