# Upstream

This folder is a fork of the XAML completion engine from the Avalonia Visual Studio extension
(AvaloniaVS, `CompletionEngine/Avalonia.Ide.CompletionEngine`, MIT, see LICENSE.md).

- Imported into AXamlIDE: before the first commit (`ffc1b54`, 2026-10-15). The upstream commit/version
  it was taken from wasn't recorded. **When you next sync with upstream, write the commit hash here.**
- Local changes since the fork:
  - Namespaces renamed to `AXamlIDE.CompletionEngine.*`.
  - `ITextChange`, `ITextChangeExtensions`, `INamespaceTrasformation` and `Utils` live here (they used to be in
    the app's `Interfaces/` and `Helpers/` folders).
- Upstream is archived (AvaloniaVS moved to closed tooling), so this copy now diverges freely. Changes since the fork:
  - Completions offer only what's valid where the caret is: child elements that fit the parent (its collection
    item type, [Content] property, property element or setter property type), Setter/selector/TemplateBinding
    properties that are AvaloniaProperties of the right type, attributes not already set, x:Class on the root only.
  - Classes is offered as an attribute (and On.Options); Classes="..." / Classes.x / ".x" in selectors offer the
    style classes used in the project; {StaticResource}/{DynamicResource} offer the project's x:Keys (the old
    hard-coded Avalonia 0.10 theme keys are gone).
  - Nested styles ("^:pointerover") resolve their parent's type; {Binding #name.X} uses the named element's type.
  - Single-quoted attributes and quoted markup extension values parse; parent lookups scan start tags
    (Parsing/XamlTagScanner, Parsing/SelectorScanner) instead of building regexes per keystroke.
  - Fixed: the NuGet folder path, selector completion ranges, nullable enum hints, a crash on generic
    [TemplatePart] types, and record-keyed dictionary lookups. Avalonia 0.9/0.10 workarounds removed.
