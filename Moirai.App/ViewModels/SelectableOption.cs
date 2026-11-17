using CommunityToolkit.Mvvm.ComponentModel;

namespace MOIRAI.ViewModels;

public partial class SelectableOption : ObservableObject
{
    public string Id { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public SelectableOption(string id, string label, bool isSelected)
    {
        Id = id;
        Label = label;
        IsSelected = isSelected;
    }
}
