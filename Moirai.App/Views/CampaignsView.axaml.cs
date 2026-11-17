using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class CampaignsView : UserControl
{
    private CampaignTreeNode? _dragNode;
    private Point _pressPoint;
    private bool _dragging;

    public CampaignsView()
    {
        InitializeComponent();
        CampaignTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        CampaignTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        CampaignTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _dragNode = e.GetCurrentPoint(CampaignTree).Properties.IsLeftButtonPressed
                    && NodeOf(e.Source) is { IsGroup: false } node
            ? node
            : null;
        if (_dragNode is not null) _pressPoint = e.GetPosition(CampaignTree);
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode is null) return;
        if (!e.GetCurrentPoint(CampaignTree).Properties.IsLeftButtonPressed) { _dragNode = null; return; }

        if (!_dragging)
        {
            var p = e.GetPosition(CampaignTree);
            if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6) return;
            _dragging = true;
            e.Pointer.Capture(CampaignTree);
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
        e.Handled = true;
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var node = _dragNode;
        bool wasDragging = _dragging;
        _dragNode = null;
        _dragging = false;
        if (!wasDragging || node is null) return;

        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        e.Handled = true;

        if (DataContext is not CampaignsViewModel vm) return;

        var pos = e.GetPosition(CampaignTree);
        if (pos.X < 0 || pos.Y < 0 || pos.X > CampaignTree.Bounds.Width || pos.Y > CampaignTree.Bounds.Height)
            return;

        var hit = CampaignTree.InputHitTest(pos) as Control;
        var target = NodeOf(hit);
        string? groupId = target is null
            ? string.Empty
            : target.IsGroup ? target.Id : vm.GroupIdOf(target.Id);
        vm.Reparent(node.Id, groupId);
    }

    private static CampaignTreeNode? NodeOf(object? source)
    {
        var c = source as Control;
        while (c is not null)
        {
            if (c.DataContext is CampaignTreeNode n) return n;
            c = c.Parent as Control;
        }
        return null;
    }
}
