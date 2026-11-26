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
  values, data tips and collection visualizers (with export to Excel), through SharpDbg or netcoredbg.
- **Testing**: Test Explorer for xUnit, NUnit, MSTest and TUnit projects: run all, run failed, results in the tree.
- **NuGet**: browse, install, update and uninstall packages; installed and transitive packages.
- **Git**: status in the explorer, stage, unstage, commit, side-by-side diffs, history graph.
- **Claude**: a Claude panel (Claude Code over the Agent Client Protocol), and the IDE as an MCP server on
  `http://localhost:47613/mcp` so Claude, in the panel or in a terminal
  (`claude mcp add --transport http axamlide http://localhost:47613/mcp`), can use what only the IDE knows: the
  editor's caret, selection and unsaved edits; the Roslyn server's symbols, types, definitions and references;
  one file's errors on demand, the Problems list and the IDE's build; how long startup, opening and building took; AXAML rendered as a picture; the paused
  debugger's call stack, locals and expressions; the Output pane and test results. Claude can also try the UI
  itself: `ui_open` runs a view of the app in a hidden headless window and gives its control tree as text, and
  `ui_act` clicks, types and presses keys in it and says what changed (`ui_tree` and `ui_screenshot` look again).
  Text costs Claude far less than screenshots and is exact.
- **Other**: Search Everywhere (double Shift, as Rider's: types, files, symbols, IDE actions and options), search in
  files, go to file and symbol, Problems panel, terminal, image viewer, dockable and floating
  panels, open files reload when changed on disk.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Roslyn language server, for C# features:
  `dotnet tool install --global roslyn-language-server --prerelease`.
  It's needed to build, too: `Projects/AXamlIDE.RoslynExtension` compiles against its assemblies. Without it the
  build warns, and solution-wide analysis uses Roslyn's slower `workspace/diagnostic` instead. After updating it,
  see [what to check](docs/architecture.md#when-roslyn-language-server-updates).
- `git` on `PATH`, for the Git features
- Git credential manager, for cloning and pushing over HTTPS:
  `dotnet tool update -g git-credential-manager --version 3.0.1`

### Debuggers

The debuggers need no install. Both are pinned to one version in `AXamlIDE.csproj` (`SharpDbgVersion`,
`NetCoreDbgVersion`) and don't update themselves: change the number and rebuild to update.

- **SharpDbg** is the main one. It has DebuggerDisplay and type proxies, takes lambdas and string interpolation in
  watches, and runs the program in the IDE's terminal so a console program can read input. It comes from NuGet with
  the normal restore and is copied to `sharpdbg/` beside the app. It runs on .NET 11, so it can only be used when a
  final (not preview) .NET 11 runtime is installed. (Before 0.1.20 it failed Locals in async methods and lambdas,
  "Object is in a zombie state"; if that comes back, stop inside an async method and check Locals.)
- **netcoredbg** is the fallback: it runs anywhere, but a console program can't read input. The Windows build is
  committed in `Projects/netcoredbg/`. On Linux and macOS the first build downloads the official release into `obj/` (set `NetCoreDbgDownload=false` to stop that and
  put your own copy in `Projects/netcoredbg/`).

Settings > Debugging > Debugger picks one. Automatic, the default, uses SharpDbg when it can run and netcoredbg
otherwise. If a stop can't read its locals, the IDE still shows the stop, the call stack and the source line.

To try SharpDbg on a preview runtime, start the IDE with `AXAMLIDE_SHARPDBG_FX` set to that runtime's exact version
(as `dotnet --list-runtimes` shows it), for example
`AXAMLIDE_SHARPDBG_FX=11.0.0-rc.3.26522.148 ./AXamlIDE`. Remove it once the final runtime is installed: it pins
SharpDbg to that one runtime, and it won't start once it's gone.

## Build and run

```
dotnet build
dotnet run
```

Tests: `dotnet test Projects/AXamlIDE.Tests`

Optional environment variables (executable paths) are listed at the top of `Program.cs`.

The UI tools (`ui_open`...) run the app on its own Avalonia version, so they need the Avalonia.Headless package in
that same version. They take it from the NuGet cache (`~/.nuget/packages`) when a restore has put it there; otherwise
they download it once from nuget.org into the user's local application data (`AXamlIDE.Packages`).

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
| `Projects/AXamlIDE.RoslynExtension` | Our code inside the Roslyn language server (it loads the assembly): solution-wide analysis |
| `Projects/AXamlIDE.UiHost` | The `ui_*` tools' host: runs a view of the user's app headless (on the app's own Avalonia), out of process |
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

**UI host**

- [Avalonia](https://www.nuget.org/packages/Avalonia) (compiled against only; it runs on the app's own), and [Avalonia.Headless](https://www.nuget.org/packages/Avalonia.Headless) in the app's version, loaded at run time

**Tests**

- [xunit](https://www.nuget.org/packages/xunit), [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk)

**Included source**

- AXAML completion engine: forked from the [Avalonia Visual Studio extension](https://github.com/AvaloniaUI/AvaloniaVS) (MIT), in `Projects/AXamlIDE.Core/CompletionEngine/`
- AXAML formatter: the core of [XAML Styler](https://github.com/Xavalon/XamlStyler) (Apache-2.0), in `Projects/AXamlIDE.Core/XamlStyler/`

**External tools**

- [roslyn-language-server](https://www.nuget.org/packages/roslyn-language-server): C# language server
- [netcoredbg](https://github.com/Samsung/netcoredbg): .NET debugger
- [SharpDbg](https://github.com/MattParkerDev/SharpDbg): .NET debugger, the main one (needs .NET 11; netcoredbg is the fallback)

## Licence

AXamlIDE is under the [MIT licence](LICENSE). The third-party code, font and debugger it includes keep their own
licences: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
