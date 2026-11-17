using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class PilotsView : UserControl
{
    public PilotsView() => InitializeComponent();

    private async void OnNewPilot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PilotsViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await new NewResourceDialog { Heading = "New pilot", TryCreate = vm.TryCreate }
            .ShowDialog(owner);
    }
}
