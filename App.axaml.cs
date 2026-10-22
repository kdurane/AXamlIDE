using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AXamlIDE.Services.Documents;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;
using Dock.Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AXamlIDE;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Build();
            services.GetRequiredService<ThemeSwitcher>().Apply();
            services.GetRequiredService<GitTreeDecorator>();   // colours the explorer tree from then on (nothing else needs it)
            services.GetRequiredService<DocumentReloader>();   // keeps open tabs in step with the disk (nothing else needs it)
            UseDockViews(services);
            desktop.MainWindow = services.GetRequiredService<MainWindow>();
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// App-wide (so floating tab windows get them too): the views of dock tabs come from DockViewCache,
    /// set as every DockControl's control cache; the welcome screen is an ordinary data template.
    /// Resolving the cache also creates the editor services, so navigation messages are handled from the start.
    /// </summary>
    private void UseDockViews(ServiceProvider services)
    {
        // The explicit match matters: FuncDataTemplate<T> on its own also matches null, so every empty
        // ContentPresenter in the app (an empty ComboBox's selection box...) would show the welcome screen.
        DataTemplates.Add(new FuncDataTemplate<WelcomeViewModel>(
            welcome => welcome is not null,
            (welcome, _) => new WelcomeView { DataContext = welcome }));
        Styles.Add(new Style(selector => selector.OfType<DockControl>())
        {
            Setters =
            {
                new Setter(ControlRecyclingDataTemplate.ControlRecyclingProperty,
                    services.GetRequiredService<DockViewCache>())
            }
        });
    }
}
