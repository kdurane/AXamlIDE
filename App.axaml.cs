using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Diagnostics;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Mcp;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;
using Dock.Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Controls;

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
            PerfTimeline.Mark(PerfTimeline.Startup, "avalonia-ready");
            var services = AppServices.Build();
            PerfTimeline.Mark(PerfTimeline.Startup, "services-built");
            ReportUnhandledExceptions(services.GetRequiredService<IMessenger>());
            services.GetRequiredService<ThemeSwitcher>().Apply();
            TextBoxCaret.Install();   // every text box's caret clear of the last letter (Avalonia draws it over it)
            ToolTipCloser.Install();  // no tooltip left on top of other apps after leaving or switching away from a window
            services.GetRequiredService<GitTreeDecorator>();   // colours the explorer tree from then on (nothing else needs it)
            services.GetRequiredService<DocumentReloader>();   // keeps open tabs in step with the disk (nothing else needs it)
            services.GetRequiredService<XamlCacheKeeper>();    // keeps XAML completion's caches current (nothing else needs it)
            services.GetRequiredService<XamlAnalysisService>(); // AXAML files' own diagnostics (nothing else needs it)
            services.GetRequiredService<IdeMcpServer>().Start();   // render_xaml, get_problems... for Claude (panel and terminal)
            UseDockViews(services);
            Start(desktop, services);
            desktop.Exit += (_, _) => services.Dispose();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;   // a hidden window (Search Everywhere) mustn't keep the app alive
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// An exception nobody caught on the UI thread (an event handler, an async void method): as Visual Studio does,
    /// log it and carry on, telling the user in the status bar where the details are. The app only dies for
    /// exceptions off the UI thread (Program.cs logs those too).
    /// </summary>
    private static void ReportUnhandledExceptions(IMessenger messenger)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var path = CrashLog.Write("UI thread", e.Exception);
            e.Handled = true;
            var summary = e.Exception.Message.Split('\n')[0].Trim();
            messenger.Send(new StatusMessage(path is null
                ? $"Something went wrong: {summary}"
                : $"Something went wrong: {summary}  (details in {path})"));
        };
    }

    /// <summary>
    /// What shows first (Settings > Environment > Startup, as in Visual Studio): the start window, which hands over to
    /// the main window once something is chosen (closing it with its X quits, as Visual Studio's does); the main window with
    /// the last solution or folder reopened; or the main window empty.
    /// </summary>
    private static void Start(IClassicDesktopStyleApplicationLifetime desktop, ServiceProvider services)
    {
        var main = services.GetRequiredService<MainWindow>();
        PerfTimeline.Mark(PerfTimeline.Startup, "main-window-created");
        // Whichever window shows first (the main window after the start window is the user's choosing, not startup).
        main.Opened += (_, _) => PerfTimeline.Mark(PerfTimeline.Startup, "window-shown", "main window");
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
        window.Opened += (_, _) => PerfTimeline.Mark(PerfTimeline.Startup, "window-shown", "start window");
        desktop.MainWindow = window;
        void Chosen()
        {
            start.Finished -= Chosen;
            desktop.MainWindow = main;
            main.Show();      // before the start window closes, or the app would think its last window had gone
            window.Close();
        }
        start.Finished += Chosen;
        // Its X closes the app: it's still the main window, so nothing hands over (ShutdownMode.OnMainWindowClose).
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
