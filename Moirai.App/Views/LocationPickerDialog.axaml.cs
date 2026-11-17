using Avalonia.Controls;
using Avalonia.Interactivity;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class LocationPickerDialog : Window
{
    public LocationPickerDialog()
    {
        InitializeComponent();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LocationPickerViewModel vm) vm.HasPin = false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}