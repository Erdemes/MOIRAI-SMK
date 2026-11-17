using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class AircraftView : UserControl
{
    public AircraftView() => InitializeComponent();

    private async void OnNewAircraft(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AircraftViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await new NewAircraftDialog { TryCreate = vm.TryCreate }.ShowDialog(owner);
    }
}
