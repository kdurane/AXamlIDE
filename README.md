# AXamlIDE

A desktop IDE for Avalonia and .NET projects, built with Avalonia.

## What it does

- **Editor**: AvaloniaEdit with TextMate highlighting, 22 colour themes, configurable key bindings, snippets,
  toggle comment, folding, outline.
- **C#**: completion, diagnostics, code actions, rename, go to definition, find references and CodeLens, through
  Microsoft's Roslyn language server.
- **AXAML**: completion (an in-process engine forked from the Avalonia Visual Studio extension), live preview and
  design view.
- **Projects**: open a folder or solution; solution explorer with file operations; file properties (Build Action);
  startup project and configuration; build, run and publish.
- **Debugging**: breakpoints, stepping, locals, watch, call stack, data tips and collection visualizers (with export
  to Excel), through netcoredbg.
- **NuGet**: browse, install, update and uninstall packages; installed and transitive packages.
- **Git**: status in the explorer, stage, unstage, commit, side-by-side diffs.
- **Other**: search in files, Problems panel, image viewer, dockable and floating panels, open files reload when
  changed on disk.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Roslyn language server, for C# features:
  `dotnet tool install --global roslyn-language-server --prerelease`
- `git` on `PATH`, for the Git features
- Git credential manager `dotnet tool update -g git-credential-manager --version 3.0.1`

The netcoredbg debugger is bundled in `Projects/netcoredbg/`.

## Build and run

```
dotnet build
dotnet run
```

Tests: `dotnet test Projects/AXamlIDE.Tests`

Optional environment variables (executable paths) are listed at the top of `Program.cs`.

## Projects

| Project | |
|---|---|
| `AXamlIDE` | The app (Avalonia UI) |
| `Projects/AXamlIDE.Core` | Everything without a UI: language server client, debugger, workspace, NuGet, Git, AXAML completion |
| `Projects/AXamlIDE.msbuildhost` | MSBuild, run out of process |
| `Projects/AXamlIDE.Tests` | xUnit tests for Core |

## Packages used

**App**

- [Avalonia.AvaloniaEdit](https://www.nuget.org/packages/Avalonia.AvaloniaEdit), [AvaloniaEdit.TextMate](https://www.nuget.org/packages/AvaloniaEdit.TextMate): text editor
- [TextMateSharp](https://www.nuget.org/packages/TextMateSharp), [TextMateSharp.Grammars](https://www.nuget.org/packages/TextMateSharp.Grammars): syntax highlighting
- [Dock.Avalonia](https://www.nuget.org/packages/Dock.Avalonia), [Dock.Avalonia.Themes.Fluent](https://www.nuget.org/packages/Dock.Avalonia.Themes.Fluent), [Dock.Model.Mvvm](https://www.nuget.org/packages/Dock.Model.Mvvm): docking layout
- [Xaml.Behaviors.Avalonia](https://www.nuget.org/packages/Xaml.Behaviors.Avalonia): XAML behaviors

**Core**

- [dnlib](https://www.nuget.org/packages/dnlib): assembly metadata for AXAML completion
- [ICSharpCode.Decompiler](https://www.nuget.org/packages/ICSharpCode.Decompiler): decompiled sources for go to definition from AXAML
- [Microsoft.Diagnostics.Runtime](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime): reads collections from the debuggee's memory for the table visualizer
- [DiffPlex](https://www.nuget.org/packages/DiffPlex): diffs

**MSBuild host**

- [Microsoft.Build](https://www.nuget.org/packages/Microsoft.Build), [Microsoft.Build.Locator](https://www.nuget.org/packages/Microsoft.Build.Locator)

**Tests**

- [xunit](https://www.nuget.org/packages/xunit), [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk)

**External tools**

- [roslyn-language-server](https://www.nuget.org/packages/roslyn-language-server): C# language server
- [netcoredbg](https://github.com/Samsung/netcoredbg): .NET debugger
