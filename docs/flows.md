# Flows

Each section follows one user action through the classes and methods that handle it, in the order they run. Paths
without a folder prefix are in the app project (the repo root). `Core/` stands for `Projects/AXamlIDE.Core/`.
[architecture.md](architecture.md) explains the parts and the four ways they connect (DI, `DocumentManager` events,
`IMessenger` messages, `IDocumentHost` / `OpenDocument.Reveal`).

Line numbers are left out because they go stale. Search for the method name instead.

Contents:

1. [App startup](#1-app-startup)
2. [Open a solution](#2-open-a-solution)
3. [Open a file from the explorer](#3-open-a-file-from-the-explorer)
4. [Type in a C# file: sync and diagnostics](#4-type-in-a-c-file-sync-and-diagnostics)
5. [Completion (C# and AXAML)](#5-completion-c-and-axaml)
6. [Save (Ctrl+S)](#6-save-ctrls)
7. [Go to Definition (F12)](#7-go-to-definition-f12)
8. [Build and Run](#8-build-and-run)
9. [Debug: start, then stop at a breakpoint](#9-debug-start-then-stop-at-a-breakpoint)
10. [AXAML live preview](#10-axaml-live-preview)
11. [Find in files](#11-find-in-files)
12. [Git commit](#12-git-commit)
13. [Close the solution](#13-close-the-solution)
14. [AXAML diagnostics](#14-axaml-diagnostics)
15. [Claude panel: send a message](#15-claude-panel-send-a-message)
16. [Solution-wide analysis](#16-solution-wide-analysis)

---

## 1. App startup

1. `Program.Main` hooks `AppDomain.UnhandledException` (→ `CrashLog.Write`), then `BuildAvaloniaApp()`
   (`AppBuilder.Configure<App>().UsePlatformDetect()`, `ConfigureFonts` to add the fonts in `Assets/Fonts` as
   `fonts:App#...`, `LogToTrace()`) → `StartWithClassicDesktopLifetime(args)`.
2. Avalonia calls `App.Initialize` (`AvaloniaXamlLoader.Load(this)`), then `App.OnFrameworkInitializationCompleted`.
3. `AppServices.Build()` registers every service as a singleton and builds the container (`ValidateOnBuild = true`).
   `ReportUnhandledExceptions` then hooks `Dispatcher.UIThread.UnhandledException`: an exception on the UI thread is
   written to the crash log, reported in the status bar, and marked handled so the IDE carries on.
4. The app resolves `ThemeSwitcher` and calls `.Apply()`. It also resolves `GitTreeDecorator`, `DocumentReloader`,
   `XamlCacheKeeper` and `XamlAnalysisService`, which only need to exist so they start listening.
5. `App.UseDockViews(services)`:
   - adds a `FuncDataTemplate<WelcomeViewModel>` (the empty-documents screen);
   - adds a style that sets `ControlRecyclingDataTemplate.ControlRecycling` on every `DockControl` to the
     `DockViewCache` singleton. Resolving `DockViewCache` creates `EditorServices` and `NavigationService` too, so
     navigation messages are handled from the start.
6. `App.Start(desktop, services)` resolves `MainWindow`. That builds the whole object graph:
   `MainWindow(MainWindowViewModel, SettingsViewModel, KeyMap, FileChangeWatcher)` → `MainWindowViewModel(...)` →
   `DockLayoutViewModel` (which runs `DockFactory.CreateLayout` and `InitLayout`), `WorkspaceViewModel`, `RunViewModel`, and so on.
   - In the `MainWindow` constructor: `docking.Factory.DefaultHostWindowLocator = CreateFloatingWindow` (floating tab
     windows get the key bindings too), `keyMap.AttachTo(this)`, `Closing += Session.Save + CloseFloatingWindows`,
     `Activated += fileWatcher.CheckAll`.
7. `App.Start` then picks what to show, from `SettingsViewModel.Startup`:
   - `LastWorkspace` with a recent path: the main window shows, and in `main.Opened` it sends
     `OpenWorkspaceMessage(last)`, which [flow 2](#2-open-a-solution) continues.
   - `StartWindow`: a `StartWindow { DataContext = StartViewModel }` becomes `desktop.MainWindow` until
     `StartViewModel.Finished` fires (or the window is closed). `HandOver()` then shows the main window.
   - Otherwise the main window shows with nothing open.

## 2. Open a solution

Starting from **File > Open Solution** or the start window's button.

1. `StartViewModel.OpenSolutionCommand` → `OpenSolution()` → `IDialogService.PickSolutionAsync()`
   (`DialogService` → `Owner.StorageProvider.OpenFilePickerAsync`).
2. `StartViewModel.Choose(path)`: raises `Finished` (the start window hands over if it's showing) and sends
   `OpenWorkspaceMessage(path)`.
3. `MainWindowViewModel.Receive(OpenWorkspaceMessage)` → `OpenWorkspaceAsync(path)`. If something is already open,
   `CloseWorkspaceAsync()` runs first ([flow 13](#13-close-the-solution)), and a cancel there stops this flow.
4. `WorkspaceViewModel.OpenAsync(path)`: `_opening` blocks a second open while one is running. A `.sln`/`.slnx` goes to
   `OpenSolutionAsync`. A folder goes to `OpenFolder`.
5. `WorkspaceViewModel.OpenSolutionAsync(path)`:
   1. `WorkspaceModel.ClearProjectCaches()` → `MsBuildHostClient.ClearCache()` (if the host has started).
   2. Sends `StatusMessage("Opening X...")`.
   3. `Task.Run(() => SolutionLoader.LoadSolution(path, _workspace.Host); loaded.Populate())`:
      - `WorkspaceModel.Host` starts the MSBuild host on first use: `new MsBuildHostClient(path)` → `Start()` →
        `Process.Start`.
      - `SolutionLoader.LoadSolution` reads project paths from the `.slnx` (`XDocument.Load`) or the `.sln` (a regex
        over its lines). Then, for each project, `host.GetProjectFacts(path).References` adds referenced projects
        the solution doesn't list.
      - `GetProjectFacts` → `GetProjectData` (cached per project and configuration until the project or one of its
        local imports is written) → `Send(new HostRequest(...))` → `Query`: writes one JSON line to the host's stdin
        and reads one line back.
      - In the host, `Program.cs` → `HandleRequest` → `Project.FromFile(...)` → reads items and properties → JSON
        `HostResponse`.
      - `ProjectTreeNode.CreateSolution(...)` makes a project node for each project, which loads its children lazily
        through `SolutionLoader.LoadProjectItems(host, csproj)`.
   4. Back on the UI thread: `XamlLanguageService.ClearProjectCaches()`, sets `WorkspaceModel.SolutionProjects`,
      puts the root in `Roots` (the explorer tree is bound to it).
   5. `LanguageSessionService.SetWorkspace(new LspWorkspace(folder, SolutionPath: path))`:
      - `LspManager.SetWorkspace` → `Restart()` (bumps `Generation`, disposes old clients), then
        `GetClientAsync(language)` for each language. This starts every configured server in the background:
        `StartAsync` → `LspClient.StartAsync(command, args, root, settings)` → `Process.Start` → `InitializeAsync`
        (the LSP `initialize` request and `initialized` notification).
      - For Roslyn, `LspServerConfig.AfterInitialize` = `LspServers.OpenRoslynWorkspace` sends the `solution/open`
        notification. The status shows "loading solution…" until Roslyn sends
        `workspace/projectInitializationComplete`.
      - `ReattachDocuments()`: any documents already open are re-sent to the new servers.
   6. `Opened` fires. Its listeners:
      - `WorkspaceSession.OnWorkspaceOpened` → `Save()` the previous session, then `Restore(folder)`:
        `WorkspaceStateStore.Load` → `DockLayoutViewModel.ApplyLayout` → `RunViewModel.RestoreStartup` → reopens each
        remembered file with `DocumentManager.Open` (each one runs [flow 3](#3-open-a-file-from-the-explorer) from step 3).
      - `StartViewModel`: adds the path to the recent list (`RecentWorkspaces.Opened`).
      - `RunViewModel.OnWorkspaceOpened`, `TestExplorerViewModel.OnWorkspaceOpened`.

## 3. Open a file from the explorer

1. Double-click in the tree: `ExplorerView.OnDoubleTapped` finds the `ProjectTreeNode` under the pointer →
   `ExplorerViewModel.OpenFileCommand.Execute(node)`.
2. `ExplorerViewModel.OpenFile(node)`: `ImageViewerManager.TryOpen` takes image files. Anything else goes to
   `IDocumentHost.OpenAndActivate(path)` (an extension method in `IDocumentHost.cs`) → `DocumentHost.Open` →
   `DocumentHost.Activate`.
3. `DocumentManager.Open(path)`: returns the existing document if the file is already open. Otherwise:
   - reads it with a `StreamReader` that detects the BOM (so the file is saved back in the same encoding);
   - `new OpenDocument(path, text, encoding, LanguageFor(path))`: makes an AvaloniaEdit `TextDocument`, calls
     `UndoStack.MarkAsOriginalFile()` (this is what "dirty" is measured against), and creates the `Diagnostics`
     segment collection;
   - adds it to `Documents` and raises `Opened`.
4. Reactions, in subscription order:
   - `Documents.CollectionChanged` → `DockLayoutViewModel.OnDocumentsChanged` →
     `new DocumentDockable(document, ...)` → `_factory.AddDockable(TargetDocumentDock(), tab)`. A new tab opens beside
     the selected tab, in that tab's window.
   - `Opened` → `LanguageSessionService.OnDocumentOpened` → `AttachAsync(document)` (C# files only, since only Roslyn is
     configured): waits for `LspManager.GetClientAsync`, then `client.DidOpen(uri, languageId, 1, text)`, creates
     `new DocumentConnection(client, new DocumentSync(...))`, raises `ClientAttached`, and pulls diagnostics if the
     server supports that.
   - `Opened` → `XamlAnalysisService.Start` (AXAML files only, see [flow 14](#14-axaml-diagnostics)),
     `RunViewModel.TrackDocument` (breakpoint changes), and `DocumentReloader` (changes on disk).
5. `DocumentHost.Activate` → `DocumentManager.BringToFront(document)` → sets `Active` → `ActiveChanged`:
   - `DockLayoutViewModel.OnActiveDocumentChanged` → `_factory.SetActiveDockable(tab)` + `SetFocusedDockable`.
   - `StatusBarViewModel.OnActiveDocumentChanged`, `OutlineViewModel`, `DockViewCache.PlaceSharedViews`,
     `NavigationService.OnActiveChanged` (back/forward history).
   - `ExplorerViewModel.TrackActiveAsync` selects the file in the tree, if Track Active Item is on.
6. Dock now shows the tab. It asks the control cache for the content: `DockViewCache.Build(DocumentDockable)` →
   `DocumentViewFor(tab)`. The first time, it creates:
   - a preview control for AXAML (`PreviewPaneView` with `PreviewSessions.For(document)`, see
     [flow 10](#10-axaml-live-preview)) or for Markdown (`MarkdownPreviewView`, whose code blocks are coloured by the
     `CodeHighlighter` singleton);
   - `new DocumentView(tab, _editorServices, preview)` → `new EditorPaneView()` → `Initialize(services)` (creates
     `EditorContext` and every controller, and calls `Editor.InstallTextMate(grammars)`) → `SetDocument(doc)`.
7. `EditorPaneView.SetDocument(doc)`: `_ctx.Detach()`, `Editor.Document = doc.Document`, `_ctx.Attach(doc)` (every
   controller hears `Attached`), sets the diagnostics renderer's segments, `TextMateController.SetGrammarForFile`,
   restores the caret, and subscribes to `doc.RevealRequested`. A posted callback then applies a pending reveal or
   restores the scroll position.

## 4. Type in a C# file: sync and diagnostics

1. A key press reaches AvaloniaEdit's `TextArea`, which changes `TextDocument`.
2. `TextDocument.Changing` → `DocumentSync.OnChanging` (`Services/Languages/DocumentSync.cs`). It converts the
   change to a 0-based LSP range *before* the edit is applied, adds it to `_pending`, and restarts a 150 ms
   `DispatcherTimer`.
3. `TextDocument.Changed` → `EditorContext.OnDocumentChanged` → raises `TextChanged` for the controllers. Also
   `OpenDocument`'s `UndoStack.PropertyChanged` → `IsDirty` / `DisplayTitle` change → `DocumentDockable` updates the
   tab title (adds `•`).
4. `TextArea.TextEntered` → `TypingController.OnTextEntered`: auto-close `{`, file-scoped namespace on `;`, and
   whether to open completion ([flow 5](#5-completion-c-and-axaml)) or signature help. In a comment
   (`CommentAtCaret`, which uses Core `CSharpComments.KindAt` for C#) nothing opens by itself, except the tag list for
   `<` in a `///` comment. In AXAML, `>` adds the end tag (`TryInsertXamlClosingTag`, which asks `XamlTagClosing`
   whether the element already has one) and `/` in a start tag makes it `/>` (`TrySelfCloseXamlTag`).
5. Caret moves → `EditorPaneView` (in `Initialize`) stores `active.CaretOffset` and sends `EditorStateChangedMessage`
   → `StatusBarViewModel.UpdateDocument` ("Ln x, Col y", line endings, encoding).
6. When the timer fires, `DocumentSync.Flush()` increments `Version`, then calls `LspClient.DidChange(uri, version,
   changes)`, or `DidChangeFull` if the server doesn't do incremental sync. It then raises `Flushed`.
   `LspClient.Enqueue` frames the JSON with `Content-Length` and puts it on a `Channel`. `WriteLoopAsync` writes the
   frames to the server's stdin in order.
7. `Flushed` → (`LanguageSessionService.AttachAsync` subscribed it) `PullDiagnosticsAsync(document)` → cancels any
   earlier pull → `LspClient.PullDiagnosticsAsync` (`textDocument/diagnostic`).
   - For servers that push instead: `LspClient.ReadLoopAsync` → `Dispatch` → `DiagnosticsPublished` →
     `LspManager` → `LanguageSessionService.OnDiagnosticsPublished` → `Dispatcher.UIThread.Post`.
8. `LanguageSessionService.ApplyDiagnostics`: drops the result if the user has typed since (pending changes, or a
   version mismatch). Otherwise `OpenDocument.ApplyDiagnostics(list)` (rebuilds the `TextSegmentCollection`; hints
   are skipped unless tagged Unnecessary, like an unused using, which become `IsUnnecessary` segments) and sends
   `DiagnosticsChangedMessage`.
9. Reactions:
   - `EditorPaneView.Receive(DiagnosticsChangedMessage)` (only the pane showing that document):
     `CodeActionController.RefreshMargin`, `TextView.InvalidateLayer(KnownLayer.Selection)` (squiggles),
     `UnnecessaryCodeColorizer.Refresh` (fades unnecessary code; redraws only if any was or is faded),
     `OverviewRuler.Refresh`, `EditorStateChangedMessage`.
   - `ProblemsViewModel.Receive(DiagnosticsChangedMessage)` rebuilds the Problems list (unnecessary code is listed
     under Messages), then filters it by the scope box, the kind buttons and the text filter.

## 5. Completion (C# and AXAML)

Started by typing (`TypingController.OnTextEntered`) or by Ctrl+Space (`KeyMap` → `EditorCommands.Completion` →
`EditorActionMessage(Completion)` → `EditorPaneView.Receive` → `CompletionController.ShowCompletionAsync(doc, null)`).

`TypingController.OnTextEntered` decides:

- **AXAML**: opens completion on `<`, space, `.`, `=`, `{`, `:`, `,`, or a letter when "completion while typing" is on.
- **C#**: opens on the server's `CompletionTriggerCharacters`, or on a letter (`ShouldAskForCompletion`). A lone `_`
  waits for the letter after it, so `_ =` and fast typing don't pop up every `_field`. If the open list is
  incomplete, it asks again.

`CompletionController.ShowCompletionAsync(doc, triggerChar)` (`Editor/CodeCompletion/CompletionController.cs`):

**AXAML branch**

Nothing opens by itself in a comment ([flow 4](#4-type-in-a-c-file-sync-and-diagnostics), step 4).

First, `XamlSourceMembers.UpdateAsync` (waits at most 300 ms): for each `x:DataType` type in the file
(`XamlTypeReference.DataTypes`), Roslyn's `workspace/symbol` finds every part of the class (the user's file and
each generator's `.g.cs`), and `textDocument/documentSymbol` on each lists its properties →
`XamlLanguageService.SetSourceProperties`. That's how `{Binding ...}` offers an `[ObservableProperty]` or a
`[RelayCommand]`'s `SaveCommand` before the next build, as Rider does. Asked again at most every 2 seconds per
type, and only of a server that is already running.

1. `_ctx.Xaml.GetCompletionsAsync(path, text, caret)` → `XamlLanguageService.GetCompletionsAsync`:
   1. `GetProjectAsync`: `WorkspaceModel.FindProjectForDocument`, then
      `Task.Run(() => Host.GetProjectFacts(project))`. This uses the MSBuild host, but is usually answered from its cache.
   2. `XamlCompletionService.GetCompletionsAsync`:
      - `GetMetadataAsync`: finds the built DLL (`BuiltAssemblies.FindNewest`) and throws "Build X first..." if there
        isn't one. The `MetadataBundle` is cached per project until the DLL's or the assets file's write time changes.
        It is built by `DepsJsonFileAssemblyProvider` (the DLL list from `.deps.json`), then
        `MetadataReader.GetForTargetAssembly` (backed by `DnlibMetadataProvider`, which loads each assembly with
        dnlib), then `ReadDocumentation` (XML doc files).
      - `GetProjectXaml`: the `x:Key`s and style classes of the project's `.axaml` files. The scan is trusted for 10
        seconds, and saved files clear it (`XamlCacheKeeper`).
      - `lock (_engineGate)`: `AddSourceProperties` gives the built types the properties their C# source has gained
        since the build (see below), then `_engine.GetCompletions(...)` runs the forked `CompletionEngine`
        (`Core/CompletionEngine/`).
2. If the request is still current (same sequence number, same document, caret and text unchanged): it creates a
   `CompletionWindow(editor.TextArea)` with one `XamlEngineCompletionData` per item, adds snippets, and calls
   `ShowCompletionWindow` → `window.Show()`.
3. If AXAML gives nothing, it falls through to the language-server branch, which for AXAML only shows snippets
   because no AXAML server is configured.

**Language-server branch (C#)**

1. `ConnectionFor(doc)`. With none: `ShowSnippetsOnly`.
2. `sync.Flush()` so the server sees what's on screen.
3. `client.CompletionAsync(uri, line, col, triggerChar)` (`textDocument/completion`).
4. Drops the result if it's stale. Sorts by `SortText`, wraps each item in `LspCompletionData`, and adds snippets.
   If the open list was incomplete and starts at the same place, it `Refill`s it. Otherwise it opens a new
   `CompletionWindow`.
5. `SelectionChanged` → `LspCompletionData.Resolve()` (`completionItem/resolve`: documentation and the `using` to add).
   Snippets are only added with Settings > IntelliSense > "Show snippets in the completion list" on
   (`SnippetController.CompletionItems`); Tab after a prefix expands one only with "Expand a snippet with Tab" on.
   A C# snippet inserted after modifiers ("private sealed cla" + `class`) drops the ones it starts with
   (`SnippetInserter.WithoutTypedModifiers`).
   Source generators: after a 600 ms pause in typing C#, `LanguageSessionService` sends Roslyn
   `workspace/_roslyn_refreshSourceGenerators` (`LspClient.RefreshSourceGenerators`), which otherwise reruns them only
   on save, so a new `[RelayCommand]`'s `SaveCommand` is in the next list.
6. Typing a character that isn't part of a name: `TypingController.OnTextEntering` →
   `CompletionController.HandleTextEntering` → `CompletionList.RequestInsertion(e)` commits the selected item.

## 6. Save (Ctrl+S)

1. `KeyMap.OnKeyDown` (tunnelling handler on every window) matches `File.Save` → `DocumentCommands.SaveDocumentCommand`
   with a `null` parameter, which means the active document.
2. `DocumentCommands.SaveDocumentAsync(null)` → `DocumentSaveService.SaveAsync(DocumentManager.Active)`:
   1. Refuses read-only documents (decompiled or generated source).
   2. If `FormatOnSave` is on and the server can format: `LanguageSessionService.FormatDocumentAsync(doc,
      reportUnchanged: false)` → `sync.Flush()` → `client.FormattingAsync` → `ApplyTextEdits` (from the end of the
      text backwards, via `TextDocument.Replace`).
   3. `DocumentManager.Save(doc)`:
      - `Saving` → `LanguageSessionService.OnDocumentSaving` → `Sync.Flush()`.
      - `OpenDocument.Save()` → `File.WriteAllText(path, text, Encoding)` → `UndoStack.MarkAsOriginalFile()`.
        The dirty marker clears.
      - `Saved` → `LanguageSessionService.OnDocumentSaved` → `client.DidSave(uri, text)`;
        `XamlCacheKeeper` → `XamlLanguageService.OnFileChanged`; `GitStatusTracker.RequestRefresh`;
        `FileChangeWatcher.Accept(path)` (so our own write doesn't show up as "changed on disk").
   4. An `IOException` or `UnauthorizedAccessException` is reported with `StatusMessage("Couldn't save ...")`.

## 7. Go to Definition (F12)

1. `KeyMap` → `Navigate.GoToDefinition` → `EditorCommands.GoToDefinition` → sends
   `EditorActionMessage(GoToDefinition)`.
2. Every open `EditorPaneView` receives it. Only the one showing `DocumentManager.Active` acts:
   `NavigationCommands.GoToDefinitionAsync()`.
3. **C#**: `TryGetActiveLanguageClient` → `client.DefinitionAsync(uri, line, char)` (`textDocument/definition`) →
   `NavigateToLocationsAsync`. With one result it goes straight there. With several, `IDialogService.PickLocationAsync`
   asks which. Then `GoToAsync(client, location)`:
   - a normal file: `NavigationService.ToLocation(location)` → `IDocumentHost.OpenAndActivate(path)`
     ([flow 3](#3-open-a-file-from-the-explorer), steps 3–7) → `document.Reveal(offset, length)`;
   - source-generated code: `client.SourceGeneratedTextAsync(uri)` → `NavigationService.ToGeneratedLocation`
     (opened read-only under `%TEMP%/AXamlIDE/Generated/`).
4. **AXAML** (`.axaml` files): `GoToXamlDefinitionAsync`:
   - on a binding path: `XamlLanguageService.GetBindingTargetAsync` → `GoToSourcePropertyAsync` (asks the C# server).
     If the property isn't in the workspace: `XamlLanguageService.DecompileAsync` →
     `XamlDecompilationService.DecompileTypeAsync` (ICSharpCode.Decompiler) → `OpenReadOnly`;
   - on a prefixed type: looks for its declaration through the C# server;
   - anywhere else: `DecompileTypeAsync` shows the element's type, decompiled.
5. `OpenDocument.Reveal` sets `PendingReveal` and raises `RevealRequested` →
   `EditorPaneView.OnRevealRequested` → posted `ApplyPendingReveal` → `Editor.Select`, `Editor.ScrollTo`,
   `window.Activate()` if the tab is in another window, and `Editor.Focus()`.

## 8. Build and Run

**Build** (Ctrl+Shift+B → `Build.Build` → `RunViewModel.BuildCommand`)

1. `RunViewModel.BuildWorkspaceAsync(BuildKind.Build)` picks the target: the solution, else the startup project, else
   the explorer selection's project, else it asks (`ChooseStartupProjectAsync`).
2. `BuildAsync(path, kind)`:
   1. `DocumentSaveService.SaveAllAsync()` (skipped for Clean).
   2. `BeginBuildOutput` clears `BuildOutput` and sends `ShowToolTabMessage(Output)` →
      `DockLayoutViewModel.ShowTool`.
   3. `BuildWithNoticeAsync(() => _projectRunner.BuildAsync(path, configuration))`:
      - sends `BuildStartedMessage` → `PreviewSessions.OnBuildStarted` → every `PreviewPaneViewModel.PauseForBuild`;
      - `DotNetProjectRunner.ExecuteAsync` → `dotnet build <path> --configuration <c>`. Each stdout and stderr line →
        `OutputReceived` → `RunViewModel.AppendProcessOutput` → `AppendBuildOutput` (posted to the UI thread if
        needed) → the Output panel;
      - `finally`: sends `BuildFinishedMessage(exitCode == 0)` → `PreviewSessions.OnBuildFinished` (resume previews),
        `XamlCacheKeeper` → `XamlLanguageService.OnBuildFinished` (looks for the DLL again) and
        `XamlAnalysisService` (checks open AXAML files for obsolete uses again);
      - then `PublishBuildProblems`: `BuildOutputParser.Parse` reads the build's lines into errors and warnings →
        `BuildProblemsMessage` → `ProblemsViewModel` (the Build rows). The status bar gets "Build failed: N errors...",
        and if there were errors, `ShowToolTabMessage(Problems)` brings the Problems panel up, as Visual Studio does
        with its Error List.
   4. Double-clicking an error line in Output: `RunViewModel.OpenOutputLineCommand` → `BuildOutputParser.TryParseLine`
      → `NavigateToProblemMessage` → `NavigationService`.

   Every build (Build, Rebuild, Clean, and the builds before Run and Debug) goes through `BuildWithNoticeAsync`, so
   each one sends exactly one started and one finished message.

**Run** (`Debug.StartWithoutDebugging` → `RunViewModel.RunProjectCommand`)

1. `RunProjectAsync(selection)`: `ChooseStartupProjectAsync` → `SaveAllAsync` →
   `BuildWithNoticeAsync(() => _projectRunner.BuildAsync(...))` (previews pause and resume as for Build).
2. If the build succeeded: `BeginRunOutput` (shows the Debug Console) → `IsRunning = true` →
   `_projectRunner.RunAsync` → `dotnet run <path> --configuration <c> --no-build`. Its output goes to `ConsoleOutput`.
3. Stop → `StopRunningProject()` → `DotNetProjectRunner.Stop()` → `Process.Kill(entireProcessTree: true)`.

## 9. Debug: start, then stop at a breakpoint

**Setting a breakpoint** (before or during debugging)

1. Clicking the gutter: `BreakpointMargin` → `OpenDocument.ToggleBreakpoint(line)`. This adds or removes a
   `TextAnchor` (`SurviveDeletion = true`, so the breakpoint follows its line as text is edited) and raises
   `BreakpointsChanged`.
2. `RunViewModel.OnDocumentBreakpointsChanged`: if a session is running, `DebugSession.SetBreakpointsAsync(path,
   lines)` (DAP `setBreakpoints`).

**Starting** (F5 → `Debug.StartOrContinue` → `RunViewModel.StartOrContinueDebuggingCommand`)

1. `StartOrContinueDebuggingAsync`: while paused it continues. Otherwise `StartDebuggingAsync` →
   `ChooseStartupProjectAsync` → `DebugProjectAsync(projectPath)`.
2. `DebugProjectAsync`:
   1. `SaveAllAsync`, then `BuildWithNoticeAsync(BuildAsync)`. A failed build stops here.
   2. `Task.Run(() => ResolveDebugProgram(...))` → `Host.GetProjectFacts(project, configuration)` → the output DLL path.
   3. `new DebugSession(DebugSession.ResolveExecutablePath())` starts `netcoredbg --interpreter=vscode` and
      begins `PumpMessagesAsync` (reads DAP messages through `DebugAdapterTransport`).
   4. `new DebugInspector(session)` subscribes to `session.EventReceived`. `RunViewModel` subscribes to
      `inspector.Stopped`, `inspector.Resumed`, `session.OutputReceived`, `session.EventReceived`
      (→ `OnDebugAdapterEvent`) and `session.Failed`.
   5. `OnIsDebuggingChanged()` sends `DebugSessionChangedMessage(true)` → `DockLayoutViewModel.Receive` puts Locals,
      Watch and Call Stack back and shows the bottom panel.
   6. Collects the breakpoints of open C# documents and calls `session.StartAsync(program, cwd, breakpoints)`:
      DAP `initialize` → `launch` (not awaited yet) → waits for the `initialized` event → `setBreakpoints` for each
      file → `configurationDone` → awaits `launch`.

**Stopping at a breakpoint**

1. netcoredbg sends a `stopped` event → `DebugSession.PumpMessagesAsync` → `EventReceived` →
   `DebugInspector.OnEvent` → `HandleStoppedAsync` (fire and forget): `GetStackAsync`, `CollectValuesAsync` (top
   frame's locals), and `GetExceptionTextAsync` if it stopped on an exception. Then it builds a `StopSnapshot` and
   raises `Stopped`.
2. `RunViewModel`'s `Stopped` handler, posted to the UI thread:
   - `IsDebugStopped = true` (enables Continue and the Step commands);
   - on the first stop of a session it sends `ShowToolTabMessage(Locals)`;
   - `DebugStatus = "Paused (breakpoint)."` → `StatusMessage`;
   - sends `NavigateToDebugLocationMessage(path, line)` → `NavigationService.ToDebugLocation` → `OpenAndActivate`
     → `Reveal`;
   - sends `DebugSnapshotChangedMessage(snapshot)` → `DebugSnapshotTracker.Receive` → sets `Current` and
     `SelectedFrame`, makes a `DebugValueProvider`, raises `Changed`.
3. `DebugSnapshotTracker.Changed` listeners: `LocalsViewModel.RefreshAsync`, the Watch and Call Stack view models, and
   each editor's `DebugValuesController.Apply` (current-line highlight and inline values).
4. Continue (F5) → `ContinueDebuggingAsync` → `session.ContinueAsync(threadId)`. The adapter's `continued` event
   makes `DebugInspector` raise `Resumed`, which sends `DebugSnapshotChangedMessage(null)`.
5. Stop → `StopRunningProject` → `StopDebugSessionAsync` → `session.StopAsync()` (DAP `disconnect` with
   `terminateDebuggee`, then kills the process if needed) → `EndDebugSession` → `DebugSessionChangedMessage(false)`
   (the debugger panels leave the bottom panel).

## 10. AXAML live preview

1. Opening an `.axaml` tab: `DockViewCache.DocumentViewFor` → `PreviewSessions.For(document)` →
   `new PreviewPaneViewModel(document, xaml, messenger)`, which subscribes to `TextDocument.Changed` and calls
   `UpdatePreviewAsync()`.
2. `PreviewPaneViewModel.UpdatePreviewAsync()` (one at a time through `_updateGate`; a request that is no longer the
   newest gives up):
   1. `PreviewThemeVariant.Apply(text, SelectedTheme)`.
   2. Returns early if the host already shows this text from the current build.
   3. `XamlLanguageService.GetPreviewTargetAsync(path)` → `XamlPreviewTargetResolver.Resolve` (cached per project,
      run with `Task.Run`). It finds the app project to host the preview, its built DLL, and the designer host path.
   4. If the target changed or the host isn't ready: `AvaloniaPreviewProcess.StartAsync(target)`:
      - picks a free loopback port (`TcpListener` on port 0), then `new BsonTcpTransport().Listen(...)`;
      - `dotnet exec --runtimeconfig ... --depsfile ... <HostAppPath> --transport tcp-bson://127.0.0.1:<port>/
        --session-id <n> --method avalonia-remote <app.dll>`;
      - when the host connects: `InitializeConnectionAsync` sends `ClientSupportedPixelFormatsMessage` and
        `ClientRenderInfoMessage`, and hooks `connection.OnMessage`.
   5. `AvaloniaPreviewProcess.UpdateXamlAsync(assemblyPath, path, xaml)` → `connection.Send(new UpdateXamlMessage
      {...})`.
3. The host renders and sends a `FrameMessage` → `OnConnectionMessage` turns it into a `RawImageData` (Skia colour
   type), raises `FrameReceived` on the UI thread, and replies with `FrameReceivedMessage`. An `UpdateXamlResultMessage`
   with an error raises `ErrorChanged` → `PreviewPaneViewModel` → `PreviewProblemMessage` → `ProblemsViewModel` (a
   Problems row) and `XamlAnalysisService` (a squiggle in the editor, [flow 14](#14-axaml-diagnostics)).
4. `PreviewPaneViewModel.OnFrameReceived` → `FrameChanged` → `PreviewPaneView.OnFrameChanged` → the frame is drawn
   through `SkImageControl` / `SkBitmapImage` (SkiaSharp `InstallPixels`, drawn in a custom draw operation through
   `ISkiaSharpApiLeaseFeature`).
5. Each edit restarts a 300 ms `DispatcherTimer` (`OnDocumentChanged`). When it fires: `UpdatePreviewAsync` again.
   After `RecycleAfterUpdates` updates, `RecycleAsync` starts a fresh host in the background and swaps it in once it
   has drawn a frame.
6. Builds: `BuildStartedMessage` → `PauseForBuild`; `BuildFinishedMessage` → `ResumeAfterBuild`.
7. Closing the tab: `DocumentManager.Closing` → `PreviewSessions.OnDocumentClosing` → `PreviewPaneViewModel.Dispose`
   → `AvaloniaPreviewProcess.Dispose`.

## 11. Find in files

1. Ctrl+Shift+F → `Edit.FindInFiles` → `MainWindowViewModel.ShowFindInFiles()` → `DockLayoutViewModel.ShowTool(Search)`.
   Search has no home dock, so the first time it opens it floats (`DockFactory.FloatTool`, near the main window) →
   sends `FocusFindInFilesMessage` → `SearchViewModel` (the view focuses the box).
   - From Search Everywhere (the top bar's button or double Shift → `SearchEverywhereViewModel.Open` →
     `ShowRequested` → MainWindow shows its one `SearchEverywhereWindow`, hidden rather than closed so it reopens
     where it was dragged): its Text row calls `MainWindowViewModel.FindInFiles`, which copies the words into
     `Search.SearchQuery` and runs `SearchWorkspaceCommand`; the window hides and its box clears. Its other tabs find
     types and symbols (Roslyn `workspace/symbol`), files (`IDocumentHost.GetWorkspaceFilesAsync`, matched by
     `FuzzyMatch`), and actions (`KeyMap.Actions`: every `CommandCatalog` command with its key) plus the Settings
     window's on/off options (a switch changes one in place; Enter opens `SettingsWindow` at its page with its check
     box focused). The list is rebuilt once per pause in typing (90 ms), after waiting up to 400 ms for the symbols.
2. `SearchViewModel.SearchWorkspace()`:
   1. `GetSearchRoots()`: the workspace folder, plus the folders of solution projects outside it
      (`WorkspaceModel.SearchRoots`).
   2. `SnapshotOpenTexts()`, so unsaved text in open tabs is searched instead of the file on disk.
   3. `WorkspaceSearchService.SearchAsync(roots, query, openDocuments)` → `SearchResultGroup.Build` → `SearchResults`.
3. Double-clicking a result: `OpenResultCommand` → sends `NavigateToSearchResultMessage(result, query)` →
   `NavigationService.ToSearchResult` → `OpenAndActivate` → `Reveal(offset, matchLength)`.

## 12. Git commit

1. `GitStatusTracker` keeps the workspace's status current. It uses a `FileSystemWatcher` on the repository plus a
   400 ms debounce (`DispatcherTimer`) → `RefreshAsync` → `GitService.GetStatusAsync` (runs `git`) → raises `Changed`.
   `GitViewModel` rebuilds its `Changes` and `Staged` lists from that.
2. Stage: `GitViewModel.StageCommand` → `RunAsync(root => _git.StageAsync(root, [path]))`.
3. Optional: the sparkle button in the message box → `GitViewModel.GenerateCommitMessageCommand` (enabled while
   there are changes and no message is being written). It sends the staged changes if there are any, else
   everything, plus the names of untracked files (which `git diff` doesn't show), to
   `CommitMessageWriter.WriteAsync(root, staged, newFiles)` (Core `Services/Agents/`): it gets the diff from
   `GitService`, then runs the Claude Code CLI found beside the agent (`ClaudeAgentInstall.FindCli`) as
   `claude -p --model haiku --tools "" --no-session-persistence ...` with the diff on stdin. The reply goes into
   `CommitMessage`, for the user to check. A missing CLI or a git error goes to the status bar.
4. Commit: `GitViewModel.Commit()` (enabled by `CanCommit`: not busy, something to commit, a message). If nothing is
   staged it runs `CommitAll()`. Otherwise `RunAsync(root => _git.CommitAsync(root, message))` →
   `GitService.CommitAsync` → `git commit -q -m <message>`.
5. `RunAsync` → `TryRunAsync` sets `IsBusy`, runs the action, and reports any error (`Report`). On success the
   message box is cleared and `StatusMessage("Committed N files.")` is sent.

## 13. Close the solution

**File > Close Solution** → `MainWindowViewModel.CloseSolutionCommand` → `CloseWorkspaceAsync()`. Opening another
workspace over this one goes the same way (`OpenWorkspaceAsync` calls `CloseWorkspaceAsync` first).

1. `ConfirmLeaveAsync()` (shared with exiting the app):
   1. If debugging: `IDialogService.ConfirmAsync("Stop Debugging", "Do you want to stop debugging?", ...)`. Cancel
      stops here.
   2. For each dirty document: `IDialogService.AskSaveAsync(document)`. Cancel stops here and nothing closes. All the
      answers are collected first, then the "Save" ones are saved (`DocumentSaveService.SaveAsync`). A failed save
      also stops here.
   3. `RunViewModel.StopDebuggingAsync()` → `StopDebugSessionAsync`: DAP `disconnect` with `terminateDebuggee: true`
      (2 s timeout), kills netcoredbg's process tree if it's still running, then `EndDebugSession`. This is awaited, so
      the session has ended before anything closes.
2. `WorkspaceSession.Close()` → `Save()` (open files, caret, layout, startup project → `WorkspaceStateStore.Save`).
3. `DocumentManager.Close(doc, force: true)` for each document. Each one raises `Closing` (didClose, preview host
   disposed, breakpoints untracked) and removes it from `Documents`. `DockLayoutViewModel.OnDocumentsChanged` then
   removes the tab and raises `TabRemoved`, and `DockViewCache.OnTabRemoved` calls `DocumentView.Shutdown()` →
   `EditorPaneView.Shutdown()`.
4. `DockLayoutViewModel.CloseAllPages()` closes the page tabs (diff, NuGet, Publish, visualizer, image, Claude). The
   Claude conversation itself lives in the `AgentChatViewModel` singleton; when the workspace folder changes, its
   `OnWorkspaceChanged` starts a new chat, because Claude's tools work in the session's folder.
5. `WorkspaceViewModel.Close()`: clears `Roots`, `ClearProjectCaches`, `LanguageSessionService.ClearWorkspace()` →
   `LspManager.ClearWorkspace()` → `Restart()` (disposes the server clients). Raises `Closed`.

**Closing a single tab** goes a different way. Dock raises `DockableClosing` → `DockLayoutViewModel.OnDockableClosing`
cancels Dock's own close and posts `DocumentCommands.CloseDocumentCommand` instead. That asks about unsaved changes,
then calls `DocumentManager.Close`, and the tab goes when the document does.

**Exiting the app** (File > Exit → `MainWindowViewModel.Exit` → `ExitRequested` → `MainWindow.Close`, or the
window's X):

1. The first `MainWindow.Closing` sets `e.Cancel = true`, because a `Closing` handler can't await the questions.
   It then awaits `MainWindowViewModel.ConfirmExitAsync()` → `ConfirmLeaveAsync()` (step 1 above: stop debugging?,
   save prompts, then the debug session is stopped cleanly). While the questions are open, `_confirmingExit` makes a
   second click on the X do nothing.
2. If everything was confirmed: `_exitConfirmed = true` and `Dispatcher.UIThread.Post(Close)`. The close is posted
   because, with nothing to ask, the check finishes before the cancelled close has.
3. The second `Closing` goes through: `WorkspaceSession.Save()` → `DockLayoutViewModel.CloseFloatingWindows()`.
4. `desktop.Exit` → `services.Dispose()` disposes every disposable singleton (MSBuild host, language servers,
   `RunViewModel` → runner, `PreviewSessions` → designer hosts). A program started with Run (not debugging) is
   stopped here without a question, as in Visual Studio: `DotNetProjectRunner.Dispose` → `Stop` kills its process tree.

## 14. AXAML diagnostics

No language server handles AXAML, so `XamlAnalysisService` (`Services/Languages/XamlAnalysisService.cs`) makes the
squiggles and Problems rows for `.axaml` files itself, and sends them out the same way a server's are.

1. `DocumentManager.Opened` → `XamlAnalysisService.Start(document)` (AXAML only): makes a 300 ms `DispatcherTimer`,
   subscribes to `TextDocument.TextChanged` (each edit bumps the watch's `Version` and restarts the timer), and runs
   `AnalyseAsync` once straight away.
2. `AnalyseAsync(document)` (when the timer fires, and after every `BuildFinishedMessage`):
   1. `XamlNamespaceAnalysis.Find(text)` (Core `Services/Xaml/`): xmlns problems, worked out from the text alone.
   2. `XamlLanguageService.GetObsoleteUsesAsync(path, text)` → `XamlCompletionService.GetObsoleteUsesAsync` →
      `GetMetadataAsync` (the same metadata completion uses, so it needs a built DLL) → `XamlObsoleteAnalysis.Find`
      on a background thread: uses of `[Obsolete]` types and properties. If the project isn't built, this throws and
      there's just nothing to flag.
   3. If the metadata isn't ready within 150 ms (the first read of a project takes seconds), the xmlns results are
      applied first, and the full set when the obsolete check finishes.
3. `Apply` drops a result if the document closed or was edited since (`Version` changed). Otherwise `Publish`:
   adds the preview's error as a squiggle if there is one (`PreviewErrorDiagnostic.From(error, text)`, from the last
   `PreviewProblemMessage`), then `OpenDocument.ApplyDiagnostics` and `DiagnosticsChangedMessage`, which runs the
   same reactions as [flow 4](#4-type-in-a-c-file-sync-and-diagnostics), step 9.
4. `PreviewProblemMessage` → `OnPreviewProblem`: stores the error and publishes again. Mid-edit it waits for the next
   analysis, since the last one's positions are out of date.
5. `DocumentManager.Closing` → `Stop`: stops the timer and unsubscribes.

## 15. Claude panel: send a message

The panel is a page tab (`AgentChatDockable`, opened by View > Claude or the title-bar button) showing `AgentChatView`. The conversation is held by the
`AgentChatViewModel` singleton (split into `.Turn`, `.History` and `.Attachments` partial files), so closing and
reopening the tab brings the same chat back. It talks to `claude-agent-acp` through `AcpClient` (Core
`Services/Agents/`) using the Agent Client Protocol.

**Starting** (the first time the panel is shown)

1. `AgentChatView.OnDataContextChanged` → `AgentChatViewModel.EnsureStarted()` → `StartAsync()`.
2. `ClaudeAgentInstall.FindAgent()`. If the agent isn't installed: `State = NotInstalled`, and
   `ClaudeAgentInstall.FindLatestReleaseAsync` looks up the newest GitHub release for this OS. Nothing is downloaded
   until the Install button: `InstallCommand` → `ClaudeAgentInstall.InstallAsync` (download, unpack the `.zip` or
   `.tar.gz` to a temporary folder, then move it into place), then `StartAsync` again.
3. `AcpClient.StartAsync(agent, args, folder)` starts the process and its read loop (`AcpClient.Transport.cs`:
   newline-delimited JSON-RPC on a `Channel`), then `InitializeAsync` (the ACP `initialize` request) reads the
   agent's capabilities (`CanListSessions`, `CanLoadSessions`, `CanSendImages`). The IDE says it can run sign-in
   commands (`_meta["terminal-auth"]`), so the agent lists its ways to sign in (`AuthMethods`, read by
   `AcpParser.AuthMethods`).
4. The view model subscribes to `Updated`, `AuthStateChanged` and `Exited`, posting each to the UI thread, and sets
   `PermissionHandler`. Then `EnsureSessionAsync` → `NewSessionAsync(folder)` (`session/new` in the workspace
   folder), so the Mode and Model drop-downs show before anything is typed (`ShowSettings`).

**Signing in** (`AgentChatViewModel.SignIn.cs`), like VS Code's Claude sign-in

1. The agent says it isn't signed in: `auth/status_update` with `unauthenticated` → `AuthStateChanged`, or
   `session/new` or `session/prompt` fails with ACP's "authentication required" error (`AcpException.IsAuthRequired`,
   code -32000; this is how an expired login shows up). `NeedSignIn` sets `State = SignIn` and remembers the
   conversation to reopen. A message that failed goes back into the prompt box.
2. The panel shows a button per `AcpAuthMethod` (Claude subscription, Anthropic Console). A click → `SignInCommand` →
   `AgentSignIn.Start`, which runs the method's command (`claude-agent-acp --cli auth login --claudeai`) with no
   window. The command opens the browser itself, and the browser hands the login back to it on localhost.
3. The command also prints a link for when the browser can't open (`AgentSignIn.LinkFound`). The panel then offers
   Open sign-in page / Copy link and a box for the code that page shows (`SubmitSignInCodeCommand` →
   `AgentSignIn.SubmitCode`, written to the command's stdin). Cancel kills the command.
4. The command exits 0 → `RestartAfterSignInAsync`: a fresh agent (the old one still thinks it isn't signed in),
   then `OpenSessionAsync` reopens the interrupted conversation.

**Attachments** (optional)

- The + button → `AddFilesCommand` → `IDialogService.PickFilesAsync`. Files dropped on the panel → `OnDrop`.
  Ctrl+V in the prompt box → `AgentChatView.PasteAsync`: copied files, else a picture (saved as PNG), else plain
  text.
- `AttachFiles` / `AttachPastedImage` add a `PromptAttachment` chip. A picture (up to `MaxImageBytes`) goes as an
  image block if the agent takes pictures. Anything else goes as a file link the agent reads itself.

**Sending** (Enter in the prompt box, or the send button)

1. `AgentChatView.OnPromptKeyDown` (a tunnelling handler, so Enter doesn't become a new line) → `SendCommand` →
   `AgentChatViewModel.Send()` (`AgentChatViewModel.Turn.cs`): adds a `UserMessageItem`, sets `IsTurnRunning`, and
   calls `client.PromptAsync(sessionId, [text, ...attachments])` (`session/prompt`), which returns when the turn is
   over.
2. While it runs, the agent sends `session/update` notifications → `AcpClient.Updated` → `OnUpdate`:
   - message and thought chunks are added to the last `AgentTextItem` (or start a new one);
   - tool calls become `ToolCallItem`s and are updated in place;
   - plans, the session title, settings and token usage update the panel.
   Replies are shown by `MarkdownText` → `MarkdownRenderer`. Code blocks are coloured by `CodeHighlighter` (the
   editor's TextMate grammars and current theme; `DockViewCache.ChatView` sets it as an inherited attached
   property), and each has a Copy button. File links → `AgentChatViewModel.OpenFile` →
   `NavigateToSearchResultMessage` → `NavigationService`.
3. `session/request_permission` from the agent → `PermissionHandler` → `AskPermissionAsync` (on the UI thread): adds a
   `PermissionItem` to the conversation and awaits the button the user clicks.
4. When `PromptAsync` returns: a stop reason other than the normal end becomes a `NoticeItem`, open permission
   questions are cancelled, and still-running tool calls are marked cancelled.
5. Stop (the button, or Escape while a turn runs) → `StopCommand` → `client.Cancel(sessionId)` (`session/cancel`).

**History**: the history button → `LoadHistoryCommand` → `client.ListSessionsAsync(folder)` (`session/list`).
Picking one → `OpenSessionAsync` → `client.LoadSessionAsync` (`session/load`), and the agent replays the conversation
as `session/update`s.

## 16. Solution-wide analysis

`SolutionAnalysisService` (`Services/Languages/SolutionAnalysisService.cs`) keeps the compiler diagnostics of every
file in the solution in `Files`, for the Problems list and the status bar's error count. Open files' come from their
own pulls ([flow 4](#4-type-in-a-c-file-sync-and-diagnostics)), and both readers skip open files in `Files`.

**Starting**

1. Turning it on (the setting, through `OnSettingsChanged` → `StartAsync`), Roslyn's projects finishing loading, or
   Roslyn asking for a refresh (`LspManager.DiagnosticsRefreshNeeded` → `OnRefreshNeeded`) → `Poll(client)` → stops
   the last live analysis → `LiveAsync`.
2. `LiveAsync`, once per server: `RoslynExtensionClient.ActivateAsync` (Core `Services/Lsp/RoslynExtensionClient.cs`)
   → `LspClient.ActivateExtensionAsync` (`server/_vs_activateExtension` with `roslynextension/AXamlIDE.RoslynExtension.dll`).
   It needs all three handlers registered. Loaded → `WatchAsync` (below), once the projects have loaded. Not loaded
   → `UseWorkspaceDiagnostics` → one `PollAsync` per identifier (Roslyn's own way, at the end).

**Through the extension** (`WatchAsync`, until stopped)

1. `WaitForQuietAsync`: until nothing has been typed for 0.5 s (`QuietDelay`).
2. `CheckAsync`, cancelled by typing (`OnTyped` cancels `_typing`):
   1. `RoslynExtensionClient.ProjectsAsync(open files)` → the extension's `ProjectsHandler`: each project's id and
      version, the open files' projects first.
   2. Each project whose version isn't the one in `_projects` → `DocumentDiagnosticsAsync` →
      `ProjectDiagnosticsHandler` → `DiagnosticCache.DocumentDiagnosticsAsync` (in the server: only files whose text
      or project key changed are checked, in parallel) → `RoslynExtensionClient.ParseDiagnostics` (the diagnostics are
      in the LSP's shape, so `LspClient.ParseDiagnostic` reads them) → stored in `_projects` → `Publish`.
   3. Then each project whose compilation step is older than its files step → `CompilationDiagnosticsAsync` →
      `ProjectCompilationDiagnosticsHandler` (the whole project compiled, less what step 2 reported) → `Publish`.
3. `WaitForChangeAsync`: until `Kick` (typing, a file closing) or 1 s (`WatchInterval`), which catches files changed
   on disk by git or another program. Then back to step 1.
4. If the extension throws (a server update changed it): back to `workspace/diagnostic` for this server.

**Through workspace/diagnostic** (`PollAsync`, one per identifier)

1. `WaitForQuietAsync`, then `LspClient.WorkspaceDiagnosticsAsync(identifier, result ids, partial)`. Roslyn holds the
   request until something changes, then goes through the solution, sending files in batches as partial results
   (`$/progress` with our `partialResultToken`, routed by `LspClient.HandleProgress`).
2. The batches are gathered on the reading thread and applied on the UI thread, several at a time (`Apply` →
   `Publish`). Typing cancels the request, and it's asked again when the typing stops.
3. Roslyn leaves open files out, so a saved file's diagnostics are kept from when it closes (`_closedSince`) until
   Roslyn reports it.

**Publishing**: `Publish` merges the extension's reports (`ExtensionFiles`: a file in two projects, one per
target framework, is listed once), `workspace/diagnostic`'s, `_closedSince`, and the last Run Code Analysis
(`_codeAnalysis`, the analyzers' diagnostics, which the live analysis doesn't have) into `Files`, then sends
`SolutionDiagnosticsChangedMessage` → `ProblemsViewModel` and `StatusBarViewModel` rebuild.

**Run Code Analysis on Solution** (`RunCodeAnalysisAsync`) stops the live analysis, widens Roslyn's analyzer scope
(`CodeAnalysisRun`, `WidenAsync`), touches a file per project, asks `workspace/diagnostic` once with no result ids,
narrows the scope again, and restarts the live analysis.
