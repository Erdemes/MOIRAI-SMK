using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MOIRAI.Views;

public partial class PlanResultsView : UserControl
{
    private bool _syncingScroll;

    public PlanResultsView()
    {
        InitializeComponent();
        GanttZoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyGanttZoom();
        };
        ApplyGanttZoom();

        GanttScroll.ScrollChanged += (_, e) => SyncX(GanttScroll, TpScroll, e);
        TpScroll.ScrollChanged += (_, e) => SyncX(TpScroll, GanttScroll, e);

        GanttChart.SortieClicked += async (_, sortie) =>
        {
            if (DataContext is ViewModels.PlanViewModel vm) await vm.RecordActualsAt(sortie);
        };
        GanttChart.SimulatorSortieClicked += async (_, sim) =>
        {
            if (DataContext is ViewModels.PlanViewModel vm) await vm.RecordActualsAt(sim);
        };
        GanttChart.PinRequested += async (_, req) =>
        {
            if (DataContext is ViewModels.PlanViewModel vm) await vm.ApplyPinRequest(req);
        };
    }

    private void ApplyGanttZoom()
    {
        GanttScaler.LayoutTransform = new ScaleTransform(GanttZoom.Value, GanttZoom.Value);
        TpScaler.LayoutTransform = new ScaleTransform(GanttZoom.Value, GanttZoom.Value);
    }

    private void SyncX(ScrollViewer source, ScrollViewer target, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || Math.Abs(e.OffsetDelta.X) < 0.5) return;
        _syncingScroll = true;
        try
        {
            if (Math.Abs(target.Offset.X - source.Offset.X) > 0.5)
                target.Offset = new Vector(source.Offset.X, target.Offset.Y);
        }
        finally
        {
            _syncingScroll = false;
        }
    }
}
