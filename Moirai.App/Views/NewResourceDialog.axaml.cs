using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MOIRAI.Views;

public partial class NewResourceDialog : Window
{
    public Func<string, string, string?>? TryCreate { get; set; }

    public string Heading
    {
        get => HeadingText.Text ?? string.Empty;
        set
        {
            HeadingText.Text = value;
            Title = value;
        }
    }

    public NewResourceDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var error = TryCreate?.Invoke(NameBox.Text ?? string.Empty, ShortNameBox.Text ?? string.Empty);
        if (error is null)
        {
            Close(true);
            return;
        }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
    }
}
