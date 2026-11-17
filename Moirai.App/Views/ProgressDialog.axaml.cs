using Avalonia.Controls;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class ProgressDialog : Window
{
    private bool _allowClose;

    public ProgressDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ProgressDialogViewModel vm)
                vm.CloseRequested += () => { _allowClose = true; Close(); };
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            (DataContext as ProgressDialogViewModel)?.CancelCommand.Execute(null);
        };
    }
}
