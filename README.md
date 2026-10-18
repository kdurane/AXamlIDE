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
Classic MVVM folders, each split by feature. A folder is a namespace (`Views/Editor/Hover` is
`AXamlIDE.Views.Editor.Hover`); `Models/` is one flat namespace. Dependencies only point downwards:
Views -> ViewModels -> Services / Models / Messages.

```
App.axaml(.cs), Program.cs   Startup; AppServices.cs registers every service (dependency injection)
Views/                       Windows, panels and editor UI (anything that is an Avalonia control)
  Shell/                       MainWindow, KeyMap (all shortcuts), ToolPanels (the bottom tool tabs)
  Documents/                   DocumentWellView: tab strip + outline + editor + preview
  Editor/                      EditorPaneView and its feature controllers:
    CodeCompletion/ Hover/ SignatureHelp/ CodeActions/ Folding/ Navigation/ Highlighting/ Debugging/
  Workspace/ Problems/ Search/ Running/ Preview/ Settings/   the other panels and windows
  Dialogs/                     DialogService (implements IDialogService)
ViewModels/                  One view model per panel/window, plus command sets
  Shell/ Documents/ Workspace/ Problems/ Outline/ Running/ Search/ Preview/ Settings/ Editor/
Models/                      App-side data: OpenDocument, ProblemItem, DiagnosticSegment, EditorTheme...
Messages/                    IMessenger messages (status, tool tabs, editor navigation, diagnostics)
Services/                    App-side services that need Avalonia/AvaloniaEdit:
  Documents/ (DocumentManager, saving, DocumentHost)  Languages/ (LanguageSessionService, DocumentSync)
  Editor/ Dialogs/ Settings/ Preview/ (designer host process)
Assets/, Styles/             Theme JSON, icons, shared styles
Projects/
  AXamlIDE.Core/             Everything without a UI (no Avalonia reference allowed). Same namespaces as the app:
    CompletionEngine/          XAML completion engine, forked from the Avalonia VS extension (see its LICENSE.md)
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
- **Panels are self-contained views** with their own view model (`ExplorerView`, `DocumentWellView`, and the tool
  panels listed in `Shell/ToolPanels.cs`). They don't depend on `MainWindow`, so they can later live in other windows.
- **The selected tab** is `DocumentManager.Active`; tab strips, the outline, preview and status bar all follow it.
- **The editor pane** is set up by its `DocumentWellView` (`EditorPane.Initialize`) and otherwise reached only by
  messages (`EditorActionMessage` for commands, `NavigateTo*Message` for navigation).
- Anything that opens a window goes through `IDialogService`.

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

