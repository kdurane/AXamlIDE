# Upstream

This folder is a copy of the core formatting engine from XAML Styler
(https://github.com/Xavalon/XamlStyler, `src/XamlStyler`, Apache-2.0, see LICENSE.txt), the formatter
behind the Visual Studio and Rider "XAML Styler" extensions. Only the UI-free core is here; the VS/Rider
extensions and the `xstyler` console tool aren't.

- Imported: 2026-11-15 from upstream commit `45e26437bd4c0a33c192fe4747ec61b18fe518da` (2025-10-20,
  merge of PR #544; the latest release at the time was v3.2504.1).
- Entry point: `new StylerService(IStylerOptions, XamlLanguageOptions).StyleDocument(string)`.
  `new StylerOptions(config: path)` loads a `Settings.XamlStyler` file, and falls back to
  `Options/DefaultSettings.json` (embedded) when the file is missing or invalid.
- Local changes since the import (Apache-2.0 asks that modified files say so; this list is that notice):
  - Namespaces renamed `Xavalon.XamlStyler.*` -> `AXamlIDE.XamlStyler.*`. The default-settings resource name
    follows (`AXamlIDE.XamlStyler.Options.DefaultSettings.json`, set by `LogicalName` in the csproj).
  - Every file starts with `#nullable disable` (upstream predates nullable reference types; Core has them on).
  - `Options/StylerOptions.cs`: Newtonsoft.Json replaced with System.Text.Json. `[JsonProperty(...,
    IgnoreAndPopulate)]` became `[JsonPropertyName]`; `Deserialize` reproduces IgnoreAndPopulate (missing
    settings take their `[DefaultValue]`) and Newtonsoft's leniency (any-case names, comments, trailing commas,
    quoted numbers, enum names). `Clone()` uses MemberwiseClone instead of a JSON round trip.
  - Not copied: the strong-name key (`Key.snk`), `Settings.StyleCop`, the csproj. The Irony package
    (markup-extension parser) is referenced from AXamlIDE.Core.csproj.
- Syncing with upstream: diff upstream `src/XamlStyler` against the commit above, reapply the list above, and
  update the commit hash here.
