using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MOIRAI.Views;

public partial class ComparisonWindow : Window
{
    private readonly List<ScrollViewer> _ganttScrollers = new();
    private bool _syncing;

    public ComparisonWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        HookGanttScrollers();
        if (_ganttScrollers.Count == 0)
            LayoutUpdated += OnFirstLayout;
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnFirstLayout;
        HookGanttScrollers();
    }

    private void HookGanttScrollers()
    {
        if (_ganttScrollers.Count > 0) return;
        foreach (var sv in this.GetVisualDescendants().OfType<ScrollViewer>()
                     .Where(s => s.Classes.Contains("ganttScroll")))
        {
            _ganttScrollers.Add(sv);
            sv.ScrollChanged += OnGanttScrollChanged;
        }
    }

    private void OnGanttScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncing || sender is not ScrollViewer src) return;
        if (Math.Abs(e.OffsetDelta.X) < 0.5) return;

        _syncing = true;
        try
        {
            double x = src.Offset.X;
            foreach (var other in _ganttScrollers)
            {
                if (ReferenceEquals(other, src)) continue;
                if (Math.Abs(other.Offset.X - x) > 0.5)
                    other.Offset = new Vector(x, other.Offset.Y);
            }
        }
        finally { _syncing = false; }
    }
}
