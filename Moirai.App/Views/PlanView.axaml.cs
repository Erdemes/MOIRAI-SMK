using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class PlanView : UserControl
{
    private const double SettingsPaneDefaultWidth = 640;

    private const double CollapsedResultsWidth = 58;

    private bool _resultsOpen = true;

    public PlanView()
    {
        InitializeComponent();
        SettingsSplit.SizeChanged += (_, _) => UpdateSettingsPaneWidth();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is PlanViewModel vm) ApplyOpeningLayout(vm.HasResult);
        });
    }

    private void ApplyOpeningLayout(bool hasResult)
    {
        SetSettingsOpen(!hasResult);
        SetResultsOpen(hasResult);
    }

    private void UpdateSettingsPaneWidth()
    {
        double total = SettingsSplit.Bounds.Width;
        SettingsSplit.OpenPaneLength =
            !_resultsOpen && total > SettingsPaneDefaultWidth + CollapsedResultsWidth
                ? total - CollapsedResultsWidth
                : SettingsPaneDefaultWidth;
    }

    private void OnToggleSettings(object? sender, RoutedEventArgs e) =>
        SetSettingsOpen(!SettingsSplit.IsPaneOpen);

    private void SetSettingsOpen(bool open)
    {
        SettingsSplit.IsPaneOpen = open;
        SettingsToggle.Content = open ? "«" : "»";
    }

    private void OnToggleResults(object? sender, RoutedEventArgs e) => SetResultsOpen(!_resultsOpen);

    private void SetResultsOpen(bool open)
    {
        _resultsOpen = open;
        ResultsContent.IsVisible = open;
        ResultsTitle.IsVisible = open;
        UndockResultsButton.IsVisible = open;
        AnalyzeButton.IsVisible = open;
        ResultsCollapsedLabel.IsVisible = !open;
        ResultsToggle.Content = open ? "«" : "»";
        ResultsArea.Width = open ? double.NaN : 44;
        ResultsArea.HorizontalAlignment = open ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        UpdateSettingsPaneWidth();
    }
}
