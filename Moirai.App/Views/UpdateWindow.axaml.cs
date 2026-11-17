using Avalonia.Controls;
using MOIRAI.ViewModels;

namespace MOIRAI.Views;

public partial class UpdateWindow : Window
{
    private UpdateViewModel? _vm;

    public UpdateWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CloseRequested -= CloseFromViewModel;
        _vm = DataContext as UpdateViewModel;
        if (_vm is not null) _vm.CloseRequested += CloseFromViewModel;
    }

    private void CloseFromViewModel() => Close();
}
