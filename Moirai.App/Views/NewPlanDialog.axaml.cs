using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MOIRAI.Views;

public partial class NewPlanDialog : Window
{
    public Func<string, string?>? TryCreate { get; set; }

    public NewPlanDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var error = TryCreate?.Invoke(NameBox.Text ?? string.Empty);
        if (error is null)
        {
            Close(true);
            return;
        }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
    }
}
