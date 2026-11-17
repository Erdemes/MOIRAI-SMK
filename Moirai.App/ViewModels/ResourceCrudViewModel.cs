using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;
using MOIRAI.Persistence;

namespace MOIRAI.ViewModels;

public abstract partial class ResourceCrudViewModel<T> : ViewModelBase where T : Resource, new()
{
    protected readonly IRepository Repo;

    private bool _isLoadingPlanner;

    public ObservableCollection<T> Items { get; } = new();

    public WindowPlannerViewModel Planner { get; } = new();

    public ObservableCollection<Campaign> AssignedCampaigns { get; } = new();

    public ObservableCollection<SelectableOption> TypeRatingOptions { get; } = new();
    public bool HasAircraft => TypeRatingOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private T? _selectedItem;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editShortName = string.Empty;

    [ObservableProperty]
    private string _editQualifications = string.Empty;

    [ObservableProperty]
    private WindowsCalendarData? _calendarData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasSelection => SelectedItem is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasAssignedCampaigns => AssignedCampaigns.Count > 0;

    protected ResourceCrudViewModel(IRepository repo)
    {
        Repo = repo;
        Planner.Changed += OnPlannerChanged;
    }

    public override void OnNavigatedTo()
    {
        Refresh();
        RebuildCalendar();
    }

    protected abstract IReadOnlyList<T> Load();
    protected abstract void Add(T entity);
    protected abstract void Update(T entity);
    protected abstract void Remove(string id);

    protected abstract bool IsAssignedTo(Campaign campaign, string id);

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in Load()) Items.Add(item);
    }

    partial void OnSelectedItemChanged(T? value)
    {
        ErrorMessage = null;
        EditName = value?.Name ?? string.Empty;
        EditShortName = value?.ShortName ?? string.Empty;
        EditQualifications = value is null ? string.Empty : string.Join(", ", value.Qualifications);

        AssignedCampaigns.Clear();
        if (value is not null)
            foreach (var c in Repo.GetCampaigns().Where(c => IsAssignedTo(c, value.Id)))
                AssignedCampaigns.Add(c);
        OnPropertyChanged(nameof(HasAssignedCampaigns));

        TypeRatingOptions.Clear();
        foreach (var a in Repo.GetAircraft())
            TypeRatingOptions.Add(new SelectableOption(a.Id, $"{a.Name} ({a.Id})",
                value?.TypeRatings.Contains(a.Id) == true));
        OnPropertyChanged(nameof(HasAircraft));

        _isLoadingPlanner = true;
        Planner.Load(value?.LeaveWindows ?? Enumerable.Empty<DateRange>());
        _isLoadingPlanner = false;
    }

    public string? TryCreate(string name, string shortName)
    {
        name = name.Trim();
        shortName = shortName.Trim();
        if (name.Length == 0) return "Name is required.";
        if (shortName.Length > 4) shortName = shortName[..4];

        var entity = new T { Name = name, ShortName = shortName };
        Add(entity);
        Refresh();
        SelectedItem = Items.FirstOrDefault(x => x.Id == entity.Id);
        RebuildCalendar();
        return null;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        if (SelectedItem is not { } person) return;
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Name is required.";
            return;
        }

        var shortName = EditShortName.Trim();
        if (shortName.Length > 4) shortName = shortName[..4];
        person.Name = EditName.Trim();
        person.ShortName = shortName;
        person.Qualifications = ParseQualifications(EditQualifications);
        person.TypeRatings = TypeRatingOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        Update(person);

        var id = person.Id;
        Refresh();
        SelectedItem = Items.FirstOrDefault(x => x.Id == id);
        RebuildCalendar();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is not { } person) return;
        try
        {
            Remove(person.Id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        Refresh();
        RebuildCalendar();
    }

    [RelayCommand]
    private void ApplyWindowChange(WindowChange? change)
    {
        if (change is null) return;
        var person = Items.FirstOrDefault(p => p.Id == change.RowId);
        if (person?.LeaveWindows.FirstOrDefault(w => w.Id == change.WindowId) is not { } win) return;

        win.Start = change.NewStart;
        win.End = change.NewEnd;
        Update(person);

        if (SelectedItem?.Id == person.Id)
        {
            _isLoadingPlanner = true;
            Planner.Load(person.LeaveWindows);
            _isLoadingPlanner = false;
        }
        RebuildCalendar();
    }

    private void OnPlannerChanged()
    {
        if (!_isLoadingPlanner && SelectedItem is { } person)
        {
            person.LeaveWindows = Planner.ToList();
            Update(person);
        }
        RebuildCalendar();
    }

    private void RebuildCalendar()
    {
        var rows = Items.Select(p => new WindowsCalendarRow
        {
            Id = p.Id,
            Name = p.Name,
            Windows = p.LeaveWindows.ToList(),
        }).ToList();

        CalendarData = WindowsCalendarData.Build(
            WindowsCalendarKind.Leave, Repo.GetScenario(), rows, SelectedItem?.Id);
    }

    private static HashSet<string> ParseQualifications(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .ToHashSet();
}
