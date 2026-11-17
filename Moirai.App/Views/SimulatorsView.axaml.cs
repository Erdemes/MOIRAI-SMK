using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class SimulatorsView : UserControl
{
    public SimulatorsView() => InitializeComponent();

    private async void OnNewSimulator(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SimulatorsViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await new NewSimulatorDialog { TryCreate = vm.TryCreate }.ShowDialog(owner);
    }
}
