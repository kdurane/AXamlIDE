# Upstream

This folder is a fork of the XAML completion engine from the Avalonia Visual Studio extension
(AvaloniaVS, `CompletionEngine/Avalonia.Ide.CompletionEngine`, MIT, see LICENSE.md).

- Imported into AXamlIDE: before the first commit (`ffc1b54`, 2026-10-15). The upstream commit/version
  it was taken from wasn't recorded. **When you next sync with upstream, write the commit hash here.**
- Local changes since the fork:
  - Namespaces renamed to `AXamlIDE.CompletionEngine.*`.
  - `ITextChange`, `ITextChangeExtensions`, `INamespaceTrasformation` and `Utils` live here (they used to be in
    the app's `Interfaces/` and `Helpers/` folders).
  - Otherwise the code is kept close to upstream on purpose (it has its own style: `.editorconfig` leaves it alone),
    so diffs against a newer upstream stay readable.
