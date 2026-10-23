# Roslyn LSP: guides for the remaining features

Written 2026-10-23, against roslyn-language-server 5.12. Every response shape quoted here came from the real server
(the probe is described at the end), so you can trust the JSON.

Recommended order, easiest first. Each one builds on the one before it:

| # | Feature | What it is in VS / VS Code / Rider | Size |
|---|---------|------------------------------------|------|
| 1 | Format Selection | Ctrl+K, Ctrl+F | small |
| 2 | Go to Implementation / Type Definition | Ctrl+F12 | small |
| 3 | Prepare Rename | rename refuses keywords, picks the right word | small |
| 4 | Highlight usages | all uses of the symbol at the caret, writes in a stronger colour | medium |
| 5 | Expand / Shrink Selection | Shift+Alt+= / Shift+Alt+- (VS), Shift+Alt+→ / ← (VS Code) | medium |
| 6 | Folding from the server | #region, usings, comments, members fold properly | medium |
| 7 | Settings for server options | mostly done; the analysis scope is left | small |
| 8 | Whole-solution errors | Problems lists errors in files that aren't open | bigger |
| 9 | `///` doc comments, format as you type | `///` expands to `<summary>`; `}` and `;` tidy the line | medium |
| 10 | Call Hierarchy / Type Hierarchy | a tool window of callers / base and derived types | bigger |
| 11 | Restore when Roslyn asks | unrestored project: restore it, then the errors go away | small |

---

## 0. The recipe (read this once)

Almost every feature below is the same three layers. Use Go to Definition as the example to copy.

**Core (Projects/AXamlIDE.Core/Services/Lsp)** — no UI, so it can be unit tested.

1. *Is it supported?* In `LspClient.cs`, `InitializeAsync` reads the server's capabilities. Add a property next to
   `SupportsDefinition`, and set it with the existing helper:
   ```csharp
   public bool SupportsImplementation { get; private set; }
   ...
   SupportsImplementation = HasProvider(caps, "implementationProvider");
   ```
   Roslyn announces all of the ones in this guide up front (nothing dynamic), so that's all you need.
2. *The request.* A method in `LspClient.Requests.cs`, copying the closest existing one. `RequestAsync(method, params, ct)`
   returns the `result` as a `JsonElement` (already cloned, safe to keep).
3. *The shape.* A record in `LspTypes.cs` if nothing existing fits, and a `Parse...` method in `LspClient.Parsing.cs`.
   Make the parse method `internal static` so the tests can call it.
4. *A test* in `Projects/AXamlIDE.Tests` with the JSON from this guide pasted in (see `RoslynCompletionTests.cs`).

**App (Editor/...)** — a method on an existing controller, or a new controller made in `EditorPaneView.Initialize`.
Always:
- get the server with `ctx.Languages.ConnectionFor(document)` and call `connection.Sync.Flush()` before asking (so the
  server has the text that's on screen);
- convert positions with `document.OffsetOf(line, character)` (LSP → offset) and
  `document.Document.GetLocation(offset)` (offset → LSP, remember `Line - 1` and `Column - 1`);
- report failures with `ctx.Status(...)`, never a dialog.

**The command** (button + menu + key, one command — see `ide-conventions`):

1. `Messages/EditorActionMessage.cs`: add a value to `EditorAction`.
2. `Commands/EditorCommands.cs`: `public IRelayCommand GoToImplementation { get; } = Make(messenger, EditorAction.GoToImplementation);`
3. `Projects/AXamlIDE.Core/Services/Keymaps/CommandCatalog.cs`: `new("Navigate.GoToImplementation", "Go to Implementation", "…", Editor),`
   (it then shows in Settings > Keyboard).
4. `Shell/KeyMap.cs`: `["Navigate.GoToImplementation"] = (editor.GoToImplementation, null),`
5. The keys, in `visual-studio.json` and `vs-code.json` next to it (chords are written `"Ctrl+K, Ctrl+F"`).
6. The menus: `Views/MainWindow.axaml` (the Navigate or Edit menu) and the editor's context menu in
   `Views/EditorPaneView.axaml` (`shell:KeyMap.CommandId="..."` makes it show the key).
7. `Views/EditorPaneView.axaml.cs`, `Receive(EditorActionMessage)`: a `case` that calls your controller.

**Trying it:** open any C# project in the IDE. Or reuse the headless harness (see "How this was checked" at the end).

---

## 1. Format Selection (`textDocument/rangeFormatting`)

VS and VS Code: Edit > Advanced > Format Selection, Ctrl+K, Ctrl+F. With nothing selected, both format the current line.

**Roslyn:** `"documentRangeFormattingProvider": true`. Request and reply are exactly Format Document's plus a `range`:
```json
→ { "textDocument": { "uri": "…" }, "range": { "start": {…}, "end": {…} }, "options": { "tabSize": 4, "insertSpaces": true } }
← [ { "range": {…}, "newText": "…" } ]     (empty when it's already tidy)
```

**Steps**
1. Core: `SupportsRangeFormatting` (see the recipe, `"documentRangeFormattingProvider"`).
2. Core: `RangeFormattingAsync(string uri, LspRange range, int tabSize, bool insertSpaces, CancellationToken ct)`.
   Copy `FormattingAsync`. The edit-list parsing at the bottom of `FormattingAsync` is now needed twice: move it into a
   `private static IReadOnlyList<LspTextEdit> ParseTextEdits(JsonElement result)` and call it from both.
3. App: `LanguageSessionService.FormatDocumentAsync` already does the rest (flush, ask, `ApplyTextEdits`, status). Add
   `FormatRangeAsync(OpenDocument document, LspRange range)` beside it, sharing as much as you can. A neat way: make
   `FormatDocumentAsync`'s body a private method that takes a `Func<LspClient, Task<IReadOnlyList<LspTextEdit>>>`.
4. The command: `Edit.FormatSelection` ("Format Selection"), key `"Ctrl+K, Ctrl+F"` in both keymaps, Edit menu and context
   menu under Format Document. In `EditorPaneView`, the range is the selection
   (`Editor.SelectionStart`, `Editor.SelectionLength`), or the caret's whole line when nothing is selected.

**Watch out:** Roslyn returns `[]` for code that's already formatted, so test on something messy
(`if(x){y();}` on one line).

---

## 2. Go to Implementation, Go to Type Definition

- **Go to Implementation** (VS and VS Code: Ctrl+F12): on an interface or abstract member, the classes that implement it.
- **Go to Type Definition** (VS Code: right-click > Go to Type Definition; no default key in VS or VS Code): on a
  variable, the declaration of *its type* (on `shape` in `IShape shape = …`, go to `IShape`).

**Roslyn:** `"implementationProvider": true`, `"typeDefinitionProvider": true`. Same request and reply as Go to Definition:
```json
→ { "textDocument": { "uri": "…" }, "position": { "line": 18, "character": 15 } }
← [ { "uri": "file:///…/Program.cs", "range": { "start": { "line": 5, "character": 17 }, "end": { "line": 5, "character": 23 } } } ]
```

**Steps**
1. Core: `SupportsImplementation`, `SupportsTypeDefinition`; `ImplementationAsync` and `TypeDefinitionAsync`, both copies of
   `DefinitionAsync` with a different method name (`ParseLocations` handles the reply already).
2. App: in `Editor/Navigation/NavigationCommands.cs`, `GoToImplementationAsync()` and `GoToTypeDefinitionAsync()`, copied
   from `GoToDefinitionAsync` *without* the AXAML branch at the top. `NavigateToLocationsAsync` already shows a picker
   when there are several results, which is exactly what Go to Implementation needs.
   - Three nearly identical methods is a hint: once both work, try one private
     `GoToLocationsAsync(string title, Func<LspClient, bool> supported, Func<LspClient, string, int, int, Task<IReadOnlyList<LspLocation>>> ask)`
     and make the three public methods one-liners.
3. Commands: `Navigate.GoToImplementation` (`"Ctrl+F12"` in both keymaps), `Navigate.GoToTypeDefinition` (no key: leave it out
   of the JSON, Settings > Keyboard can bind one). Navigate menu under Go to Definition, and the context menu.

---

## 3. Prepare Rename (`textDocument/prepareRename`)

Before asking for the new name, ask the server whether there's something renamable at the caret, and which text it is.
Today the prompt is filled with `IdentifierAtCaret`. That guesses wrong on keywords, `@class`, or with the caret just
after a name, and the user only finds out after typing the new name.

**Roslyn:** `"renameProvider": { "prepareProvider": true }`. It only uses prepare if the client says it can:
```json
→ { "textDocument": { "uri": "…" }, "position": { "line": 21, "character": 9 } }
← { "start": { "line": 21, "character": 8 }, "end": { "line": 21, "character": 14 } }      on a name
← null                                                                                        on a keyword
```
(The LSP spec also allows `{ "range": …, "placeholder": "…" }` and `{ "defaultBehavior": true }`; handle those too, it's
two more lines.)

**Steps**
1. Core, `InitializeAsync`'s capabilities: add `rename = new { prepareSupport = true },` under `textDocument`.
2. Core: `SupportsPrepareRename`: `renameProvider` is an object with `prepareProvider: true` (look at how
   `_staticCodeActionResolve` reads `resolveProvider` for the pattern).
3. Core: `Task<LspRange?> PrepareRenameAsync(uri, line, character, ct)`. A null result means "nothing to rename here".
   Note the reply can also be an LSP *error* rather than null ("You cannot rename this element"): catch `LspException`
   and treat it as null too.
4. App, `NavigationCommands.RenameSymbolAsync`: before the prompt, if supported, call it.
   - null: `ctx.Status("You can't rename this.")` and return (VS Code's wording is "The element can't be renamed.").
   - a range: use `document.Document.GetText(...)` of that range as `current` instead of `IdentifierAtCaret`.
5. Test (Core): the three reply shapes, plus null.

---

## 4. Highlight usages (`textDocument/documentHighlight`)

Put the caret on a name, and every use of *that symbol* in the file is highlighted (not just the same text): reads in
one colour, writes (assignments) in a stronger one. VS, VS Code and Rider all do this, after a short pause. Today's
`OccurrenceController` only matches selected text, which is a different feature; keep both.

**Roslyn:** `"documentHighlightProvider": true`.
```json
→ { "textDocument": { "uri": "…" }, "position": { "line": 19, "character": 13 } }
← [ { "range": { "start": { "line": 19, "character": 12 }, "end": { "line": 19, "character": 16 } }, "kind": 1 },
    { "range": { … line 20 … }, "kind": 2 },
    { "range": { … line 21 … }, "kind": 2 } ]
```
`kind`: 1 = text, 2 = read, 3 = write. (Here the declaration came back as 1 and the uses as 2.)

**Steps**
1. Core: record `LspDocumentHighlight(LspRange Range, int Kind)`, `SupportsDocumentHighlight`,
   `DocumentHighlightsAsync(uri, line, character, ct)`, parse plus a test.
2. Theme colours: VS Code's keys are `editor.wordHighlightBackground` (already used, see `WordHighlightBrush` in
   `Styles/ThemeResources.axaml`) and `editor.wordHighlightStrongBackground` for writes. Add the second key to every
   `Styles/Themes/*.axaml` beside the first, and a `WordHighlightStrongBrush` in ThemeResources. (That's how
   `editorInlayHint.*` was just added: look at the diff of any theme file.)
3. App: `Editor/Highlighting/SymbolHighlightController.cs` plus a renderer, modelled on `OccurrenceController` and
   `OccurrenceRenderer` (an `IBackgroundRenderer` drawing rectangles under segments).
   - Listen to `ctx.Editor.TextArea.Caret.PositionChanged`. Restart a 250 ms `DispatcherTimer` on each move, and only
     ask when it fires: holding an arrow key must not send a request per character. `FoldingController` has the timer
     pattern.
   - Cancel the previous request (`CancellationTokenSource`, as `CodeLensController.FetchAsync` does). After the await,
     drop the answer if the caret has moved or the document changed.
   - Skip it while there's a selection (the selected-text highlight is showing then), and clear on `ctx.TextChanged`
     and `ctx.Detaching`.
   - Turn the ranges into `TextSegment`s with `OffsetOf`, then `textView.InvalidateLayer(KnownLayer.Selection)`.
4. Optional: tell `OverviewRuler` about them too (it already takes the selected-text occurrences).

---

## 5. Expand / Shrink Selection (`textDocument/selectionRange`)

Each press selects the next bigger piece of syntax: the name, then `shape.Area()`, the statement, the block, the
method, the class. Shrink goes back. VS: Shift+Alt+= / Shift+Alt+-. VS Code: Shift+Alt+→ / Shift+Alt+←.

**Roslyn:** `"selectionRangeProvider": true`. You send positions, and get back, for each one, the innermost range with a
chain of `parent`s:
```json
→ { "textDocument": { "uri": "…" }, "positions": [ { "line": 19, "character": 26 } ] }
← [ { "range": { 19:25–19:29 }, "parent": { "range": { 19:19–19:29 }, "parent": { "range": { 19:19–19:31 }, "parent": … } } } ]
```
(Written short. Each `range` is a normal `{ start, end }`. The chain went word → `shape.Area` → `shape.Area()` →
`= shape.Area()` → … → the method → the class → the whole file.)

**Steps**
1. Core: `Task<IReadOnlyList<LspRange>> SelectionRangesAsync(uri, line, character, ct)`: follow `parent` until there isn't
   one and return the list innermost-first. Test it with the JSON above. (A `while` loop over `parent`, not recursion,
   reads better.)
2. App: `Editor/SelectionExpander.cs` (next to `WhitespaceSelectionController.cs`) keeping a `Stack<(int Start, int Length)>`
   of earlier selections:
   - **Expand:** ask at the caret (or at the selection's start), take the first range that is *strictly bigger* than the
     current selection, push the current selection, select it (`Editor.Select(start, length)`).
   - **Shrink:** pop and select the previous one.
   - Forget the stack when the user changes the selection some other way: remember the selection you last set, and
     compare it in `TextArea.SelectionChanged`.
3. Commands: `Edit.ExpandSelection` / `Edit.ShrinkSelection`. VS keymap `"Shift+Alt+="` / `"Shift+Alt+-"`, VS Code
   keymap `"Shift+Alt+Right"` / `"Shift+Alt+Left"`. Check the keymap parser accepts `=`, `-` and `Right` as key names
   (look at how `Ctrl+/` is parsed in `Core/Services/Keymaps`).

---

## 6. Folding from the server (`textDocument/foldingRange`)

`CSharpFoldingStrategy` scans braces itself. The server knows the real structure: `#region` (folded showing the
region's name), the usings block, multi-line comments, and members.

**Roslyn:** `"foldingRangeProvider": true`.
```json
→ { "textDocument": { "uri": "…" } }
← [ { "startLine": 16, "startCharacter": 29, "endLine": 22, "endCharacter": 5, "kind": "implementation", "collapsedText": "..." },
    { "startLine": 15, "startCharacter": 4,  "endLine": 23, "endCharacter": 14, "kind": "region", "collapsedText": "Entry" },
    { "startLine": 0,  "startCharacter": 6,  "endLine": 1,  "endCharacter": 33, "kind": "imports", "collapsedText": "..." } ]
```

**Steps**
1. Core: record `LspFoldingRange(int StartLine, int? StartCharacter, int EndLine, int? EndCharacter, string? Kind, string? CollapsedText)`,
   `SupportsFoldingRanges`, `FoldingRangesAsync(uri, ct)`, parse plus a test.
2. App, `Editor/Folding/FoldingController.Update`: for a `.cs` document with a connection that supports it, ask the
   server, then build AvaloniaEdit `NewFolding`s:
   - start = `OffsetOf(StartLine, StartCharacter ?? end of that line)`, end = `OffsetOf(EndLine, EndCharacter ?? end of that line)`;
   - `Name = CollapsedText` (what's shown when folded: `Entry` for the region, `...` otherwise);
   - **sort by start offset**: `FoldingManager.UpdateFoldings` requires it.
3. `Update` is called from a timer, and the request is async. Make it `async void` *only* at the timer tick (or make
   `Update` return a `Task` and discard it there). After the await, check that the document is still `ctx.Active` and
   its `Version` is unchanged, as `VisibleLinesFetcher.FetchAsync` does.
4. Keep `CSharpFoldingStrategy` as the fallback: while the server is still loading, or when it isn't installed.
5. Optional: Rider folds usings when a file opens. With `kind == "imports"`, set `DefaultClosed = true`, but only on
   the first update for a document, or every edit would close them again.

---

## 7. Settings for server options (mostly done)

Done on 2026-10-23: `EditorFeatureSettings` (Core/Models) holds the switch-off-able helpers, the Settings window has
Text Editor / IntelliSense / Inline Hints / Debugging pages for them, and the ones that are Roslyn options
(inlay hints, unimported completion, code lens) reach Roslyn through `RoslynSettings.Get(section, features)`. A change
sends `workspace/didChangeConfiguration`, Roslyn asks for its options again, and `LspClient.ConfigurationAnswered`
makes the inlay hints refetch (Roslyn doesn't send its own refresh for that).

**Adding a setting now** is: a property on `EditorFeatureSettings` (with its default), a property on
`SettingsViewModel` (copy any of the `SetFeatures` ones), a checkbox + `SettingHint` line in `SettingsWindow.axaml`,
and the code that reads `ctx.Settings.Features.X` (react to `ctx.FeaturesChanged` if it shows something). Saving needs
nothing: the whole record is stored, and a feature missing from an older settings file keeps its default.

**Left for guide 8:** the analysis scope, `background_analysis.dotnet_analyzer_diagnostics_scope` and
`…compiler_diagnostics_scope` (`"openFiles"`, `"fullSolution"` or `"none"`). It isn't a bool, so add it as an enum on
`EditorSettings` (not the features record: it's about errors, which always show), a ComboBox
("Open documents" / "Entire solution"), and an arm in `RoslynSettings.Get`'s switch.

## 8. Whole-solution errors in Problems (`workspace/diagnostic`)

Problems only lists diagnostics for open documents, because those are the only ones pulled. VS's Error List (with
"Entire Solution" analysis) and Rider's Solution-Wide Analysis list every file.

It costs CPU: Roslyn analyses everything in the background. So make it the analysis-scope option from guide 7, off by
default, as Rider and VS do.

**Roslyn:** with the scopes set to `fullSolution`, it registers several diagnostic sources dynamically. Some have
`"workspaceDiagnostics": true` (from the log: `enc`, `HotReloadDiagnostics`, `WorkspaceDocumentsAndProject`). For each
of those identifiers:
```json
→ workspace/diagnostic  { "identifier": "WorkspaceDocumentsAndProject", "previousResultIds": [] }
← { "items": [ { "uri": "file:///…/Program.cs", "kind": "full", "resultId": "…", "items": [ …diagnostics… ] },
               { "uri": "…", "kind": "unchanged", "resultId": "…" } ] }
```
Send the `resultId`s you got last time back in `previousResultIds` (`[{ "uri": …, "value": resultId }]`), and files
that haven't changed come back as `"unchanged"`. Be aware that a call with `previousResultIds` can stay open until
something changes: that's how the server pushes updates. Always have exactly one in flight per identifier, with a
`CancellationToken` you cancel on shutdown or workspace change.

**Steps**
1. Core, `LspClient.RegisterCapabilities`: remember which identifiers have `workspaceDiagnostics: true` (it reads
   `identifier` already) in a second list.
2. Core: `WorkspaceDiagnosticsAsync(string identifier, IReadOnlyDictionary<string, string> previousResultIds, ct)`
   returning `(Uri, Kind, ResultId, IReadOnlyList<LspDiagnostic>)` items. `ParseDiagnostic` already exists.
3. Core: a `WorkspaceDiagnostics` class (path → diagnostics, plus the result ids), with tests. It's plain data, so it
   belongs in Core.
4. App, `LanguageSessionService`: after `ProjectInitializationComplete`, and on `workspace/diagnostic/refresh`, run a
   loop per identifier that asks, stores, sends a message (a new `WorkspaceDiagnosticsChangedMessage`) and asks again.
5. App, `ProblemsViewModel.Rebuild`: add the workspace diagnostics for files that *aren't* open. An open document's
   own pulled diagnostics are fresher, so those win.

This is the biggest data-plumbing job here. Do 7 first and ask me to review the loop before wiring the UI.

---

## 9. `///` doc comments and formatting as you type

### 9a. `///` → `<summary>` (`textDocument/_vs_onAutoInsert`)

In VS, Rider and VS Code, typing the third `/` above a member produces
```csharp
/// <summary>
/// |
/// </summary>
```
(parameters get `<param>` lines too).

**Roslyn** (a Visual Studio extension of LSP, hence the `_vs_` names): the capability is
`"_vs_onAutoInsertProvider": { "_vs_triggerCharacters": ["'", "/", "\n", "\""] }`. The position is *after* the typed
character:
```json
→ textDocument/_vs_onAutoInsert
  { "_vs_textDocument": { "uri": "…" }, "_vs_position": { "line": 27, "character": 7 }, "_vs_ch": "/",
    "_vs_options": { "tabSize": 4, "insertSpaces": true } }
← { "_vs_textEditFormat": 2,
    "_vs_textEdit": { "range": { 27:7–27:7 }, "newText": " <summary>\n    /// $0\n    /// </summary>" } }
← null    when there's nothing to insert
```
`_vs_textEditFormat`: 1 = plain text, 2 = snippet (`$0` is where the caret goes).

**Steps**
1. Core: read `_vs_onAutoInsertProvider._vs_triggerCharacters` into `AutoInsertTriggerCharacters` (like
   `CompletionTriggerCharacters`). `Task<(LspTextEdit Edit, bool IsSnippet)?> OnAutoInsertAsync(uri, line, character, string ch, ct)`.
   Test with the JSON above.
2. App, `Editor/TypingController.OnTextEntered`: for C#, when `e.Text == "/"` and the server lists it: flush, ask,
   then apply. Applying is the same job as Roslyn's override completion, which is already written:
   `ApplyComplexEdit` in `Editor/CodeCompletion/LspCompletionData.cs`. Move it into a small shared helper
   (`Editor/LspEdits.cs`?) that both use, instead of copying it.
3. Don't run it while the completion list is open, and check the caret hasn't moved before applying.
4. Later, Enter inside a doc comment: Roslyn continues it with `/// ` (that's the `"\n"` trigger). Enter doesn't raise
   `TextEntered` in AvaloniaEdit (the newline comes from its Enter command), so this needs a `KeyUp` for `Key.Enter`,
   or a look at how `IndentationController` hooks Enter. Leave it for a second pass.

### 9b. Format as you type (`textDocument/onTypeFormatting`)

VS tidies a statement when you type `;` and a block when you type `}` (Tools > Options > Text Editor > C# > Formatting >
"Automatically format statement on ;" / "…block on }").

**Roslyn:** `"documentOnTypeFormattingProvider": { "firstTriggerCharacter": "}", "moreTriggerCharacter": [";", "\n"] }`.
```json
→ { "textDocument": { "uri": "…" }, "position": { …after the typed character… }, "ch": ";", "options": { "tabSize": 4, "insertSpaces": true } }
← [ { "range": {…}, "newText": "…" } ]      ([] when the line is already tidy)
```

**Steps**
1. Core: read the trigger characters; `OnTypeFormattingAsync(uri, line, character, ch, tabSize, insertSpaces, ct)` (reuse
   `ParseTextEdits` from guide 1).
2. App: `TypingController.OnTextEntered`, for `;` and `}` in C#: ask, then apply with `LanguageSessionService`'s
   `ApplyTextEdits` (make it internal or public). The caret moves along with the edits on its own.
3. Careful with `;`: `TryMakeNamespaceFileScoped` also runs on `;`. Do the namespace check first, and skip the formatting
   when it changed something.
4. A setting "Format on type" (`EditorFeatureSettings`, see guide 7), on by default like VS.

---

## 10. Call Hierarchy and Type Hierarchy

A tool window (VS: View Call Hierarchy, Ctrl+K, Ctrl+T. VS Code: Shift+Alt+H) with a tree. Callers of the method at the
caret, each expandable to *its* callers. Type Hierarchy shows base types and derived types.

**Roslyn:** `"callHierarchyProvider": true`, `"typeHierarchyProvider": true`. Three requests each. "Prepare" turns
the caret into an item, then you ask about the item:
```json
→ textDocument/prepareCallHierarchy  { "textDocument": {…}, "position": {…} }
← [ { "name": "Program.Helper(double)", "kind": 6, "detail": "Sample.Program", "uri": "…",
      "range": {…}, "selectionRange": {…}, "data": { … Roslyn's, send it back untouched … } } ]

→ callHierarchy/incomingCalls  { "item": <that whole item object> }
← [ { "from": { …an item, same shape… }, "fromRanges": [ { …where in "from" the call is… } ] } ]

→ callHierarchy/outgoingCalls  { "item": … }
← [ { "to": { …item… }, "fromRanges": [ … ] } ]

→ textDocument/prepareTypeHierarchy, typeHierarchy/supertypes, typeHierarchy/subtypes   (same idea; replies are item lists)
```
`kind` is an LSP SymbolKind (5 class, 6 method, 11 interface, …). `document symbols` already use the same numbers.

**Steps**
1. Core: record `LspHierarchyItem(string Name, int Kind, string? Detail, string Uri, LspRange Range, LspRange SelectionRange, JsonElement Raw)`.
   Always send `Raw` back as `item` (like `ResolveCodeLensAsync` sends `lens.Raw`). Requests:
   `PrepareCallHierarchyAsync`, `IncomingCallsAsync(item)` → list of `(LspHierarchyItem From, IReadOnlyList<LspRange> Ranges)`,
   `OutgoingCallsAsync`, and the three type ones. Tests with the JSON above.
2. App: a tool window like Problems: a `ToolTab` value, a `ViewModels/HierarchyViewModel.cs` and
   `Views/HierarchyView.axaml` with a `TreeView`. (You have uncommitted changes in `DockLayoutViewModel.cs`; commit
   those first so this diff is easy to read.)
3. Tree nodes load their children when expanded (bind `IsExpanded` and load on the first `true`), not all at once:
   call trees are deep. Show a "Loading…" child until then, as the explorer does.
4. Double-click or Enter on a node: `NavigationService.ToLocation(new LspLocation(item.Uri, item.SelectionRange))`.
5. A toggle at the top: Callers / Callees (or Base types / Derived types).
6. Commands: `View.CallHierarchy` (`"Ctrl+K, Ctrl+T"` in VS, `"Shift+Alt+H"` in VS Code), `View.TypeHierarchy` (no default key).
   Context menu and View menu.

---

## 11. Restore when Roslyn asks (`workspace/_roslyn_projectNeedsRestore`)

When a project hasn't been restored (fresh clone, a package just added outside the IDE), Roslyn loads it with errors on
every package type. Then it asks the client to restore. VS Code restores and the errors go away. Today the IDE answers
`null` without doing anything.

**Roslyn:** a *server-to-client request*, so it goes in `LspClient.HandleServerRequestAsync` next to `workspace/applyEdit`.
Its parameters list the projects:
```json
{ "projectFilePaths": [ "/…/MyApp/MyApp.csproj" ] }
```
(That's the VS Code extension's name for the field. Check it first: put a `Log(p.GetRawText())` in the new `case` and
delete a project's `obj/project.assets.json` to trigger it.) Answer only once the restore has finished. Roslyn then
reloads the projects by itself.

**Steps**
1. Core, `LspClient`: `public Func<IReadOnlyList<string>, Task>? RestoreHandler { get; set; }` (like
   `WorkspaceEditHandler`), a `case "workspace/_roslyn_projectNeedsRestore":` that reads the paths and awaits the handler.
2. Core, `LspManager.StartAsync`: pass it through, as `WorkspaceEditHandler` is.
3. App, `LanguageSessionService`: the handler runs `NuGetCli.RestoreAsync(project, ct)` for each path, one at a time,
   with "Restoring MyApp…" / "Restored MyApp." in the status bar.
4. `projects.dotnet_enable_automatic_restore` (a Roslyn option, on by default) turns this off. Make it a setting (guide 7)
   "Restore projects automatically".

---

## Not on the list, and why

- **Rename/move file → update namespaces** (`workspace/willRenameFiles`): Roslyn 5.12 doesn't offer it (its
  `workspace` capabilities are empty), so keep your own namespace-on-move code.
- **Run / Debug Test code lenses:** Roslyn can show them (`code_lens.dotnet_enable_tests_code_lens`), but running tests
  goes through Roslyn's test-runner protocol plus a test results window. That's a feature of its own; ask me when you
  get to it.
- **`snippetSupport = true`:** Roslyn would then add its own `prop`, `ctor`, `for` … snippets to completion, duplicating
  yours. Leave it off.

---

## How this was checked (reuse it)

- **The probe.** A Python script that talks to `roslyn-language-server` directly and prints its replies. It's where
  every JSON sample above came from. It's `/tmp/claude-1000/-home-kieron-RiderProjects-AXamlIDE/22382b97-4f08-46eb-ab50-d0f47e38f047/scratchpad/probe/probe.py`
  (run as `python3 -I probe.py <sample folder> <questions.py>`; `q1.py`…`q4.py` beside it are the questions asked). Use the same approach with your own sample project whenever you're unsure what a request returns.
- **The headless harness.** `/tmp/claude-1000/-home-kieron-RiderProjects-AXamlIDE/22382b97-4f08-46eb-ab50-d0f47e38f047/scratchpad/ui/Program.cs` (sample: `/tmp/claude-1000/-home-kieron-RiderProjects-AXamlIDE/22382b97-4f08-46eb-ab50-d0f47e38f047/scratchpad/uisample/`): the real IDE, headless, against real Roslyn. It opens a
  sample folder, waits for colours and hints, drives completion with keys, and saves screenshots. Copy its
  `Pump`/`WaitFor`/reflection helpers when you want to check one of the features above without clicking through.

Scratchpads are deleted eventually. Copy anything you want to keep.
