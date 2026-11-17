using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Domain;

namespace MOIRAI.ViewModels;

public partial class WindowPlannerViewModel : ViewModelBase
{
    public ObservableCollection<DateRange> Windows { get; } = new();

    [ObservableProperty]
    private DateTime? _newStart;

    [ObservableProperty]
    private DateTime? _newEnd;

    [ObservableProperty]
    private string _newNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string? _inputError;

    public bool HasInputError => !string.IsNullOrWhiteSpace(InputError);

    public bool IsEmpty => Windows.Count == 0;

    public event Action? Changed;

    public void Load(IEnumerable<DateRange> windows)
    {
        Windows.Clear();
        foreach (var w in windows.OrderBy(w => w.Start)) Windows.Add(w);
        NewStart = null;
        NewEnd = null;
        NewNote = string.Empty;
        InputError = null;
        NotifyChanged();
    }

    public List<DateRange> ToList() => Windows.ToList();

    [RelayCommand]
    private void Add()
    {
        InputError = null;
        if (NewStart is not { } rawStart || NewEnd is not { } rawEnd)
        {
            InputError = "Pick both a start and an end date.";
            return;
        }

        var start = DateOnly.FromDateTime(rawStart);
        var end = DateOnly.FromDateTime(rawEnd);
        if (end < start) (start, end) = (end, start);
        var note = NewNote.Trim();

        if (Windows.FirstOrDefault(w => w.Start == start && w.End == end) is { } existing)
        {
            int i = Windows.IndexOf(existing);
            Windows[i] = new DateRange { Start = start, End = end, Note = note };
        }
        else
        {
            int at = 0;
            while (at < Windows.Count && Windows[at].Start <= start) at++;
            Windows.Insert(at, new DateRange { Start = start, End = end, Note = note });
        }

        NewStart = null;
        NewEnd = null;
        NewNote = string.Empty;
        NotifyChanged();
    }

    [RelayCommand]
    private void Remove(DateRange window)
    {
        Windows.Remove(window);
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        Changed?.Invoke();
    }
}
