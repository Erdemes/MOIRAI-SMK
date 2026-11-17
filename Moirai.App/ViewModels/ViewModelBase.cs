using CommunityToolkit.Mvvm.ComponentModel;

namespace MOIRAI.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public virtual void OnNavigatedTo() { }
}
