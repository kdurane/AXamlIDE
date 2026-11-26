using System.Linq;
using AXamlIDE.Commands;
using AXamlIDE.Editor.Highlighting;
using AXamlIDE.Services.Agents;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Snippets;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Mcp;
using AXamlIDE.Services.Navigation;
using AXamlIDE.Services.NuGet;
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
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ExcelService>();
        services.AddSingleton(_ => new SnippetLibrary(SnippetLibrary.DefaultUserFolder));   // code snippets (built-in + the user's)
        services.AddSingleton<EditorCommands>();
        services.AddSingleton<KeyMap>();

        // ---- settings: loaded once at startup; SettingsViewModel is also the live IEditorSettings
        services.AddSingleton<EditorSettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<EditorSettingsStore>().Load());
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<IEditorSettings>(sp => sp.GetRequiredService<SettingsViewModel>());
        services.AddSingleton(_ => new RegistryOptions(ThemeName.DarkPlus));
        services.AddSingleton<EditorGrammars>();   // the built-in grammars plus our AXAML grammar and colours
        services.AddSingleton<CodeHighlighter>();  // colours Markdown code blocks (Claude panel, Markdown preview)
        services.AddSingleton<NuGetService>();
        services.AddSingleton<NuGetManager>();     // opens the NuGet manager tab (explorer: Manage NuGet Packages...)
        services.AddSingleton<PublishManager>();   // opens the Publish tab (Build > Publish, explorer: Publish...)
        services.AddSingleton<ImageViewerManager>();

        // ---- workspace, documents, languages
        services.AddSingleton<WorkspaceModel>();
        services.AddSingleton<WorkspaceStateStore>();
        services.AddSingleton<DocumentManager>();
        services.AddSingleton<DocumentSaveService>();
        services.AddSingleton<ClosedFileBreakpoints>(); // breakpoints of files that aren't open (WorkspaceSession fills it)
        services.AddSingleton<FileChangeWatcher>();     // tells open tabs when their file changes on disk
        services.AddSingleton<DocumentCommands>();
        services.AddSingleton<IDocumentHost, DocumentHost>();
        services.AddSingleton<NavigationHistory>();     // Navigate Backward / Forward
        services.AddSingleton<NavigationService>();
        services.AddSingleton<WorkspaceEditService>();
        services.AddSingleton<DebugSnapshotTracker>();
        services.AddSingleton<EditorServices>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IEditorSettings>();
            var codeAnalysis = sp.GetRequiredService<CodeAnalysisRun>();
            return new LspManager(LspServers.Defaults(() => settings.Features, () => codeAnalysis.IsRunning));   // some settings are server options
        });
        services.AddSingleton<XamlLanguageService>();
        services.AddSingleton<DocumentReloader>();      // reloads or flags those tabs

        // ---- git: the service runs git; the tracker keeps the workspace's status current
        services.AddSingleton<GitService>();
        services.AddSingleton<GitCredentialSetup>();
        services.AddSingleton<GitStatusTracker>();
        services.AddSingleton<CommitMessageWriter>();   // the commit box's sparkle button (claude -p)
        services.AddSingleton<LanguageSessionService>();
        services.AddSingleton<CodeAnalysisRun>();
        services.AddSingleton<SolutionAnalysisService>();   // errors in closed files; Analyze > Run Code Analysis
        services.AddSingleton<XamlCacheKeeper>();          // tells XAML completion when its caches go stale
        services.AddSingleton<XamlAnalysisService>();      // AXAML diagnostics: unused and undeclared xmlns prefixes

        // ---- view models
        services.AddSingleton<PreviewSessions>();          // each AXAML tab's preview and designer host
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<GitTreeDecorator>();
        services.AddSingleton<GitViewModel>();
        services.AddSingleton<PropertiesViewModel>();
        services.AddSingleton<ProblemsViewModel>();
        services.AddSingleton<StatusBarViewModel>();        // messages, Ln/Col, encoding, solution analysis, language server
        services.AddSingleton<OutlineViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SearchEverywhereViewModel>();   // the top bar's box (double Shift)
        services.AddSingleton<RunViewModel>();
        services.AddSingleton<RunTerminalViewModel>();    // the Run panel: the program the IDE ran, in a terminal
        services.AddSingleton<TerminalViewModel>();       // the Terminal panel's shell
        services.AddSingleton<TestExplorerViewModel>();   // the Tests panel (Test Explorer)
        services.AddSingleton<AgentChatViewModel>();      // the Claude panel (claude-agent-acp over ACP)
        services.AddSingleton(new RecentWorkspaces());      // recent.json: the start window's "Open recent"
        services.AddSingleton(_ =>                         // trusted.json: folders whose code may run (Trust and Open)
        {
            var trust = new WorkspaceTrust();
            trust.StartWith(new RecentWorkspaces().Load().Select(item => item.Path));   // first run: what was already opened
            return trust;
        });
        services.AddSingleton<StartViewModel>();          // the start window, File > Recent / New Project / Clone
        services.AddSingleton<LocalsViewModel>();          // the debugger's views (Locals, Watch, Call Stack, visualizers)
        services.AddSingleton<WatchViewModel>();
        services.AddSingleton<CallStackViewModel>();
        services.AddSingleton<BreakpointsViewModel>();     // every breakpoint in the open documents
        services.AddSingleton<DiagnosticsViewModel>();     // the Debug tab's memory and CPU graphs
        services.AddSingleton<DebugSideBarViewModel>();    // the side bar's Debug tab (VS Code's Run and Debug view)
        services.AddSingleton<VisualizerManager>();
        services.AddSingleton<DockLayoutViewModel>();
        services.AddSingleton<McpPreviewRenderer>();      // render_xaml's own designer host
        services.AddSingleton<McpUiHost>();               // ui_open/ui_act's host: the user's app, headless
        services.AddSingleton<IdeMcpServer>();            // the IDE's tools for Claude, on localhost (started in App)
        services.AddSingleton<WorkspaceSession>();
        services.AddSingleton<MainWindowViewModel>();

        // ---- views: the tool panels, the dock tabs' view cache (see App.axaml.cs), the window
        services.AddSingleton<ToolPanels>();
        services.AddSingleton<DockViewCache>();
        services.AddSingleton<ThemeSwitcher>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,   // a missing registration fails at startup, not on first use
            ValidateScopes = true
        });
    }
}
