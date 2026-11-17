using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class ExternalSupportView : UserControl
{
    public ExternalSupportView() => InitializeComponent();

    private async void OnNewAsset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExternalSupportViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await new NewExternalSupportDialog { TryCreate = vm.TryCreate }.ShowDialog(owner);
    }
}
