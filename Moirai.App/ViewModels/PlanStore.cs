using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public partial class MonthRateOption : ObservableObject
{
    public string Month { get; }

    [ObservableProperty]
    private decimal _rate;

    public MonthRateOption(string month, decimal rate)
    {
        Month = month;
        _rate = rate;
    }
}

public sealed class PlanSettings
{
    public DateTime? HorizonStart { get; set; }
    public DateTime? HorizonEnd { get; set; }

    public DateTime? MasterDeadline { get; set; }

    public int FlyableModelIndex { get; set; }
    public decimal FlyableRate { get; set; } = 0.7m;
    public decimal[] MonthlyRates { get; set; } = Array.Empty<decimal>();
    public decimal Persistence { get; set; } = 0.5m;
    public decimal FlyablePatternLength { get; set; } = 7;
    public DateTime? FlyableAnchor { get; set; }

    public bool[] WeekendDays { get; set; } = Array.Empty<bool>();
    public string Holidays { get; set; } = string.Empty;

    public decimal BlocksPerDay { get; set; } = 3;
    public decimal LoadBalanceWindowDays { get; set; } = 7;
    public decimal MaxSortiesPerDay { get; set; } = 6;
    public decimal MaxRehearsalsPerDay { get; set; } = 1;
    public decimal PilotsPerSortie { get; set; } = 1;
    public decimal EngineersPerSortie { get; set; } = 2;
    public decimal MaxRehearsalGapDays { get; set; } = 2;
    public bool RehearsalOnWorkdaysOnly { get; set; } = true;
    public decimal MaxConsecutiveFlyingDays { get; set; } = 6;
    public decimal MinRestHours { get; set; } = 12;
    public decimal MaxFlightHoursPerDay { get; set; } = 6;
    public decimal MaxFlightHoursPer7Days { get; set; } = 30;
    public decimal MaxFlightHoursPer28Days { get; set; } = 100;
    public decimal MaxFlightHoursPerYear { get; set; } = 900;
    public decimal MaxDutyHoursPerDay { get; set; } = 12;
    public decimal MaxDutyHoursPer7Days { get; set; } = 60;
    public decimal MaxDutyHoursPer14Days { get; set; } = 110;
    public decimal MaxDutyHoursPer28Days { get; set; } = 190;
    public decimal MaxFlightDutyHoursPerDay { get; set; }

    public bool SimulateYieldInNominal { get; set; }

    public bool OverrideSortieAbort { get; set; }
    public decimal SortieAbortOverride { get; set; } = 0.05m;

    public bool UseRevisingEngine { get; set; }

    public HashSet<string> SelectedCampaignIds { get; set; } = new();
}

public sealed partial class ScenarioItem : ObservableObject
{
    public Scenario Model { get; }

    public ScenarioItem(Scenario model, int depth = 0)
    {
        Model = model;
        Depth = depth;
        _isSelected = model.IsSelected;
        _status = model.Status;
    }

    public int Depth { get; }

    public bool IsChild => Depth > 0;
    public Avalonia.Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string Name => Model.Name;
    public string CreatedAt => Model.CreatedAt;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _status;

    public bool HasRun => Model.RunSchedule is not null;

    partial void OnIsSelectedChanged(bool value) => Model.IsSelected = value;

    public void RefreshStatus() => Status = Model.Status;

    [ObservableProperty] private bool _isEditing;

    [ObservableProperty] private string _editName = string.Empty;

    public void BeginEdit()
    {
        EditName = Model.Name;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    public void ApplyRename(string newName)
    {
        Model.Name = newName;
        IsEditing = false;
        OnPropertyChanged(nameof(Name));
    }
}

public sealed class FamilyResultRow
{
    public FamilyResultRow(Scenario sc, int depth, bool isActive)
    {
        Name = sc.Name;
        Relation = depth == 0 ? "original" : "optimized variant";
        IsActive = isActive;
        Indent = new Avalonia.Thickness(depth * 18, 0, 0, 0);
        Status = sc.Status;
        HasRun = sc.RunSchedule is not null;
        Campaigns = (sc.CampaignResults ?? new List<CampaignOutcome>())
            .Select(r => new FamilyCampaignCell
            {
                Text = r.Status == "Incomplete" ? $"{r.CampaignName}: Incomplete" : $"{r.CampaignName}: {r.Status} {r.EndText}",
                Brush = r.Status switch
                {
                    "Met" or "Done" => MetBrush,
                    "Late" => LateBrush,
                    "Incomplete" => IncompleteBrush,
                    _ => MutedBrush,
                },
            }).ToList();
    }

    public string Name { get; }
    public string Relation { get; }
    public bool IsActive { get; }
    public Avalonia.Thickness Indent { get; }
    public string Status { get; }
    public bool HasRun { get; }
    public List<FamilyCampaignCell> Campaigns { get; }

    private static readonly Avalonia.Media.IBrush MetBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3FB950"));
    private static readonly Avalonia.Media.IBrush LateBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFBF1F"));
    private static readonly Avalonia.Media.IBrush IncompleteBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EF4444"));
    private static readonly Avalonia.Media.IBrush MutedBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9AA4B2"));
}

public sealed class FamilyCampaignCell
{
    public required string Text { get; init; }
    public required Avalonia.Media.IBrush Brush { get; init; }
}

public sealed partial class PlanComparisonViewModel : ObservableObject
{
    public required List<ComparisonHeader> Headers { get; init; }
    public required List<ComparisonRow> Rows { get; init; }
    public required List<PlanGanttItem> Gantts { get; init; }

    public required IReadOnlyList<SettingGroup> AllSettingGroups { get; init; }

    [ObservableProperty] private bool _showOnlyDiffs;

    public ObservableCollection<SettingGroup> SettingGroups { get; } = new();

    public bool HasSettings => AllSettingGroups.Count > 0;

    public string DiffSummary
    {
        get
        {
            int total = AllSettingGroups.Sum(g => g.Rows.Count);
            int diff = AllSettingGroups.Sum(g => g.Rows.Count(r => r.IsDifferent));
            return $"{diff} of {total} settings differ across the {Headers.Count} scenarios.";
        }
    }

    public PlanComparisonViewModel Initialize() { RebuildSettings(); return this; }

    partial void OnShowOnlyDiffsChanged(bool value) => RebuildSettings();

    private void RebuildSettings()
    {
        SettingGroups.Clear();
        foreach (var g in AllSettingGroups)
        {
            var rows = ShowOnlyDiffs ? g.Rows.Where(r => r.IsDifferent).ToList() : g.Rows;
            if (rows.Count > 0)
                SettingGroups.Add(new SettingGroup { Title = g.Title, Rows = rows });
        }
    }
}

public sealed class SettingGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<SettingRow> Rows { get; init; }
}

public sealed class SettingRow
{
    public required string Label { get; init; }
    public required List<string> Values { get; init; }
    public bool IsDifferent { get; init; }
}

public static class ScenarioComparison
{
    public static PlanComparisonViewModel? Build(IReadOnlyList<ScenarioItem> selected)
    {
        if (selected.Count < 2) return null;

        var headers = selected
            .Select(s => new ComparisonHeader { Name = s.Name, Summary = s.Status })
            .ToList();

        var order = new List<(string Id, string Name)>();
        foreach (var r in selected.SelectMany(s => s.Model.CampaignResults!))
            if (!order.Any(o => o.Id == r.CampaignId))
                order.Add((r.CampaignId, r.CampaignName));

        var rows = order.Select(c => new ComparisonRow
        {
            Campaign = c.Name,
            Cells = selected.Select(s =>
            {
                var r = s.Model.CampaignResults!.FirstOrDefault(x => x.CampaignId == c.Id);
                return r is null
                    ? new ComparisonCell { Text = "-" }
                    : new ComparisonCell
                    {
                        Text = r.Status == "Incomplete" ? "Incomplete" : $"{r.Status} · {r.EndText}",
                        IsMet = r.Status is "Met" or "Done",
                        IsLate = r.Status == "Late",
                        IsIncomplete = r.Status == "Incomplete",
                    };
            }).ToList(),
        }).ToList();

        var gantts = selected.Select(s => new PlanGanttItem
        {
            Name = s.Name,
            Summary = s.Status,
            Gantt = new GanttData { Scenario = s.Model.RunSnapshot!, Schedule = s.Model.RunSchedule! },
        }).ToList();

        var snapshots = selected.Select(s => s.Model.RunSnapshot!).ToList();

        return new PlanComparisonViewModel
        {
            Headers = headers,
            Rows = rows,
            Gantts = gantts,
            AllSettingGroups = ScenarioSettingsDiff.Build(snapshots),
        }.Initialize();
    }
}

public static class ScenarioSettingsDiff
{
    public static List<SettingGroup> Build(IReadOnlyList<Scenario> snaps)
    {
        var groups = new List<SettingGroup>();

        void Group(string title, params (string Label, Func<Scenario, string> Sel)[] rows)
        {
            var built = rows.Select(r =>
            {
                var values = snaps.Select(r.Sel).ToList();
                return new SettingRow
                {
                    Label = r.Label,
                    Values = values,
                    IsDifferent = values.Distinct().Count() > 1,
                };
            }).ToList();
            groups.Add(new SettingGroup { Title = title, Rows = built });
        }

        Group("Horizon & program",
            ("Horizon start", s => D(s.HorizonStart)),
            ("Horizon end", s => D(s.HorizonEnd)),
            ("Master deadline", s => s.MasterDeadline is { } m ? D(m) : "-"));

        Group("Fleet (aircraft)",
            ("Aircraft count", s => s.Aircraft.Count.ToString()),
            ("Tails", s => Join(s.Aircraft.Select(a => a.Id))),
            ("Maintenance windows", s => s.Aircraft.Sum(a => a.MaintenanceWindows.Count).ToString()),
            ("Periodic-maintenance rules", s => s.Aircraft.Sum(a => a.PeriodicMaintenances.Count).ToString()));

        Group("Personnel",
            ("Pilots", s => s.Pilots.Count.ToString()),
            ("Pilot names", s => Join(s.Pilots.Select(p => p.Name))),
            ("Engineers", s => s.Engineers.Count.ToString()),
            ("Engineer names", s => Join(s.Engineers.Select(e => e.Name))),
            ("Leave windows (total)", s =>
                (s.Pilots.Cast<Resource>().Concat(s.Engineers)).Sum(r => r.LeaveWindows.Count).ToString()),
            ("Crew with type ratings", s =>
                (s.Pilots.Cast<Resource>().Concat(s.Engineers)).Count(r => r.TypeRatings.Count > 0).ToString()));

        Group("Weather (flyable model)",
            ("Model", WeatherType),
            ("Persistence φ", s => s.FlyableModel is ProbabilisticFlyableDays p ? F(p.Persistence) : "-"),
            ("Flyable rate", WeatherRate),
            ("Monthly rates (min…max)", s => s.FlyableModel is ProbabilisticFlyableDays { MonthlyRates.Length: 12 } p
                ? $"{F(p.MonthlyRates.Min())}…{F(p.MonthlyRates.Max())}" : "-"),
            ("Pattern length", s => s.FlyableModel switch
            {
                RateBasedFlyableDays r => r.PatternLength.ToString(),
                ProbabilisticFlyableDays p => p.PatternLength.ToString(),
                _ => "-",
            }),
            ("Anchor", s => s.FlyableModel switch
            {
                RateBasedFlyableDays r => D(r.Anchor),
                ProbabilisticFlyableDays p => D(p.Anchor),
                _ => "-",
            }));

        Group("Calendar",
            ("Weekend days", s => Join(s.Calendar.WeekendDays.OrderBy(d => (int)d).Select(d => d.ToString()[..3]))),
            ("Holidays", s => s.Calendar.Holidays.Count.ToString()),
            ("Extra workdays (authorized)", s => s.Calendar.ExtraWorkdays.Count.ToString()));

        Group("Locations",
            ("Remote locations", s => s.Locations.Count == 0 ? "-" : Join(s.Locations.Select(l => l.Name))),
            ("Campaigns deployed", s => s.Campaigns.Count(c => c.LocationId.Length > 0).ToString()));

        Group("Simulators",
            ("Main-base simulators", s => s.Simulators.Count == 0 ? "unlimited" : s.Simulators.Count.ToString()),
            ("Sim maintenance windows", s => s.Simulators.Sum(x => x.MaintenanceWindows.Count).ToString()));

        Group("FTOM limits",
            ("Blocks / day", s => s.Config.BlocksPerDay.ToString()),
            ("Pilots / sortie", s => s.Config.PilotsPerSortie.ToString()),
            ("Engineers / sortie", s => s.Config.EngineersPerSortie.ToString()),
            ("Max sorties / person / day", s => s.Config.MaxSortiesPerPersonPerDay.ToString()),
            ("Max rehearsals / person / day", s => s.Config.MaxRehearsalsPerPersonPerDay.ToString()),
            ("Max rehearsal gap (working days)", s => s.Config.MaxRehearsalGapDays.ToString()),
            ("Rehearsals on workdays only", s => B(s.Config.RehearsalOnWorkdaysOnly)),
            ("Load-balance window (days)", s => s.Config.LoadBalanceWindowDays.ToString()),
            ("Max consecutive flying days", s => s.Config.MaxConsecutiveFlyingDays.ToString()),
            ("Min rest (h)", s => F(s.Config.MinRestHours)),
            ("Flight cap · day / 7 / 28 / year", s =>
                $"{F(s.Config.MaxFlightHoursPerDay)} / {F(s.Config.MaxFlightHoursPer7Days)} / " +
                $"{F(s.Config.MaxFlightHoursPer28Days)} / {F(s.Config.MaxFlightHoursPerYear)}"),
            ("Duty cap · day / 7 / 14 / 28", s =>
                $"{F(s.Config.MaxDutyHoursPerDay)} / {F(s.Config.MaxDutyHoursPer7Days)} / " +
                $"{F(s.Config.MaxDutyHoursPer14Days)} / {F(s.Config.MaxDutyHoursPer28Days)}"));

        Group("Test efficiency",
            ("Campaigns with efficiency on", s => $"{s.Campaigns.Count(c => c.TestEfficiencyEnabled)}/{s.Campaigns.Count}"),
            ("Simulate yield in nominal", s => B(s.Config.SimulateYieldInNominal)),
            ("Scenario abort override", s => s.Config.OverrideSortieAbort ? F(s.Config.SortieAbortOverride) : "off"));

        var union = new List<(string Id, string Name)>();
        foreach (var c in snaps.SelectMany(s => s.Campaigns))
            if (!union.Any(u => u.Id == c.Id)) union.Add((c.Id, c.Name));

        var campaignRows = new List<SettingRow> { CampaignRow("Campaigns in scope", snaps, s => s.Campaigns.Count.ToString()) };
        foreach (var (id, name) in union)
        {
            var values = snaps.Select(s =>
            {
                var c = s.Campaigns.FirstOrDefault(x => x.Id == id);
                return c is null
                    ? "-"
                    : $"{F(c.TotalTestPoints)} TP ÷ {F(c.TestPointsPerSortie)} = {c.RequiredSorties()} sorties · " +
                      $"eff {(c.TestEfficiencyEnabled ? "on" : "off")}";
            }).ToList();
            campaignRows.Add(new SettingRow { Label = name, Values = values, IsDifferent = values.Distinct().Count() > 1 });
        }
        groups.Add(new SettingGroup { Title = "Campaigns", Rows = campaignRows });

        return groups;
    }

    private static SettingRow CampaignRow(string label, IReadOnlyList<Scenario> snaps, Func<Scenario, string> sel)
    {
        var values = snaps.Select(sel).ToList();
        return new SettingRow { Label = label, Values = values, IsDifferent = values.Distinct().Count() > 1 };
    }

    private static string WeatherType(Scenario s) => s.FlyableModel switch
    {
        RateBasedFlyableDays => "Rate pattern",
        ProbabilisticFlyableDays => "Probabilistic (seasonal)",
        ExplicitFlyableDays => "Explicit days",
        _ => s.FlyableModel.GetType().Name,
    };

    private static string WeatherRate(Scenario s) => s.FlyableModel switch
    {
        RateBasedFlyableDays r => F(r.Rate),
        ProbabilisticFlyableDays { MonthlyRates.Length: 12 } p => $"mean {F(p.MonthlyRates.Average())}",
        ExplicitFlyableDays e => $"{e.Days.Count} days",
        _ => "-",
    };

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "-" : string.Join(", ", list);
    }

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    private static string F(double d) => double.IsInfinity(d) ? "∞" : d.ToString("0.###");
    private static string B(bool b) => b ? "yes" : "no";
}

public sealed class PlanGanttItem
{
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required GanttData Gantt { get; init; }
}

public sealed class ComparisonHeader
{
    public required string Name { get; init; }
    public required string Summary { get; init; }
}

public sealed class ComparisonRow
{
    public required string Campaign { get; init; }
    public required List<ComparisonCell> Cells { get; init; }
}

public sealed class ComparisonCell
{
    public required string Text { get; init; }
    public bool IsMet { get; init; }
    public bool IsLate { get; init; }
    public bool IsIncomplete { get; init; }
}
