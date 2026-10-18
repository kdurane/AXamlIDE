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
Code is grouped by feature. Each folder is one namespace (`Editor/Hover` is `AXamlIDE.Editor.Hover`),
and a feature's view, view model and services live together.

```
Shell/            Main window, its view model, the bottom tool-tab indexes
Shared/           Dialog service (IDialogService) and other UI helpers used everywhere
Documents/        OpenDocument, DocumentManager, IDocumentHost: the open files and their state
Workspace/        Open folder/solution, project tree, MSBuild host client, "which project owns this file"
Editor/           The editor pane (EditorPaneView), EditorContext, its messages, and one folder per feature:
  CodeCompletion/   completion popup (XAML engine + LSP)
  Hover/            hover tooltips and markdown rendering
  SignatureHelp/    parameter hints
  CodeActions/      lightbulb margin and quick fixes
  Folding/          code folding
  Navigation/       go to definition, references, rename, quick open, workspace edits
  Highlighting/     TextMate grammars/themes, diagnostics squiggles, occurrence highlights
  Debugging/        breakpoint margin and inline debug values
Lsp/              Language server client, server configs, LSP types and constants
Xaml/             AXAML language service (completion, hover, decompile) on top of CompletionEngine
CompletionEngine/ XAML completion engine, forked from the Avalonia VS extension (see its LICENSE.md)
Preview/          Live AXAML preview pane and the designer host process
Running/          Build/run/debug commands (RunViewModel) and the floating debug toolbar
Debugging/        netcoredbg debug adapter session and stop inspection
Search/           Find/replace in files
Problems/         Problems list items and F8 navigation
Outline/          C# document outline items
Settings/         Settings window, editor settings, persistence, themes
Assets/, Styles/  Theme JSON, icons, shared styles
Projects/         msbuildhost (out-of-process MSBuild) and netcoredbg (debugger binaries, optional)
```

How the pieces talk:
- Views talk to their own view model.
- The editor pane is reached through `EditorPaneView` and talks back only through the messages
  in `Editor/EditorMessages.cs` (search for `Register<SomeMessage>` to find the handler).
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

