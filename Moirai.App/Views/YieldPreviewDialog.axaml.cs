using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MOIRAI.Views;

public partial class YieldPreviewDialog : Window
{
    public YieldPreviewDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
