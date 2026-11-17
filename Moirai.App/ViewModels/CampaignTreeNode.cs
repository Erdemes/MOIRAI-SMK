using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MOIRAI.ViewModels;

public sealed partial class CampaignTreeNode : ObservableObject
{
    public required string Id { get; init; }
    public required bool IsGroup { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;

    [ObservableProperty] private bool _isExpanded = true;

    public string Icon => IsGroup ? "◈" : "◎";

    public string Tooltip => Subtitle.Length > 0 ? $"{Name} · {Subtitle}" : Name;

    public ObservableCollection<CampaignTreeNode> Children { get; } = new();
}
