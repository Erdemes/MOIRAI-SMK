using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MOIRAI.Views;

public partial class NewAircraftDialog : Window
{
    public Func<string, string, string?>? TryCreate { get; set; }

    public NewAircraftDialog()
    {
        InitializeComponent();
        Opened += (_, _) => TailBox.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var error = TryCreate?.Invoke(TailBox.Text ?? string.Empty, NameBox.Text ?? string.Empty);
        if (error is null)
        {
            Close(true);
            return;
        }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
    }
}
