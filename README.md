# AXamlIDE

A desktop IDE for Avalonia and .NET projects, built with Avalonia.

## What it does

- **Editor**: AvaloniaEdit with TextMate highlighting, semantic highlighting, 22 colour themes, configurable key
  bindings (Visual Studio and VS Code keymaps), snippets, toggle comment, folding, outline, Markdown preview.
- **C#**: completion, diagnostics, code actions, rename, go to definition, find references, CodeLens, inlay hints,
  unnecessary code faded, format document, through Microsoft's Roslyn language server. Optional solution-wide
  analysis puts the errors in closed files in the Problems panel.
- **AXAML**: completion (an in-process engine forked from the Avalonia Visual Studio extension), live preview and
  design view, go to definition into decompiled Avalonia source, colour swatches, and format document with the same
  engine as the XAML Styler extension for Visual Studio and Rider (a project's `Settings.XamlStyler` is used).
- **Projects**: start window, new project and new item templates, clone a repository; open a folder or solution;
  Solution Explorer with file operations; file properties (Build Action); startup project and configuration; build,
  run and publish.
- **Debugging**: breakpoints (with conditions), stepping, locals, watch, call stack, exception settings, inline
  values, data tips and collection visualizers (with export to Excel), through netcoredbg.
- **Testing**: Test Explorer for xUnit, NUnit, MSTest and TUnit projects: run all, run failed, results in the tree.
- **NuGet**: browse, install, update and uninstall packages; installed and transitive packages.
- **Git**: status in the explorer, stage, unstage, commit, side-by-side diffs, history graph.
- **Other**: Search Everywhere (double Shift, as Rider's: types, files, symbols, IDE actions and options), search in
  files, go to file and symbol, Problems panel, terminal, image viewer, dockable and floating
  panels, open files reload when changed on disk.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Roslyn language server, for C# features:
  `dotnet tool install --global roslyn-language-server --prerelease`
- `git` on `PATH`, for the Git features
- Git credential manager, for cloning and pushing over HTTPS:
  `dotnet tool update -g git-credential-manager --version 3.0.1`

The netcoredbg debugger needs no install. The Windows build is committed in `Projects/netcoredbg/`. On Linux and
macOS the first build downloads the official release into `obj/` (set `NetCoreDbgDownload=false` to stop that and
put your own copy in `Projects/netcoredbg/`).

## Build and run

```
dotnet build
dotnet run
```

Tests: `dotnet test Projects/AXamlIDE.Tests`

Optional environment variables (executable paths) are listed at the top of `Program.cs`.

## Settings

Tools > Settings (saved as JSON in the user's application data folder):

- **Text Editor**: font, theme, highlighting, typing helpers, and formatting: format on save, whether AXAML is
  included, and the AXAML indent size (a project's `Settings.XamlStyler` overrides it).
- **IntelliSense**, **Inline Hints**, **Debugging**: the helpers that pop up or add to the code, each can be switched off.
- **Environment**: Solution Explorer on the left or right, track the active item in Solution Explorer, what opens at
  startup.
- **Keyboard**: the keymap, and every command's shortcut.

## Projects

| Project | |
|---|---|
| `AXamlIDE` | The app (Avalonia UI) |
| `Projects/AXamlIDE.Core` | Everything without a UI: language server client, debugger, workspace, NuGet, Git, AXAML completion and formatting |
| `Projects/AXamlIDE.msbuildhost` | MSBuild, run out of process |
| `Projects/AXamlIDE.Tests` | xUnit tests for Core |

How the parts fit together: [docs/architecture.md](docs/architecture.md). What happens step by step for the main
actions (open, type, save, debug...): [docs/flows.md](docs/flows.md). The third-party APIs the code calls:
[docs/api-glossary.md](docs/api-glossary.md).

## Packages used

**App**

- [Avalonia](https://www.nuget.org/packages/Avalonia), [Avalonia.Controls.ColorPicker](https://www.nuget.org/packages/Avalonia.Controls.ColorPicker), [Avalonia.Controls.DataGrid](https://www.nuget.org/packages/Avalonia.Controls.DataGrid): UI
- [Avalonia.AvaloniaEdit](https://www.nuget.org/packages/Avalonia.AvaloniaEdit), [AvaloniaEdit.TextMate](https://www.nuget.org/packages/AvaloniaEdit.TextMate): text editor
- [TextMateSharp](https://www.nuget.org/packages/TextMateSharp), [TextMateSharp.Grammars](https://www.nuget.org/packages/TextMateSharp.Grammars): syntax highlighting
- [Dock.Avalonia](https://www.nuget.org/packages/Dock.Avalonia), [Dock.Avalonia.Themes.Fluent](https://www.nuget.org/packages/Dock.Avalonia.Themes.Fluent), [Dock.Model.Mvvm](https://www.nuget.org/packages/Dock.Model.Mvvm): docking layout
- [Xaml.Behaviors.Avalonia](https://www.nuget.org/packages/Xaml.Behaviors.Avalonia): XAML behaviors
- [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm): view models, commands, messages
- [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection): wiring the services together
- [Markdig](https://www.nuget.org/packages/Markdig): Markdown preview and hover documentation
- [ClosedXML](https://www.nuget.org/packages/ClosedXML): the debugger visualizer's export to Excel

**Core**

- [dnlib](https://www.nuget.org/packages/dnlib): assembly metadata for AXAML completion
- [ICSharpCode.Decompiler](https://www.nuget.org/packages/ICSharpCode.Decompiler): decompiled sources for go to definition from AXAML
- [Microsoft.Diagnostics.Runtime](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime): reads collections from the debuggee's memory for the table visualizer
- [DiffPlex](https://www.nuget.org/packages/DiffPlex): diffs (Git, and applying formatting as small edits)
- [Irony](https://www.nuget.org/packages/Irony): markup extension parsing for the AXAML formatter

**MSBuild host**

- [Microsoft.Build](https://www.nuget.org/packages/Microsoft.Build), [Microsoft.Build.Locator](https://www.nuget.org/packages/Microsoft.Build.Locator)

**Tests**

- [xunit](https://www.nuget.org/packages/xunit), [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk)

**Included source**

- AXAML completion engine: forked from the [Avalonia Visual Studio extension](https://github.com/AvaloniaUI/AvaloniaVS) (MIT), in `Projects/AXamlIDE.Core/CompletionEngine/`
- AXAML formatter: the core of [XAML Styler](https://github.com/Xavalon/XamlStyler) (Apache-2.0), in `Projects/AXamlIDE.Core/XamlStyler/`

**External tools**

- [roslyn-language-server](https://www.nuget.org/packages/roslyn-language-server): C# language server
- [netcoredbg](https://github.com/Samsung/netcoredbg): .NET debugger

## Licence

AXamlIDE is under the [MIT licence](LICENSE). The third-party code, font and debugger it includes keep their own
licences: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
