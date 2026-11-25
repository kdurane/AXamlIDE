# Third-party notices

AXamlIDE's own code is under the MIT licence (see [LICENSE](LICENSE)). It also contains, or ships with, the
third-party code and assets below, each under its own licence.

## Included in this repository

| What | Where | Licence | Licence text |
|---|---|---|---|
| AXAML completion engine, forked from the Avalonia Visual Studio extension (AvaloniaVS), © 2014 Steven Kirk and contributors | `Projects/AXamlIDE.Core/CompletionEngine/` | MIT | [LICENSE.md](Projects/AXamlIDE.Core/CompletionEngine/LICENSE.md) |
| XAML Styler formatting engine (Xavalon/XamlStyler), © Xavalon. Modified: see [UPSTREAM.md](Projects/AXamlIDE.Core/XamlStyler/UPSTREAM.md) | `Projects/AXamlIDE.Core/XamlStyler/` | Apache-2.0 | [LICENSE.txt](Projects/AXamlIDE.Core/XamlStyler/LICENSE.txt) |
| Material Design Icons (the icon paths), © Pictogrammers | `Styles/IconResources.axaml` | Apache-2.0 | [pictogrammers.com/docs/general/license](https://pictogrammers.com/docs/general/license/) |
| JetBrains Mono font, © 2020 The JetBrains Mono Project Authors | `Assets/Fonts/` | SIL Open Font License 1.1 | [JetBrainsMono-OFL.txt](Assets/Fonts/JetBrainsMono-OFL.txt) |
| netcoredbg debugger (Windows build), © 2017 Samsung Electronics Co., Ltd. Includes Microsoft's dbgshim and Roslyn assemblies (MIT, .NET Foundation) | `Projects/netcoredbg/` | MIT | [LICENSE](Projects/netcoredbg/LICENSE) |

On Linux and macOS the build downloads the official netcoredbg release instead; it carries the same licence.

## NuGet packages

Restored at build time and shipped in the app's output. The licence each package declares:

| Package | Licence |
|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Controls.ColorPicker, Avalonia.Controls.DataGrid | MIT |
| Avalonia.AvaloniaEdit, AvaloniaEdit.TextMate | MIT |
| TextMateSharp, TextMateSharp.Grammars | MIT |
| Dock.Avalonia, Dock.Avalonia.Themes.Fluent, Dock.Model.Mvvm | MIT |
| Xaml.Behaviors.Avalonia | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.DependencyInjection | MIT |
| SkiaSharp.NativeAssets.Linux.NoDependencies | MIT |
| ClosedXML | MIT |
| Iciclecreek.Avalonia.Terminal, XTerm.NET, Porta.Pty (the Terminal panel), with Wcwidth, Unicode.net, SkiaSharp.HarfBuzz and Microsoft.Windows.Console.ConPTY | MIT |
| Markdig | BSD-2-Clause |
| dnlib | MIT |
| ICSharpCode.Decompiler | MIT |
| Microsoft.Diagnostics.Runtime | MIT |
| DiffPlex | Apache-2.0 |
| Irony | MIT |
| Microsoft.Build, Microsoft.Build.Locator | MIT |
| SharpDbg.Cli (the SharpDbg debugger, © 2026 Matthew Parker, [github.com/MattParkerDev/SharpDbg](https://github.com/MattParkerDev/SharpDbg)), copied to `sharpdbg/`. It bundles ICorDebugSharp, Microsoft's dbgshim, Roslyn's expression evaluator, Microsoft.Diagnostics.NETCore.Client, Microsoft.DiaSymReader, ICSharpCode.Decompiler, ZLinq, AsyncLock and Ardalis.GuardClauses (all MIT), and Debug Adapter Protocol types generated from Microsoft's schema (code MIT, documentation CC-BY) | MIT |
| xunit, xunit.runner.visualstudio (tests only, not shipped) | Apache-2.0 |
| Microsoft.NET.Test.Sdk (tests only, not shipped) | MIT |

## Used but not shipped

- [roslyn-language-server](https://www.nuget.org/packages/roslyn-language-server) (MIT): installed separately as a .NET tool.
- `git` (GPL-2.0): the system's own copy, run as a separate program.
