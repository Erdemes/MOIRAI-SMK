using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MOIRAI.Persistence;
using MOIRAI.Services;
using MOIRAI.ViewModels;
using MOIRAI.Views;

namespace MOIRAI;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = default!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = Services.GetRequiredService<MainWindowViewModel>();
            Services.GetRequiredService<IDialogService>().Initialize(mainWindow);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IRepository, JsonRepository>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<AnalysisHandoff>();
        services.AddSingleton<ScenarioListStore>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddTransient<PlanViewModel>();
        services.AddTransient<AnalysisViewModel>();
        services.AddTransient<OptimizationViewModel>();
        services.AddTransient<AnimationViewModel>();
        services.AddTransient<CampaignsViewModel>();
        services.AddTransient<PilotsViewModel>();
        services.AddTransient<EngineersViewModel>();
        services.AddTransient<AircraftViewModel>();
        services.AddTransient<LocationsViewModel>();
        services.AddTransient<SimulatorsViewModel>();
        services.AddTransient<ExternalSupportViewModel>();

        return services.BuildServiceProvider();
    }
}
