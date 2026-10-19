using AXamlIDE.Commands;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Workspace;
using AXamlIDE.Services.Xaml;
using AXamlIDE.Shell;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using TextMateSharp.Grammars;

namespace AXamlIDE;

/// <summary>
/// The composition root: the one place that decides which concrete class backs each service.
/// To add a service, register it here and take it as a constructor parameter where it's needed.
/// To swap one (a fake for tests, a different implementation), change only its registration.
///
/// Everything is a singleton: there is one main window and one of each service. The container
/// owns them and disposes the IDisposable ones when the app exits (see App.axaml.cs).
/// </summary>
public static class AppServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ---- cross-cutting
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<EditorCommands>();
        services.AddSingleton<KeyMap>();

        // ---- settings: loaded once at startup; SettingsViewModel is also the live IEditorSettings
        services.AddSingleton<EditorSettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<EditorSettingsStore>().Load());
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<IEditorSettings>(sp => sp.GetRequiredService<SettingsViewModel>());
        services.AddSingleton(_ => new RegistryOptions(ThemeName.DarkPlus));

        // ---- workspace, documents, languages
        services.AddSingleton<WorkspaceModel>();
        services.AddSingleton<WorkspaceStateStore>();
        services.AddSingleton<DocumentManager>();
        services.AddSingleton<DocumentSaveService>();
        services.AddSingleton<DocumentCommands>();
        services.AddSingleton<IDocumentHost, DocumentHost>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<WorkspaceEditService>();
        services.AddSingleton<DebugSnapshotTracker>();
        services.AddSingleton<EditorServices>();
        services.AddSingleton(_ => new LspManager(LspServers.Defaults));
        services.AddSingleton<XamlLanguageService>();
        services.AddSingleton<LanguageSessionService>();

        // ---- view models
        services.AddSingleton<PreviewPaneViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<ProblemsViewModel>();
        services.AddSingleton<OutlineViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<RunViewModel>();
        services.AddSingleton<DockLayoutViewModel>();
        services.AddSingleton<WorkspaceSession>();
        services.AddSingleton<MainWindowViewModel>();

        // ---- views: the tool panels, the dock tabs' view cache (see App.axaml.cs), the window
        services.AddSingleton<ToolPanels>();
        services.AddSingleton<DockViewCache>();
        services.AddSingleton<ThemeResources>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,   // a missing registration fails at startup, not on first use
            ValidateScopes = true
        });
    }
}
