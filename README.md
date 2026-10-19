# AXamlIDE

## Project Overview
AXamlIDE is an IDE built with Avalonia and .NET for XML/AXAML development. It provides intelligent code completion, real-time syntax checking, and a customizable editor interface.

## Key Features
- 🧠 Smart code completion with Dnlib metadata analysis
- 📌 Customizable margin indicators for code navigation
- 🛠️ Extensible editor features via plugin system
- 🌐 Cross-platform development support
- 🧪 Integrated diagnostic rendering and occurrence tracking

## Folder Structure
MVVM folders. A folder is a namespace (`Editor/Hover` is `AXamlIDE.Editor.Hover`). Dependencies point one way:
Views -> ViewModels / Commands -> Services / Models / Messages.

```
App.axaml(.cs), Program.cs   Startup; AppServices.cs registers every service (dependency injection)
Views/                       Every window and panel (.axaml + code-behind), DialogService, view helpers
ViewModels/                  One view model per window/panel (MainWindow, Explorer, Problems, Outline,
                             Run (+ .Debug, .Startup), Search, Preview, Settings, Workspace), plus the dock layout:
                             DockLayoutViewModel, DockFactory, DocumentDockable / ToolDockable
Commands/                    Command sets shared by menus, KeyMap and panels: DocumentCommands, EditorCommands
Shell/                       KeyMap (all shortcuts, attached to every window), ToolPanels (the view for each tool tab)
Editor/                      The text editor's feature controllers (used by Views/EditorPaneView):
  CodeCompletion/ Hover/ SignatureHelp/ CodeActions/ Folding/ Navigation/ Highlighting/ Debugging/
Models/                      App-side data: OpenDocument, ProblemItem, DiagnosticSegment, EditorTheme...
Messages/                    IMessenger messages (status, tool tabs, editor actions/navigation, diagnostics)
Services/                    App-side services that need Avalonia/AvaloniaEdit:
  Documents/ (DocumentManager, saving, DocumentHost)  Languages/ (LanguageSessionService, DocumentSync)
  Editor/ (EditorServices, NavigationService, WorkspaceEditService)  Dialogs/ (IDialogService)  Settings/ (IEditorSettings)  Preview/ (designer host)
Assets/, Styles/             Theme JSON, icons, shared styles
Projects/
  AXamlIDE.Core/             Everything without a UI (no Avalonia reference allowed). Same namespaces as the app:
    CompletionEngine/          XAML completion engine, forked from the Avalonia VS extension (see its UPSTREAM.md)
    Services/Lsp/              language server client, server configs, LSP types
    Services/Debugging/        netcoredbg debug adapter session
    Services/Workspace/        workspace model, solutions, MSBuild host client
    Services/Xaml/ Search/ Preview/ Problems/ Running/ Settings/   the rest of the logic
    Models/                    ProjectTreeNode, WorkspaceSearchResult, EditorSettings, XamlPreviewTarget
  AXamlIDE.Tests/            xUnit tests for Core: `dotnet test Projects/AXamlIDE.Tests`
  AXamlIDE.msbuildhost/      out-of-process MSBuild
  netcoredbg/                debugger binaries (optional)
```

How the pieces are put together and talk:
- **`AppServices.cs` builds everything** (Microsoft.Extensions.DependencyInjection). A class gets what it
  needs as constructor parameters; to add a service, register it there. No callbacks are passed into constructors.
- **Views talk to their own view model** (bindings, or the view calling the VM directly).
- **Direct dependency** when A naturally uses B: inject B and call it, or subscribe to B's events
  (e.g. `OutlineViewModel` listens to `DocumentManager.ActiveChanged`).
- **A message** (`IMessenger`) when the sender shouldn't know who listens: status text, "show this tool tab",
  "navigate the editor here", "diagnostics changed". Messages live in `Messages/`. Find the handler by searching for `IRecipient<ThatMessage>`
  or `Register<ThatMessage>`.
- **Docking** uses the Dock library (`Dock.Avalonia`). `ViewModels/DockLayoutViewModel` owns the layout and is the only
  code that talks to it: one document tab per open document, tool tabs from `ViewModels/DockFactory`, and closing a
  floating window moves its tabs back. `Views/DockViewCache` (plugged in as Dock's control cache) makes each tab's
  view once and moves it with the tab: a `DocumentView` per document (its editor, plus the AXAML preview area with
  Code / Split / Design and the split direction), and the panel view per tool. There is one live preview; it moves
  into the selected AXAML tab, and the outline drop-down moves into the selected tab's bar.
- **Per-workspace state** lives in a hidden `.axamlide/workspace.json` in the opened folder (open files, caret and
  scroll, preview layout, dock sizes, the Search window's place): `ViewModels/WorkspaceSession` restores it on open
  and saves it on close/exit, using `Services/Workspace/WorkspaceStateStore` (Core).
- **Colours** use Avalonia's theme variants. Each theme in Settings is a `ThemeVariant` (`Views/ThemeVariants`, based
  on Dark or Light) with its own colour file, `Styles/Themes/<Id>.axaml`, whose keys are VS Code's theme colour names
  (`editor.background`, `sideBar.background`...). `Styles/ThemeResources.axaml` lists the themes and defines the brushes
  the views use (our `App*`/`Theme*` ones and the Dock/Fluent ones we restyle), each coloured from a theme key.
  Choosing a theme only sets `Application.RequestedThemeVariant` (`Views/ThemeSwitcher`); Fluent and Dock fall back to
  their Dark or Light look for anything not set. To colour another part, add a brush there and use it as
  `{DynamicResource ...}`; code that draws for itself (editor margins, renderers, the hover card) looks brushes up with
  `Views/ThemeBrushes.Get` when it draws. AvaloniaEdit's completion list is styled in `Styles/EditorStyles.axaml`.
  No colour is hard-coded. The editor's syntax colours come from the same theme through TextMate.
- **AXAML highlighting** has its own grammar, `Assets/Grammars/axaml.tmLanguage.json` (scope `text.xml.axaml`), with a
  scope for every part: comments, tags and property elements, attributes and attached properties, `x:` directives,
  `xmlns`, and markup extensions split into name / parameters / values. `Assets/Grammars/axaml-colors.json` holds AXAML
  colours laid over every theme (a "dark" and a "light" set; it lists all the scopes): change or add a rule there, or
  leave a scope out to keep the theme's colour. `Editor/Highlighting/EditorGrammars` registers both with TextMate.
- **Git** goes through the `git` command line (`Services/Git/GitService`, Core; status is parsed from
  `git status --porcelain=v2 -z`). `Services/Git/GitStatusTracker` keeps the workspace repository's status current
  (on open, on save, and from a debounced file watcher that includes `.git/index`, HEAD and refs) and raises `Changed`.
  `ViewModels/GitTreeDecorator` sets each explorer node's `GitState` (coloured by `Views/GitStateConverters`);
  `ViewModels/GitViewModel` is the Git tab (stage, unstage, commit); `ViewModels/DiffDockable` + `Views/DiffView` are
  the side-by-side diff tabs (DiffPlex, via `Services/Git/SideBySideDiff`). Colours are the `gitDecoration.*` and
  `diffEditor.*` theme keys.
- **Properties panel** (right-click > Properties, F4, Alt+Enter) sits under the explorer (`DockFactory.ExplorerColumn`) and
  follows the explorer selection (`ViewModels/PropertiesViewModel`, `Views/PropertiesView`). A file's Build Action and
  metadata are read and written by the MSBuild host (`ItemProperties.cs` there; requests in the shared
  `Projects/AXamlIDE.msbuildhost/HostProtocol.cs`, compiled into Core too). Changes are written to the .csproj at once.
- **Panels are self-contained views** with their own view model (listed in `Shell/ToolPanels.cs`). They don't depend
  on the window they're in.
- **The selected tab** is `DocumentManager.Active`, in whichever window it is; the menus, outline, preview and status
  bar all act on it.
- **Each document has its own editor** (`Views/EditorPaneView`). Editor commands (`EditorActionMessage`) run only in
  the one showing `Active`. Navigation asks the document, not an editor: `NavigationService` opens/activates it and
  calls `OpenDocument.Reveal(...)`, and the editor showing it selects, scrolls and focuses (even one created later).
- Anything that opens a window goes through `IDialogService`.
- **Run and Debug** start the startup project chosen in the top bar (Visual Studio style: startup project box,
  Debug/Release box, play button that becomes stop), built with that configuration (`ViewModels/RunViewModel.Startup`).
  The box lists the workspace's executable projects; with several and none chosen, Run/Debug ask. The choice is
  remembered in `.axamlide/` and the startup project is bold in the explorer ("Set as Startup Project" there too).

## Additional Requirements
- **Roslyn LSP Server**: Install globally via `dotnet tool install --global roslyn-language-server --prerelease`
- **Debugger (optional)**: put the netcoredbg binaries in `Projects/netcoredbg/`; the build copies them next to the app.
  Or set `AXAMLIDE_NETCOREDBG_PATH`, or have `netcoredbg` on `PATH`.
- Other environment overrides are listed at the top of `Program.cs`.

## Getting Started
1. Clone the repository
2. Restore NuGet packages via `dotnet restore`
3. Build the solution with `dotnet build`
4. Run the application using `dotnet run`

