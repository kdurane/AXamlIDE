using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;
using Dock.Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
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
            services.GetRequiredService<XamlCacheKeeper>();    // keeps XAML completion's caches current (nothing else needs it)
            UseDockViews(services);
            Start(desktop, services);
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// What shows first (Settings > Environment > Startup, as in Visual Studio): the start window, which hands over to
    /// the main window once something is chosen (or it's closed: the IDE opens with nothing in it); the main window with
    /// the last solution or folder reopened; or the main window empty.
    /// </summary>
    private static void Start(IClassicDesktopStyleApplicationLifetime desktop, ServiceProvider services)
    {
        var main = services.GetRequiredService<MainWindow>();
        var start = services.GetRequiredService<StartViewModel>();
        var startup = services.GetRequiredService<SettingsViewModel>().Startup;

        if (startup == StartupAction.LastWorkspace && start.MostRecentPath is { } last)
        {
            desktop.MainWindow = main;
            main.Opened += OpenLast;
            void OpenLast(object? sender, EventArgs e)
            {
                main.Opened -= OpenLast;
                services.GetRequiredService<IMessenger>().Send(new OpenWorkspaceMessage(last));
            }
            return;
        }

        if (startup != StartupAction.StartWindow)
        {
            desktop.MainWindow = main;
            return;
        }

        // The start window is the main window until it hands over (dialogs it opens are owned by it).
        var window = new StartWindow { DataContext = start };
        desktop.MainWindow = window;
        var handedOver = false;
        void HandOver()
        {
            if (handedOver)
                return;
            handedOver = true;
            start.Finished -= Chosen;
            desktop.MainWindow = main;
            main.Show();      // before the start window closes, or the app would think its last window had gone
        }
        void Chosen()
        {
            HandOver();
            window.Close();
        }
        start.Finished += Chosen;
        window.Closing += (_, _) => HandOver();   // its X: on into the IDE with nothing open
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
