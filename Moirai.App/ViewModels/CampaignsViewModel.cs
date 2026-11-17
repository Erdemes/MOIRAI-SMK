using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;
using MOIRAI.Services;

namespace MOIRAI.ViewModels;

public partial class CampaignsViewModel : ViewModelBase
{
    private const decimal DefaultRetestStart = 0.25m;
    private const decimal DefaultRetestFloor = 0.05m;
    private const decimal DefaultLearningScale = 20m;
    private const decimal DefaultAbortProbability = 0.05m;

    private readonly IRepository _repo;
    private readonly IDialogService _dialogs;

    public ObservableCollection<Campaign> Items { get; } = new();

    public ObservableCollection<SelectableOption> AircraftOptions { get; } = new();
    public ObservableCollection<SelectableOption> PilotOptions { get; } = new();
    public ObservableCollection<SelectableOption> EngineerOptions { get; } = new();
    public ObservableCollection<SelectableOption> PrerequisiteOptions { get; } = new();

    public ObservableCollection<SelectableOption> SimulatorOptions { get; } = new();
    public bool HasSimulatorOptions => SimulatorOptions.Count > 0;

    public ObservableCollection<SelectableOption> ExternalSupportOptions { get; } = new();
    public bool HasExternalSupportOptions => ExternalSupportOptions.Count > 0;

    public IReadOnlyList<string> VenueOptions { get; } = new[] { "Aircraft", "Simulator" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimulatorVenue))]
    [NotifyPropertyChangedFor(nameof(IsAircraftVenue))]
    [NotifyPropertyChangedFor(nameof(ShowSimList))]
    [NotifyPropertyChangedFor(nameof(ShowExternalList))]
    private int _editVenueIndex;

    public bool IsSimulatorVenue => EditVenueIndex == 1;
    public bool IsAircraftVenue => EditVenueIndex == 0;

    public bool ShowSimList => IsSimulatorVenue && HasSimulatorOptions;

    public bool ShowExternalList => IsAircraftVenue && HasExternalSupportOptions;

    public ObservableCollection<SelectableOption> MaintenancePrereqOptions { get; } = new();
    public bool HasMaintenancePrereqOptions => MaintenancePrereqOptions.Count > 0;

    public ObservableCollection<SelectableOption> SupportAircraftOptions { get; } = new();
    [ObservableProperty] private decimal _editSupportPerSortie;

    public ObservableCollection<string> LocationOptions { get; } = new();
    private List<string> _locationIds = new();
    [ObservableProperty] private int _editLocationIndex;
    public bool HasLocationOptions => LocationOptions.Count > 1;

    public ObservableCollection<string> GroupOptions { get; } = new();
    private List<string> _groupIds = new();
    [ObservableProperty] private int _editGroupIndex;
    public bool HasGroupOptions => GroupOptions.Count > 1;

    public ObservableCollection<CampaignTreeNode> Tree { get; } = new();

    [ObservableProperty]
    private CampaignTreeNode? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingCampaign))]
    [NotifyPropertyChangedFor(nameof(EditingGroup))]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    private int _editorMode;

    public bool EditingCampaign => EditorMode == 1;
    public bool EditingGroup => EditorMode == 2;
    public bool HasEditor => EditorMode != 0;

    private List<CampaignGroup> _groups = new();

    [ObservableProperty] private string _editGroupName = string.Empty;
    [ObservableProperty] private DateTime? _editGroupTargetStart;
    [ObservableProperty] private DateTime? _editGroupTargetEnd;

    public ObservableCollection<SelectableOption> GroupPrereqOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupFormTitle))]
    [NotifyPropertyChangedFor(nameof(CanDeleteGroup))]
    private string? _editingGroupId;

    public string GroupFormTitle => EditingGroupId is null ? "New A-Level Campaign" : "Edit A-Level Campaign";
    public bool CanDeleteGroup => EditingGroupId is not null;

    [ObservableProperty]
    private Campaign? _selectedItem;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editTotalTestPoints;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editTestPointsPerSortie = 1;

    [ObservableProperty]
    private DateTime? _editStartDate;

    [ObservableProperty]
    private DateTime? _editTargetEndDate;

    [ObservableProperty]
    private bool _editPreferSingleAircraft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private bool _editTestEfficiencyEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editRetestStart = DefaultRetestStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editRetestFloor = DefaultRetestFloor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editRetestLearningScale = DefaultLearningScale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    [NotifyPropertyChangedFor(nameof(ShowRetestExponent))]
    private int _editRetestModelIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editRetestExponent = 1;

    public IReadOnlyList<string> RetestModelOptions { get; } = new[] { "Exponential", "Power law" };

    public bool ShowRetestExponent => EditRetestModelIndex == (int)RetestModel.PowerLaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSortiesText))]
    private decimal _editAbortProbability = DefaultAbortProbability;

    [ObservableProperty]
    private decimal _editBlocksPerSortie = 1;

    [ObservableProperty]
    private string _editAllowedBlocks = string.Empty;

    [ObservableProperty]
    private decimal _editReviewWorkdays;

    [ObservableProperty]
    private decimal _editMaxSortiesPerDay;

    [ObservableProperty]
    private decimal _editPostReviewWorkdays;

    [ObservableProperty]
    private decimal _editRestDaysAfterCompletion;

    [ObservableProperty] private bool _editUseGlobalCalendar = true;
    [ObservableProperty] private bool _editWeekendMonday;
    [ObservableProperty] private bool _editWeekendTuesday;
    [ObservableProperty] private bool _editWeekendWednesday;
    [ObservableProperty] private bool _editWeekendThursday;
    [ObservableProperty] private bool _editWeekendFriday;
    [ObservableProperty] private bool _editWeekendSaturday = true;
    [ObservableProperty] private bool _editWeekendSunday = true;
    [ObservableProperty] private bool _editWorkOnHolidays;

    [ObservableProperty] private decimal _editPilotsPerSortie;
    [ObservableProperty] private decimal _editEngineersPerSortie;

    [ObservableProperty]
    private decimal _editSortieFlightHours = 1;

    [ObservableProperty]
    private decimal _editSortieDutyHours = 4;

    [ObservableProperty]
    private decimal _editSortieFlightDutyHours;

    [ObservableProperty]
    private decimal _editRehearsalDutyHours = 2;

    [ObservableProperty]
    private bool _editRehearsalRequired = true;

    [ObservableProperty]
    private WindowsCalendarData? _calendarData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private string? _editingId;

    public string FormTitle => EditingId is null ? "New Campaign" : "Edit Campaign";
    public bool CanDelete => EditingId is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string RequiredSortiesText
    {
        get
        {
            double tps = (double)EditTestPointsPerSortie;
            double ttp = (double)EditTotalTestPoints;
            int required = tps <= 0 ? 0 : (int)Math.Ceiling(ttp / tps);
            string text = $"Required sorties: {required}";

            double inflation = FormCampaign().ExpectedInflation();
            if (tps > 0 && inflation > 1.0001)
            {
                int expected = (int)Math.Ceiling(ttp * inflation / tps);
                text += $" · with test-efficiency losses: ~{expected} (×{inflation:0.##})";
            }
            return text;
        }
    }

    private Campaign FormCampaign() => new()
    {
        Name = string.IsNullOrWhiteSpace(EditName) ? "Campaign" : EditName.Trim(),
        TotalTestPoints = (double)EditTotalTestPoints,
        TestPointsPerSortie = (double)EditTestPointsPerSortie,
        TestEfficiencyEnabled = EditTestEfficiencyEnabled,
        RetestProbabilityStart = (double)EditRetestStart,
        RetestProbabilityFloor = (double)EditRetestFloor,
        RetestLearningScale = (double)EditRetestLearningScale,
        RetestCurve = (RetestModel)EditRetestModelIndex,
        RetestExponent = (double)EditRetestExponent,
        AbortProbability = (double)EditAbortProbability,
    };

    [RelayCommand]
    private Task PreviewEfficiency() =>
        _dialogs.ShowYieldPreviewDialogAsync(new YieldPreviewViewModel { Campaign = FormCampaign() });

    public CampaignsViewModel(IRepository repo, IDialogService dialogs)
    {
        _repo = repo;
        _dialogs = dialogs;
    }

    public override void OnNavigatedTo()
    {
        ReloadList();
        ReloadGroups();
        RebuildTree();

        var current = EditingId;
        if (current is not null && Items.Any(c => c.Id == current))
            SelectNode(current, group: false);
        else if (Items.Count > 0)
            SelectNode(Items[0].Id, group: false);
        else
            EditorMode = 0;
        RebuildCalendar();
    }

    private void ReloadList()
    {
        Items.Clear();
        foreach (var c in _repo.GetCampaigns()) Items.Add(c);
    }

    private void ReloadGroups() => _groups = _repo.GetCampaignGroups().ToList();

    private void RebuildTree()
    {
        Tree.Clear();
        var scenario = _repo.GetScenario();
        DateOnly CampKey(Campaign c) => CampaignDeadlines.EffectiveMilestone(scenario, c) ?? DateOnly.MaxValue;
        DateOnly GroupKey(CampaignGroup g) => g.TargetEndDate ?? DateOnly.MaxValue;

        var grouped = new HashSet<string>();
        var roots = new List<(DateOnly Key, CampaignTreeNode Node)>();

        foreach (var g in _groups)
        {
            var node = new CampaignTreeNode
            {
                Id = g.Id, IsGroup = true, Name = g.Name,
                Subtitle = g.TargetEndDate is { } te ? $"target {te:yyyy-MM-dd}" : "no target",
            };
            foreach (var c in Items.Where(c => c.GroupId == g.Id).OrderBy(CampKey))
            {
                node.Children.Add(MakeCampaignNode(c));
                grouped.Add(c.Id);
            }
            roots.Add((GroupKey(g), node));
        }
        foreach (var c in Items.Where(c => !grouped.Contains(c.Id)))
            roots.Add((CampKey(c), MakeCampaignNode(c)));

        foreach (var (_, node) in roots.OrderBy(r => r.Key))
            Tree.Add(node);
    }

    private static CampaignTreeNode MakeCampaignNode(Campaign c) => new()
    {
        Id = c.Id, IsGroup = false,
        Name = string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name,
        Subtitle = c.TargetEndDate == DateOnly.MaxValue ? "" : c.TargetEndDate.ToString("yyyy-MM-dd"),
    };

    private void RefreshAll()
    {
        ReloadList();
        ReloadGroups();
        RebuildTree();
    }

    private void SelectNode(string id, bool group)
    {
        foreach (var root in Tree)
        {
            if (root.Id == id && root.IsGroup == group) { SelectedNode = root; return; }
            foreach (var child in root.Children)
                if (child.Id == id && child.IsGroup == group) { SelectedNode = child; return; }
        }
    }

    partial void OnSelectedNodeChanged(CampaignTreeNode? value)
    {
        if (value is null) return;
        if (value.IsGroup)
        {
            BeginEditGroup(_groups.FirstOrDefault(g => g.Id == value.Id));
            return;
        }
        var camp = Items.FirstOrDefault(c => c.Id == value.Id);
        if (camp is not null && ReferenceEquals(SelectedItem, camp))
            EditorMode = 1;
        else
            SelectedItem = camp;
    }

    public string GroupIdOf(string campaignId) =>
        Items.FirstOrDefault(c => c.Id == campaignId)?.GroupId ?? string.Empty;

    public void Reparent(string campaignId, string? groupId)
    {
        var c = Items.FirstOrDefault(x => x.Id == campaignId);
        if (c is null) return;
        string target = groupId ?? string.Empty;
        if (target.Length > 0 && _groups.All(g => g.Id != target)) target = string.Empty;
        if (c.GroupId == target) return;
        c.GroupId = target;
        _repo.UpdateCampaign(c);
        RefreshAll();
        SelectNode(c.Id, group: false);
    }

    private void BeginEditGroup(CampaignGroup? g)
    {
        ErrorMessage = null;
        EditingGroupId = g?.Id;
        EditGroupName = g?.Name ?? string.Empty;
        EditGroupTargetStart = ToPickerDate(g?.TargetStartDate);
        EditGroupTargetEnd = ToPickerDate(g?.TargetEndDate);
        GroupPrereqOptions.Clear();
        foreach (var other in _groups.Where(o => o.Id != g?.Id))
            GroupPrereqOptions.Add(new SelectableOption(other.Id, other.Name,
                g?.PrerequisiteGroupIds.Contains(other.Id) == true));
        EditorMode = 2;
    }

    [RelayCommand]
    private void NewGroup()
    {
        SelectedNode = null;
        SelectedItem = null;
        BeginEditGroup(null);
    }

    [RelayCommand]
    private void SaveGroup()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(EditGroupName))
        {
            ErrorMessage = "Name is required.";
            return;
        }
        var start = FromPickerDate(EditGroupTargetStart);
        var end = FromPickerDate(EditGroupTargetEnd);
        if (start is { } s && end is { } e && e < s)
        {
            ErrorMessage = "The phase target window ends before it starts.";
            return;
        }

        bool isNew = EditingGroupId is null;
        var g = isNew
            ? new CampaignGroup()
            : _groups.FirstOrDefault(x => x.Id == EditingGroupId) ?? new CampaignGroup { Id = EditingGroupId! };
        g.Name = EditGroupName.Trim();
        g.TargetStartDate = start;
        g.TargetEndDate = end;
        g.PrerequisiteGroupIds = GroupPrereqOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (isNew) _repo.AddCampaignGroup(g); else _repo.UpdateCampaignGroup(g);

        EditingGroupId = g.Id;
        RefreshAll();
        SelectNode(g.Id, group: true);
    }

    [RelayCommand]
    private void DeleteGroup()
    {
        if (EditingGroupId is not { } id) return;
        try
        {
            _repo.DeleteCampaignGroup(id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        EditingGroupId = null;
        RefreshAll();
        EditorMode = 0;
    }

    private static DateTime? ToPickerDate(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? FromPickerDate(DateTime? d) => d.HasValue ? DateOnly.FromDateTime(d.Value) : null;

    private void FillOptions(Campaign? c)
    {
        Fill(AircraftOptions, _repo.GetAircraft().Select(a => (a.Id, $"{a.Name} ({a.Id})")), c?.CompatibleAircraftIds);
        Fill(SupportAircraftOptions, _repo.GetAircraft().Select(a => (a.Id, $"{a.Name} ({a.Id})")), c?.SupportAircraftIds);
        Fill(PilotOptions, _repo.GetPilots().Select(p => (p.Id, p.Name)), c?.AssignedPilotIds);
        Fill(EngineerOptions, _repo.GetEngineers().Select(e => (e.Id, e.Name)), c?.AssignedEngineerIds);
        Fill(PrerequisiteOptions, _repo.GetCampaigns().Where(x => x.Id != c?.Id).Select(x => (x.Id, x.Name)), c?.PrerequisiteCampaignIds);
        Fill(MaintenancePrereqOptions,
            _repo.GetAircraft().SelectMany(a => a.MaintenanceWindows.Select(w =>
                (w.Id, $"{a.Id} · {w.Start:yyyy-MM-dd} → {w.End:yyyy-MM-dd}" +
                       (string.IsNullOrWhiteSpace(w.Note) ? "" : $" ({w.Note})")))),
            c?.PrerequisiteMaintenanceIds);
        OnPropertyChanged(nameof(HasMaintenancePrereqOptions));

        var locName = _repo.GetLocations().ToDictionary(l => l.Id, l => l.Name);
        Fill(SimulatorOptions,
            _repo.GetSimulators().Select(s => (s.Id,
                string.IsNullOrEmpty(s.LocationId)
                    ? $"{s.Name} (main)"
                    : $"{s.Name} ({locName.GetValueOrDefault(s.LocationId, "remote")})")),
            c?.CompatibleSimulatorIds);
        OnPropertyChanged(nameof(HasSimulatorOptions));
        OnPropertyChanged(nameof(ShowSimList));

        Fill(ExternalSupportOptions,
            _repo.GetExternalSupports().Select(a => (a.Id, a.Name)),
            c?.RequiredExternalSupportIds);
        OnPropertyChanged(nameof(HasExternalSupportOptions));
        OnPropertyChanged(nameof(ShowExternalList));

        LocationOptions.Clear();
        _locationIds = new List<string> { string.Empty };
        LocationOptions.Add("Main base");
        foreach (var l in _repo.GetLocations())
        {
            LocationOptions.Add(l.Name);
            _locationIds.Add(l.Id);
        }
        int li = c is null ? 0 : _locationIds.IndexOf(c.LocationId);
        EditLocationIndex = li < 0 ? 0 : li;
        OnPropertyChanged(nameof(HasLocationOptions));

        GroupOptions.Clear();
        _groupIds = new List<string> { string.Empty };
        GroupOptions.Add("None (ungrouped)");
        foreach (var g in _repo.GetCampaignGroups())
        {
            GroupOptions.Add(g.Name);
            _groupIds.Add(g.Id);
        }
        int gi = c is null ? 0 : _groupIds.IndexOf(c.GroupId);
        EditGroupIndex = gi < 0 ? 0 : gi;
        OnPropertyChanged(nameof(HasGroupOptions));
    }

    private static void Fill(
        ObservableCollection<SelectableOption> target,
        IEnumerable<(string Id, string Label)> all,
        ICollection<string>? selectedIds)
    {
        target.Clear();
        foreach (var (id, label) in all)
            target.Add(new SelectableOption(id, label, selectedIds?.Contains(id) == true));
    }

    private void LoadWeekend(ICollection<DayOfWeek> days)
    {
        EditWeekendMonday = days.Contains(DayOfWeek.Monday);
        EditWeekendTuesday = days.Contains(DayOfWeek.Tuesday);
        EditWeekendWednesday = days.Contains(DayOfWeek.Wednesday);
        EditWeekendThursday = days.Contains(DayOfWeek.Thursday);
        EditWeekendFriday = days.Contains(DayOfWeek.Friday);
        EditWeekendSaturday = days.Contains(DayOfWeek.Saturday);
        EditWeekendSunday = days.Contains(DayOfWeek.Sunday);
    }

    private HashSet<DayOfWeek> EditedWeekend()
    {
        var set = new HashSet<DayOfWeek>();
        if (EditWeekendMonday) set.Add(DayOfWeek.Monday);
        if (EditWeekendTuesday) set.Add(DayOfWeek.Tuesday);
        if (EditWeekendWednesday) set.Add(DayOfWeek.Wednesday);
        if (EditWeekendThursday) set.Add(DayOfWeek.Thursday);
        if (EditWeekendFriday) set.Add(DayOfWeek.Friday);
        if (EditWeekendSaturday) set.Add(DayOfWeek.Saturday);
        if (EditWeekendSunday) set.Add(DayOfWeek.Sunday);
        return set;
    }

    partial void OnSelectedItemChanged(Campaign? value)
    {
        ErrorMessage = null;
        if (value is null) return;

        EditingId = value.Id;
        EditName = value.Name;
        EditVenueIndex = (int)value.Venue;
        EditTotalTestPoints = (decimal)value.TotalTestPoints;
        EditTestPointsPerSortie = (decimal)value.TestPointsPerSortie;
        EditStartDate = FromStart(value.StartDate);
        EditTargetEndDate = FromTarget(value.TargetEndDate);
        EditPreferSingleAircraft = value.PreferSingleAircraft;
        EditBlocksPerSortie = value.BlocksPerSortie;
        EditAllowedBlocks = string.Join(", ", value.AllowedBlocks);
        EditReviewWorkdays = value.MinWorkdaysBetweenSorties;
        EditMaxSortiesPerDay = value.MaxSortiesPerDay;
        EditPostReviewWorkdays = value.PostCompletionReviewWorkdays;
        EditRestDaysAfterCompletion = value.RestDaysAfterCompletion;
        LoadWeekend(value.UseGlobalCalendar ? _repo.GetScenario().Calendar.WeekendDays : value.WeekendDays);
        EditUseGlobalCalendar = value.UseGlobalCalendar;
        EditWorkOnHolidays = value.WorkOnHolidays;
        LoadOverrides(value);
        EditPilotsPerSortie = value.PilotsPerSortie;
        EditEngineersPerSortie = value.EngineersPerSortie;
        EditSortieFlightHours = (decimal)value.ExpectedSortieFlightHours;
        EditSortieDutyHours = (decimal)value.ExpectedSortieDutyHours;
        EditSortieFlightDutyHours = (decimal)value.ExpectedSortieFlightDutyHours;
        EditRehearsalDutyHours = (decimal)value.ExpectedRehearsalDutyHours;
        EditRehearsalRequired = value.RehearsalRequired;
        EditSupportPerSortie = value.SupportAircraftPerSortie;
        EditTestEfficiencyEnabled = value.TestEfficiencyEnabled;
        EditRetestStart = (decimal)value.RetestProbabilityStart;
        EditRetestFloor = (decimal)value.RetestProbabilityFloor;
        EditRetestLearningScale = (decimal)value.RetestLearningScale;
        EditRetestModelIndex = (int)value.RetestCurve;
        EditRetestExponent = (decimal)value.RetestExponent;
        EditAbortProbability = (decimal)value.AbortProbability;
        FillOptions(value);
        EditorMode = 1;
        RebuildCalendar();
    }

    [RelayCommand]
    private void NewCampaign()
    {
        string presetGroup = SelectedNode switch
        {
            { IsGroup: true } g => g.Id,
            { IsGroup: false } c => GroupIdOf(c.Id),
            _ => string.Empty,
        };
        SelectedNode = null;
        SelectedItem = null;
        EditingId = null;
        EditName = string.Empty;
        EditVenueIndex = 0;
        EditTotalTestPoints = 0;
        EditTestPointsPerSortie = 1;
        EditStartDate = null;
        EditTargetEndDate = null;
        EditPreferSingleAircraft = false;
        EditBlocksPerSortie = 1;
        EditAllowedBlocks = string.Empty;
        EditReviewWorkdays = 0;
        EditMaxSortiesPerDay = 0;
        EditPostReviewWorkdays = 0;
        EditRestDaysAfterCompletion = 0;
        LoadWeekend(_repo.GetScenario().Calendar.WeekendDays);
        EditUseGlobalCalendar = true;
        EditWorkOnHolidays = false;
        LoadOverrides(null);
        EditPilotsPerSortie = 0;
        EditEngineersPerSortie = 0;
        EditSortieFlightHours = 1;
        EditSortieDutyHours = 4;
        EditSortieFlightDutyHours = 0;
        EditRehearsalDutyHours = 2;
        EditRehearsalRequired = true;
        EditSupportPerSortie = 0;
        EditTestEfficiencyEnabled = false;
        EditRetestStart = DefaultRetestStart;
        EditRetestFloor = DefaultRetestFloor;
        EditRetestLearningScale = DefaultLearningScale;
        EditRetestModelIndex = 0;
        EditRetestExponent = 1;
        EditAbortProbability = DefaultAbortProbability;
        ErrorMessage = null;
        FillOptions(null);
        int pgi = _groupIds.IndexOf(presetGroup);
        EditGroupIndex = pgi < 0 ? 0 : pgi;
        EditorMode = 1;
        RebuildCalendar();
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Name is required.";
            return;
        }
        if (EditTestPointsPerSortie <= 0)
        {
            ErrorMessage = "Test points per sortie must be greater than 0.";
            return;
        }

        bool isNew = EditingId is null;
        var entity = new Campaign
        {
            Name = EditName.Trim(),
            TotalTestPoints = (double)EditTotalTestPoints,
            TestPointsPerSortie = (double)EditTestPointsPerSortie,
            StartDate = ToStart(EditStartDate),
            TargetEndDate = ToTarget(EditTargetEndDate),
            Venue = (CampaignVenue)EditVenueIndex,
            PreferSingleAircraft = !IsSimulatorVenue && EditPreferSingleAircraft,
            BlocksPerSortie = Math.Max(1, (int)EditBlocksPerSortie),
            AllowedBlocks = ParseAllowedBlocks(EditAllowedBlocks),
            MinWorkdaysBetweenSorties = Math.Max(0, (int)EditReviewWorkdays),
            MaxSortiesPerDay = Math.Max(0, (int)EditMaxSortiesPerDay),
            PostCompletionReviewWorkdays = Math.Max(0, (int)EditPostReviewWorkdays),
            RestDaysAfterCompletion = Math.Max(0, (int)EditRestDaysAfterCompletion),
            UseGlobalCalendar = EditUseGlobalCalendar,
            WeekendDays = EditedWeekend(),
            WorkOnHolidays = EditWorkOnHolidays,
            PilotsPerSortie = Math.Max(0, (int)EditPilotsPerSortie),
            EngineersPerSortie = Math.Max(0, (int)EditEngineersPerSortie),
            ExpectedSortieFlightHours = (double)EditSortieFlightHours,
            ExpectedSortieDutyHours = (double)EditSortieDutyHours,
            ExpectedSortieFlightDutyHours = (double)EditSortieFlightDutyHours,
            ExpectedRehearsalDutyHours = (double)EditRehearsalDutyHours,
            RehearsalRequired = !IsSimulatorVenue && EditRehearsalRequired,
            TestEfficiencyEnabled = EditTestEfficiencyEnabled,
            RetestProbabilityStart = (double)EditRetestStart,
            RetestProbabilityFloor = (double)EditRetestFloor,
            RetestLearningScale = (double)EditRetestLearningScale,
            RetestCurve = (RetestModel)EditRetestModelIndex,
            RetestExponent = (double)EditRetestExponent,
            AbortProbability = (double)EditAbortProbability,
            CompatibleAircraftIds = IsSimulatorVenue ? new() : SelectedIds(AircraftOptions),
            CompatibleSimulatorIds = SelectedIds(SimulatorOptions),
            RequiredExternalSupportIds = IsSimulatorVenue ? new() : SelectedIds(ExternalSupportOptions),
            SupportAircraftIds = IsSimulatorVenue ? new() : SelectedIds(SupportAircraftOptions),
            SupportAircraftPerSortie = IsSimulatorVenue ? 0 : Math.Max(0, (int)EditSupportPerSortie),
            AssignedPilotIds = SelectedIds(PilotOptions),
            AssignedEngineerIds = SelectedIds(EngineerOptions),
            PrerequisiteCampaignIds = SelectedIds(PrerequisiteOptions),
            PrerequisiteMaintenanceIds = SelectedIds(MaintenancePrereqOptions),
            LocationId = !IsSimulatorVenue && EditLocationIndex >= 0 && EditLocationIndex < _locationIds.Count
                ? _locationIds[EditLocationIndex]
                : string.Empty,
            GroupId = EditGroupIndex >= 0 && EditGroupIndex < _groupIds.Count
                ? _groupIds[EditGroupIndex]
                : string.Empty,
        };
        ApplyOverrides(entity);
        if (!isNew) entity.Id = EditingId!;

        if (isNew) _repo.AddCampaign(entity); else _repo.UpdateCampaign(entity);
        RefreshAll();
        SelectNode(entity.Id, group: false);
    }

    [RelayCommand]
    private void Delete()
    {
        if (EditingId is null) return;
        _repo.DeleteCampaign(EditingId);
        EditingId = null;
        SelectedItem = null;
        RefreshAll();
        EditorMode = 0;
    }

    private void RebuildCalendar()
    {
        var resolved = ResolveAll();
        var groupColors = GanttPalette.GroupColors(_repo.GetScenario());
        var rows = Items.Select(c =>
        {
            var (start, end, after) = resolved[c.Id];
            return new WindowsCalendarRow
            {
                Id = c.Id,
                Name = c.Venue == CampaignVenue.Simulator ? $"{c.Name} (SIM)" : c.Name,
                Accent = c.GroupId.Length > 0 && groupColors.TryGetValue(c.GroupId, out var gc)
                    ? gc : (Color?)null,
                Windows = new List<DateRange>
                {
                    new() { Id = c.Id, Start = start, End = end, Note = after is null ? "" : $"after {after}" },
                },
            };
        }).ToList();

        CalendarData = WindowsCalendarData.Build(
            WindowsCalendarKind.Campaign, _repo.GetScenario(), rows, EditingId);
    }

    private Dictionary<string, (DateOnly Start, DateOnly End, string? After)> ResolveAll()
    {
        var scenario = _repo.GetScenario();
        DateOnly defaultStart, defaultEnd;
        if (scenario.HorizonEnd > scenario.HorizonStart)
        {
            defaultStart = scenario.HorizonStart;
            defaultEnd = scenario.HorizonEnd;
        }
        else
        {
            defaultStart = DateOnly.FromDateTime(DateTime.Today);
            defaultEnd = defaultStart.AddDays(89);
        }
        if (scenario.MasterDeadline is { } master) defaultEnd = master;

        var byId = Items.ToDictionary(c => c.Id);
        var memo = new Dictionary<string, (DateOnly Start, DateOnly End, string? After)>();
        var visiting = new HashSet<string>();

        DateOnly OwnStart(Campaign c) => c.StartDate == DateOnly.MinValue ? defaultStart : c.StartDate;
        DateOnly OwnEnd(Campaign c, DateOnly start)
        {
            var end = c.TargetEndDate == DateOnly.MaxValue ? (defaultEnd > start ? defaultEnd : start) : c.TargetEndDate;
            return end < start ? start : end;
        }

        (DateOnly Start, DateOnly End, string? After) Resolve(Campaign c)
        {
            if (memo.TryGetValue(c.Id, out var cached)) return cached;
            if (!visiting.Add(c.Id)) return (OwnStart(c), OwnEnd(c, OwnStart(c)), null);

            var start = OwnStart(c);
            string? after = null;
            foreach (var pid in CampaignDeadlines.EffectivePrerequisiteCampaignIds(scenario, c))
            {
                if (!byId.TryGetValue(pid, out var prereq)) continue;
                var candidate = Resolve(prereq).End.AddDays(1);
                if (candidate > start) { start = candidate; after = prereq.Name; }
            }

            visiting.Remove(c.Id);
            var r = (start, OwnEnd(c, start), after);
            memo[c.Id] = r;
            return r;
        }

        foreach (var c in Items) Resolve(c);
        return memo;
    }

    [RelayCommand]
    private void ApplyWindowChange(WindowChange? change)
    {
        if (change is null) return;
        var camp = Items.FirstOrDefault(c => c.Id == change.RowId);
        if (camp is null) return;

        int delta = change.NewEnd.DayNumber - ResolveAll()[camp.Id].End.DayNumber;

        camp.StartDate = change.NewStart;
        camp.TargetEndDate = change.NewEnd;
        _repo.UpdateCampaign(camp);

        if (delta != 0)
            foreach (var depId in TransitiveDependents(camp.Id))
            {
                var d = Items.First(x => x.Id == depId);
                if (d.StartDate != DateOnly.MinValue) d.StartDate = d.StartDate.AddDays(delta);
                if (d.TargetEndDate != DateOnly.MaxValue) d.TargetEndDate = d.TargetEndDate.AddDays(delta);
                _repo.UpdateCampaign(d);
            }

        if (EditingId == camp.Id)
        {
            EditStartDate = FromStart(camp.StartDate);
            EditTargetEndDate = FromTarget(camp.TargetEndDate);
        }
        RebuildCalendar();
    }

    private HashSet<string> TransitiveDependents(string rootId)
    {
        var result = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var d in Items)
                if (d.PrerequisiteCampaignIds.Contains(cur) && result.Add(d.Id))
                    queue.Enqueue(d.Id);
        }
        result.Remove(rootId);
        return result;
    }

    private static List<string> SelectedIds(IEnumerable<SelectableOption> options) =>
        options.Where(o => o.IsSelected).Select(o => o.Id).ToList();

    private static List<int> ParseAllowedBlocks(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(s => int.TryParse(s, out var block) ? block : 0)
           .Where(block => block >= 1)
           .Distinct()
           .OrderBy(block => block)
           .ToList();

    private static DateTime? FromStart(DateOnly d) =>
        d == DateOnly.MinValue ? null : d.ToDateTime(TimeOnly.MinValue);

    private static DateTime? FromTarget(DateOnly d) =>
        d == DateOnly.MaxValue ? null : d.ToDateTime(TimeOnly.MinValue);

    private static DateOnly ToStart(DateTime? picked) =>
        picked.HasValue ? DateOnly.FromDateTime(picked.Value) : DateOnly.MinValue;

    private static DateOnly ToTarget(DateTime? picked) =>
        picked.HasValue ? DateOnly.FromDateTime(picked.Value) : DateOnly.MaxValue;
}
