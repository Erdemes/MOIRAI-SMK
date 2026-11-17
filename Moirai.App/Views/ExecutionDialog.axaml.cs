using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class ExecutionDialog : Window
{
    public ExecutionDialog()
    {
        InitializeComponent();

        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ExecutionViewModel { HighlightIndex: var i and >= 0 })
                RowScroll.Offset = RowScroll.Offset.WithY(Math.Max(0, i * 46 - 46));
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var error = (DataContext as ExecutionViewModel)?.Apply();
        if (error is null)
        {
            Close(true);
            return;
        }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
    }
}
