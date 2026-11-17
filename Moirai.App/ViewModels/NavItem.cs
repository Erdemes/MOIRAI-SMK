namespace MOIRAI.ViewModels;

public sealed class NavItem
{
    public string Title { get; }
    public string Icon { get; }
    public ViewModelBase? ViewModel { get; }

    public bool IsHeader => ViewModel is null;

    public NavItem(string title, string icon, ViewModelBase viewModel)
    {
        Title = title;
        Icon = icon;
        ViewModel = viewModel;
    }

    private NavItem(string title)
    {
        Title = title;
        Icon = string.Empty;
        ViewModel = null;
    }

    public static NavItem Header(string title) => new(title);
}
