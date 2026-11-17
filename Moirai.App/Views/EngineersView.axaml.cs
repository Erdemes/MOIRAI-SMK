using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class EngineersView : UserControl
{
    public EngineersView() => InitializeComponent();

    private async void OnNewFte(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EngineersViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await new NewResourceDialog { Heading = "New FTE", TryCreate = vm.TryCreate }
            .ShowDialog(owner);
    }
}
