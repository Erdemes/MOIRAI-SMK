using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MOIRAI.Views;

public partial class GanttWindow : Window
{
    public GanttWindow()
    {
        InitializeComponent();
        Zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyZoom();
        };
        ApplyZoom();

        Chart.SortieClicked += async (_, sortie) =>
        {
            if (DataContext is ViewModels.PlanViewModel vm) await vm.RecordActualsAt(sortie);
        };
        Chart.SimulatorSortieClicked += async (_, sim) =>
        {
            if (DataContext is ViewModels.PlanViewModel vm) await vm.RecordActualsAt(sim);
        };
        Chart.PinRequested += async (_, req) =>
        {
            if (DataContext is ViewModels.PlanViewModel vm) await vm.ApplyPinRequest(req);
        };
    }

    private void ApplyZoom() =>
        Scaler.LayoutTransform = new ScaleTransform(Zoom.Value, Zoom.Value);
}
