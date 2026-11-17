using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MOIRAI.ViewModels;

public sealed partial class ScopeGroupNode : ObservableObject
{
    public string Name { get; }
    public IReadOnlyList<SelectableOption> Children { get; }

    private bool _cascading;

    [ObservableProperty] private bool? _isChecked;

    public ScopeGroupNode(string name, IReadOnlyList<SelectableOption> children)
    {
        Name = name;
        Children = children;
        foreach (var c in children) c.PropertyChanged += OnChildChanged;
        Recompute();
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_cascading) return;
        bool selectAll = !Children.All(c => c.IsSelected);
        _cascading = true;
        foreach (var c in Children) c.IsSelected = selectAll;
        _cascading = false;
        Recompute();
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_cascading && e.PropertyName == nameof(SelectableOption.IsSelected)) Recompute();
    }

    private void Recompute()
    {
        _cascading = true;
        IsChecked = Children.All(c => c.IsSelected) ? true
                  : Children.Any(c => c.IsSelected) ? (bool?)null
                  : false;
        _cascading = false;
    }
}
