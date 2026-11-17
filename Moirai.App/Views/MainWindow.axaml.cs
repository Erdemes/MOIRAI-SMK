using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm)
            await vm.CheckForUpdatesOnStartupAsync();
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleNav(object? sender, RoutedEventArgs e) =>
        NavSplit.IsPaneOpen = !NavSplit.IsPaneOpen;

    private void OnScenarioDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ScenarioItem item }) return;
        item.BeginEdit();
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is Control v
                && v.GetVisualParent()?.GetVisualDescendants().OfType<TextBox>()
                       .FirstOrDefault(t => t.Name == "RenameBox") is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Background);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ScenarioItem item }) return;
        if (e.Key == Key.Enter) { CommitRename(item); e.Handled = true; }
        else if (e.Key == Key.Escape) { item.CancelEdit(); e.Handled = true; }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ScenarioItem item } && item.IsEditing)
            CommitRename(item);
    }

    private void CommitRename(ScenarioItem item)
    {
        if (DataContext is MainWindowViewModel vm) vm.CommitRenameScenario(item);
    }
}
