using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed class DecisionRow
{
    public required string DayText { get; init; }
    public required string Kind { get; init; }
    public required IBrush KindBrush { get; init; }
    public required string Campaign { get; init; }
    public required string Message { get; init; }
}

public partial class DecisionLogViewModel : ViewModelBase
{
    private readonly List<DecisionEntry> _all;
    private readonly DateOnly _horizonStart;

    public DecisionLogViewModel(Scenario snapshot, Schedule captured)
    {
        _all = captured.DecisionLog;
        _horizonStart = snapshot.HorizonStart;
        ScenarioName = snapshot.Name;

        Campaigns.Add(AllCampaigns);
        foreach (var name in _all.Where(e => e.CampaignName is not null)
                     .Select(e => e.CampaignName!).Distinct())
            Campaigns.Add(name);
        SelectedCampaign = AllCampaigns;

        ApplyFilter();
    }

    private const string AllCampaigns = "All campaigns";

    public string ScenarioName { get; }

    public ObservableCollection<string> Campaigns { get; } = new();

    public ObservableCollection<DecisionRow> Entries { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    [ObservableProperty] private string _selectedCampaign = AllCampaigns;
    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _showPlaced = true;
    [ObservableProperty] private bool _showBlocked = true;
    [ObservableProperty] private bool _showWaiting = true;
    [ObservableProperty] private bool _showRepair = true;
    [ObservableProperty] private bool _showSearch = true;
    [ObservableProperty] private bool _showOther = true;

    partial void OnSelectedCampaignChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowPlacedChanged(bool value) => ApplyFilter();
    partial void OnShowBlockedChanged(bool value) => ApplyFilter();
    partial void OnShowWaitingChanged(bool value) => ApplyFilter();
    partial void OnShowRepairChanged(bool value) => ApplyFilter();
    partial void OnShowSearchChanged(bool value) => ApplyFilter();
    partial void OnShowOtherChanged(bool value) => ApplyFilter();

    private bool KindEnabled(DecisionKind k) => k switch
    {
        DecisionKind.Placed => ShowPlaced,
        DecisionKind.Blocked => ShowBlocked,
        DecisionKind.Waiting => ShowWaiting,
        DecisionKind.Repair => ShowRepair,
        DecisionKind.Search => ShowSearch,
        _ => ShowOther,
    };

    private void ApplyFilter()
    {
        Entries.Clear();
        bool allCamps = SelectedCampaign == AllCampaigns;
        string needle = SearchText?.Trim() ?? string.Empty;

        foreach (var e in _all)
        {
            if (!KindEnabled(e.Kind)) continue;
            if (!allCamps && e.CampaignName != SelectedCampaign) continue;
            if (needle.Length > 0 && !e.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            Entries.Add(ToRow(e));
        }

        int placed = _all.Count(e => e.Kind == DecisionKind.Placed);
        int blocked = _all.Count(e => e.Kind == DecisionKind.Blocked);
        int waiting = _all.Count(e => e.Kind == DecisionKind.Waiting);
        int repair = _all.Count(e => e.Kind == DecisionKind.Repair);
        int search = _all.Count(e => e.Kind == DecisionKind.Search);
        Summary = $"{Entries.Count} shown of {_all.Count} decisions · " +
                  $"{placed} placed · {blocked} blocked · {waiting} waiting" +
                  (repair > 0 ? $" · {repair} repair" : "") +
                  (search > 0 ? $" · {search} search" : "");
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SelectedCampaign = AllCampaigns;
        SearchText = string.Empty;
        ShowPlaced = ShowBlocked = ShowWaiting = ShowRepair = ShowSearch = ShowOther = true;
    }

    private DecisionRow ToRow(DecisionEntry e) => new()
    {
        DayText = e.Date is { } d ? d.ToString("ddd dd MMM") : "-",
        Kind = KindLabel(e.Kind),
        KindBrush = BrushFor(e.Kind),
        Campaign = e.CampaignName ?? string.Empty,
        Message = e.Message,
    };

    private static string KindLabel(DecisionKind k) => k switch
    {
        DecisionKind.Placed => "PLACED",
        DecisionKind.Blocked => "BLOCKED",
        DecisionKind.Waiting => "WAITING",
        DecisionKind.Rollback => "ROLLBACK",
        DecisionKind.Repair => "REPAIR",
        DecisionKind.Deployment => "DEPLOY",
        DecisionKind.Search => "SEARCH",
        DecisionKind.Outcome => "OUTCOME",
        _ => k.ToString().ToUpperInvariant(),
    };

    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static readonly IBrush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
    private static readonly IBrush Blue = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));
    private static readonly IBrush Violet = new SolidColorBrush(Color.FromRgb(0xAB, 0x47, 0xBC));
    private static readonly IBrush Purple = new SolidColorBrush(Color.FromRgb(0x7E, 0x57, 0xC2));
    private static readonly IBrush Gray = new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE));

    private static IBrush BrushFor(DecisionKind k) => k switch
    {
        DecisionKind.Placed => Green,
        DecisionKind.Blocked => Red,
        DecisionKind.Waiting => Amber,
        DecisionKind.Rollback => Orange,
        DecisionKind.Repair => Blue,
        DecisionKind.Deployment => Violet,
        DecisionKind.Search => Purple,
        _ => Gray,
    };
}
