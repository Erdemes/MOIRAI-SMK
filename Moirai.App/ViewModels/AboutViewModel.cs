using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase
{
    private const string RepoUrl = "https://github.com/Erdemes/MOIRAI";

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#EF4444"));

    public string Version { get; } = ReadVersion();

    public string Copyright => "© 2026 MOIRAI · Turkish Aerospace";

    [ObservableProperty] private string _updateStatus = "Press “Check for updates” to query the latest release.";
    [ObservableProperty] private IBrush _updateStatusBrush = Muted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateUrl))]
    private string? _updateUrl;

    public bool HasUpdateUrl => !string.IsNullOrWhiteSpace(UpdateUrl);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotChecking))]
    private bool _isChecking;

    public bool NotChecking => !IsChecking;

    public ObservableCollection<ChangelogEntry> Changelog { get; } = BuildChangelog();

    [RelayCommand]
    private void OpenProjectPage() => OpenUrl(RepoUrl);

    [RelayCommand]
    private void OpenDownloadPage()
    {
        if (UpdateUrl is { } u) OpenUrl(u);
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsChecking) return;
        IsChecking = true;
        UpdateStatus = "Checking for updates…";
        UpdateStatusBrush = Muted;
        UpdateUrl = null;
        try
        {
            var r = await new UpdateChecker().CheckAsync();
            switch (r.Status)
            {
                case UpdateChecker.Status.UpdateAvailable:
                    UpdateStatus = $"Update available: v{r.LatestVersion} (you have v{Version})."
                        + (string.IsNullOrWhiteSpace(r.Notes) ? "" : $" {r.Notes}");
                    UpdateUrl = r.Url;
                    UpdateStatusBrush = Warn;
                    break;
                case UpdateChecker.Status.UpToDate:
                    UpdateStatus = $"You’re up to date (v{Version}).";
                    UpdateStatusBrush = Good;
                    break;
                case UpdateChecker.Status.NotConfigured:
                    UpdateStatus = $"Update check is not configured. You’re on v{Version}.";
                    UpdateStatusBrush = Muted;
                    break;
                default:
                    UpdateStatus = $"Could not check for updates: {r.Message}";
                    UpdateStatusBrush = Bad;
                    break;
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static string ReadVersion()
    {
        var v = typeof(AboutViewModel).Assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static ObservableCollection<ChangelogEntry> BuildChangelog() => new()
    {
        new("1.3.6", "2026-10", new[]
        {
            "IMPROVED: Flight crew load balancing",
            "IMPROVED: Aircraft pool selection based on the type rated crew availability",
            "IMPROVED: Rehearsal placements",
            "FIXED: Monte Carlo analysis now uses the same engine as the plan",
            "FIXED: Campaign post completion rest days counted as flyable day in some cases",
        }),
        new("1.3.5", "2026-10", new[]
        {
            "ADDED: Per campaign working calendar",
            "ADDED: Per campaign flyable days model",
            "ADDED: Per campaign operational limits",
            "ADDED: Date pins - sorties now can be pinned to specific days",
        }),
        new("1.3.4", "2026-10", new[]
        {
            "ADDED: Sortie pins - right click on Gantt chart to pin aircraft+crew for a sortie then re-run the plan",
            "ADDED: Idle reasons - hover over an empty gantt cell to see why a pilot, FTE or aircraft was not used that day",
            "ADDED: Mark Idle Days - Colored markers on the Gantt chart for unused days",
        }),
        new("1.3.3", "2026-10", new[]
        {
            "IMPROVED: UI",
            "FIXED: Various bugs in crewLedger module",
        }),
        new("1.3.2", "2026-08", new[]
        {
            "ADDED: External Support - Target, tanker, chase etc. that is different from your project aircraft",
            "ADDED: External Support risk - External support availability probability",
            "ADDED: Data review day after campaign completion setting - Blocks the prerequisite campaign for the selected duration",
            "ADDED: Crew rest after deployment - The deployed crew will rest for the specified days.",
            "ADDED: Rest after campaign completion - The assigned crew and aircraft will rest for the specified days after campaign completion",
            "ADDED: P10/P50/P90 schedule sheets in the analysis Excel export",
            "IMPROVED: Pre-run checks panel visibility",
            "FIXED: Various fixes",
        }),
        new("1.3.1", "2026-08", new[]
        {
            "ADDED: Crew rest stagger · rotates the forced rest days so the program keeps flying every day.",
        }),
        new("1.3.0", "2026-06", new[]
        {
            "ADDED: Simulator-only campaigns · set a campaign's venue to Simulator and it runs as sim-device sessions (development / lab testing): duty-only, weather-independent, no aircraft or rehearsal, with the same test-point / yield / prerequisite machinery. Planned by both engines, validated, and shown in the results, Excel/PDF exports and a new Gantt SIMULATORS panel.",
            "ADDED: Record actuals for simulator sessions · click a sim-session box to mark it flown or aborted; both engines replay the recorded sessions and replan the rest, and Monte Carlo recalibrates.",
            "CHANGED: A simulator is now required everywhere · the old \"a base with no devices allows unlimited rehearsals\" fallback is removed, so a rehearsal-required campaign with no compatible device at its base cannot fly. Whether a base has a simulator is derived from the simulators assigned to it.",
            "CHANGED: The probabilistic weather defaults now use the Akinci / LTAE airfield climatology.",
        }),
        new("1.2.5", "2026-06", new[]
        {
            "ADDED: The weather pattern length and anchor are derived automatically from the flyable rate · no manual inputs. The length realises the rate with the shortest non-flyable run and avoids weekly resonance (the pitfalls that made deterministic runs over-optimistic).",
            "CHANGED: The default planning engine is now \"Smart\" · the advanced per-day steering + modify-behind repair heuristics (no whole-schedule search), floored against the basic engine so it is never worse, at roughly the same speed but usually a better plan. \"Use advanced engine\" now ADDS the thorough whole-schedule LNS search on top.",
            "CHANGED: Monte Carlo now evaluates every realization on the Smart engine, so the risk profile matches the plan.",
            "FIXED: edge cases in the advanced engine's cross-day repair (daily-sortie-cap overshoot) and the criticality float calculation · hardened by a much broader randomized test sweep across both engines and all analyses.",
        }),
        new("1.2.4", "2026-06", new[]
        {
            "ADDED: \"Criticality & float\" on the Analysis page · each campaign's free float (days it can slip before the program finish moves), the critical chain, and the load-bearing resources (whose removal delays the program or breaks a campaign). Runs on the plan's own engine.",
            "CHANGED: The Analysis page sections are now collapsible.",
        }),
        new("1.2.3", "2026-06", new[]
        {
            "ADDED: \"View DayLog\" on the results · a day-by-day decision log of why the engine placed each sortie, why a campaign could not fly, and what it waited on.",
            "ADDED: The decision log also shows the advanced engine's modify-behind repairs and whole-schedule search steps; filter it by campaign, kind or text.",
        }),
        new("1.2.2", "2026-06", new[]
        {
            "FIXED: A deployed campaign whose phase waits on another phase (an A-Level prerequisite) and shares crew/aircraft with it no longer deadlocks the plan · the deployment departs after the prerequisite phase completes.",
            "ADDED: A comprehensive one-year sample program demonstrating the A-Level/B-Level hierarchy, deployments, multi-ship, maintenance, failures, test efficiency and Monte-Carlo risk.",
        }),
        new("1.2.1", "2026-06", new[]
        {
            "ADDED: \"Order-spread\" sensitivity · the tornado and the sweeps now show a band across the P10 / P50 / P90 plan orders, so you can see how much each parameter's effect depends on the schedule the advanced engine commits to.",
            "ADDED: The campaign calendar now respects A-Level campaign prerequisites · a B-Level campaign's band lines up after every campaign of the A-Level campaign it waits on · and tints each A-Level campaign's bands a distinct colour.",
            "PERF: Sensitivity analysis and risk decomposition are much faster on the advanced engine · the plan order is computed once and reused, and the Sobol importance design runs in parallel.",
        }),
        new("1.2.0", "2026-06", new[]
        {
            "ADDED: Two-level campaign hierarchy · group B-Level campaigns under A-Level campaigns in a drag-and-drop tree on the Campaigns page (\"New ▾\" adds either level; drag a B-Level campaign onto an A-Level campaign to file it there).",
            "ADDED: An A-Level campaign shares its target window with its B-Level campaigns (one with no own target inherits it) and its prerequisites · its B-Level campaigns wait until the A-Level campaigns it depends on finish.",
            "ADDED: A-Level rollups everywhere · collapsible A-Level groups in the results with a per-A-Level Met/Late/Incomplete status and an A-Level \"why it slipped\", plus A-Level rollup tables in the Excel and PDF exports.",
            "ADDED: Tri-state A-Level checkboxes for \"campaigns to plan\" and the Gantt/schedule filter; the Gantt legend is grouped by A-Level campaign.",
            "ADDED: Plan results show which engine produced them (basic or advanced).",
            "CHANGED: Monte Carlo, the resource recommender and scenario comparison judge a B-Level campaign against its effective deadline (its own, or its inherited A-Level campaign's).",
        }),
        new("1.1.0", "2026-06", new[]
        {
            "ADDED: \"Use advanced engine (experimental)\" · a smarter, slower scheduler that searches the whole plan (look-forward / modify-behind + Large Neighborhood Search) and is never worse than the default. Off by default.",
            "ADDED: Run shows a cancellable progress dialog.",
        }),
        new("1.0.5", "2026-06", new[]
        {
            "ADDED: Global importance in sensitivity analysis · how much of the uncertainty each parameter drives, interactions included (Sobol).",
            "FIXED: Test-points burn-down now shows in the Monte Carlo realization window.",
            "FIXED: Deployment rehearsals stay packed close to departure when campaigns share crew (no more stranding weeks early).",
        }),
        new("1.0.4", "2026-06", new[]
        {
            "ADDED: Middle-mouse autoscroll on charts (Gantt, burn-down, timeline).",
            "ADDED: Monte Carlo results saved per scenario.",
            "ADDED: Double-click a scenario to rename it.",
            "FIXED: MC realization burn-down Y axis reaches 0.",
        }),
        new("1.0.3", "2026-06", new[]
        {
            "ADDED: Per-parameter sensitivity sweeps (not just φ).",
            "ADDED: Animation run picker (deterministic / P10 / P50 / P90).",
            "ADDED: Startup update-check progress dialog.",
            "CHANGED: Full-width Analysis page; default sensitivity runs 2000.",
        }),
        new("1.0.2", "2026-06", new[]
        {
            "ADDED: Animated return ferry; aircraft icons rotate to heading.",
            "ADDED: Stand-down day reason (holiday / weekend / not flyable).",
            "CHANGED: Ferry flies side-by-side; sortie climbs to a test area and loiters.",
            "FIXED: Burn-down fills its window height.",
        }),
        new("1.0.1", "2026-06", new[]
        {
            "ADDED: Pick a location's coordinates on a Turkey map.",
            "ADDED: Animation timeline under the map.",
            "ADDED: Chase/support ships fly; parked / maintenance tails shown.",
            "CHANGED: GitLab update fallback reads the latest release directly.",
        }),
        new("1.0.0", "2026-06", new[]
        {
            "ADDED: First C# / Avalonia release.",
            "ADDED: Interactive calendars, earned-value burn-down, horizon quick-set.",
            "ADDED: Execution tracking (actual aircraft / pilots / FTEs).",
            "ADDED: Monte Carlo risk, decomposition, sensitivity, resource recommender.",
            "ADDED: Portable build + update notifier.",
        }),
        new("0.4.0", "2026-05", new[]
        {
            "CHANGED: Plan page overhaul (per-plan runs, comparison, filters, SplitView).",
            "CHANGED: Resource pages reworked (dialog create, read-only assignments).",
            "ADDED: Table-based maintenance / leave planning.",
        }),
        new("0.3.0", "2026-04", new[]
        {
            "CHANGED: App layer refactored to MVVM/OOP; Gantt + scheduler fixes.",
        }),
        new("0.2.0", "2026-03", new[]
        {
            "CHANGED: Engine, tests, UI, reporting ported to the v2 spec (intra-day blocks).",
        }),
        new("0.1.0", "2026-02", new[]
        {
            "ADDED: Initial flight-test scheduling and what-if tool.",
        }),
    };
}

public sealed class ChangelogEntry
{
    public ChangelogEntry(string version, string date, IReadOnlyList<string> changes)
    {
        Version = version;
        Date = date;
        Changes = changes;
    }

    public string Version { get; }
    public string Date { get; }
    public string Header => $"v{Version} · {Date}";
    public IReadOnlyList<string> Changes { get; }
}
