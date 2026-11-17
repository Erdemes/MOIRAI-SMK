using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class PinEditorDialog : Window
{
    public PinEditorDialog() => InitializeComponent();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnPin(object? sender, RoutedEventArgs e)
    {
        var error = (DataContext as PinEditorViewModel)?.Apply();
        if (error is null)
        {
            Close(true);
            return;
        }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
    }
}
