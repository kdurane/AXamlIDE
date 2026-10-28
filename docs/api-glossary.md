# Third-party API glossary

The third-party types and methods the code calls: what each one does, and where we call it. It is grouped by
package, in the order the packages appear in the `.csproj` files. Paths without a folder prefix are in the app project
(the repo root). `Core/` stands for `Projects/AXamlIDE.Core/` and `Host/` for `Projects/AXamlIDE.msbuildhost/`.

**What's covered.** Every call to the smaller libraries (dnlib, ICSharpCode.Decompiler, ClrMD, DiffPlex, ClosedXML,
Markdig, SkiaSharp, Microsoft.Build) is listed. **Avalonia** and **AvaloniaEdit** are used in hundreds of places, mostly
for ordinary control properties (`IsVisible`, `Text`, `Grid.SetRow`...). For those two, this page lists the calls
that do real work or are easy to miss, not every property set. Calls made only from XAML (bindings, styles) are left
out, except where a package is used *only* from XAML.

The .NET base library (`Process`, `System.Text.Json`, `FileSystemWatcher`...) is not third-party, so it isn't listed.

---

## Avalonia (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`) — app

| API | What it does | Where we call it |
|---|---|---|
| `AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()` | Builds the app and picks the platform backend (X11/Wayland, Win32, macOS) | `Program.BuildAvaloniaApp` |
| `AppBuilder.StartWithClassicDesktopLifetime(args)` | Starts the UI loop with a desktop lifetime (main window, exit on last window) | `Program.Main` |
| `AvaloniaXamlLoader.Load(this)` | Loads the compiled XAML for `App` (styles, resources) | `App.Initialize` |
| `IClassicDesktopStyleApplicationLifetime.MainWindow` / `.Exit` | The window whose closing ends the app; the exit event | `App.Start`, `App.OnFrameworkInitializationCompleted` |
| `Application.DataTemplates.Add(new FuncDataTemplate<T>(match, build))` | App-wide "view for this view model type" | `App.UseDockViews` (the `WelcomeViewModel` screen) |
| `Application.Styles.Add(new Style(selector => selector.OfType<T>()) { Setters = ... })` | An app-wide style made in code | `App.UseDockViews` (sets `DockViewCache` on every `DockControl`) |
| `ControlRecyclingDataTemplate.ControlRecyclingProperty` / `IControlRecycling` / `ControlRecycling` (namespace `Avalonia.Controls.Recycling`, from the `Dock.Controls.Recycling` package that `Dock.Avalonia` brings in) | The control cache Dock asks for tab contents | `App.UseDockViews`, `Views/DockViewCache.cs` (implements it, and passes everything else to a `ControlRecycling`) |
| `Dispatcher.UIThread.Post(action, priority)` | Runs code later on the UI thread | Many places (24 files), e.g. `LanguageSessionService.OnDiagnosticsPublished`, `RunViewModel.Debug`, `EditorPaneView.SetDocument`, `DockLayoutViewModel.OnDockableClosing` |
| `Dispatcher.UIThread.InvokeAsync` | Runs on the UI thread and waits for it | `Services/Preview/AvaloniaPreviewProcess.cs` (raising `FrameReceived`) |
| `Dispatcher.UIThread.CheckAccess()` | "Am I on the UI thread?" | `RunViewModel.AppendBuildOutput` / `AppendConsoleOutput` |
| `DispatcherTimer` (`Interval`, `Tick`, `Start`, `Stop`) | A timer that ticks on the UI thread; used for debounces | `Services/Languages/DocumentSync.cs` (150 ms didChange), `ViewModels/PreviewPaneViewModel.cs` (300 ms), `Services/Git/GitStatusTracker.cs` (400 ms), `Services/Documents/FileChangeWatcher.cs`, `Editor/CodeLens/CodeLensController.cs`, `Editor/VisibleLinesFetcher.cs` |
| `StorageProvider.OpenFilePickerAsync` / `OpenFolderPickerAsync` | The system file and folder pickers | `Views/DialogService.cs` (`PickSolutionAsync`, `PickFolderAsync`), `Views/PublishWizardWindow.axaml.cs`, `Views/PublishSettingsWindow.axaml.cs` |
| `TopLevel.Clipboard.SetTextAsync` | Puts text on the clipboard | `Views/ClipboardService.cs` |
| `TopLevel.GetTopLevel(control)` | The window a control is in (null when it isn't on screen) | `EditorPaneView` (`ApplyPendingReveal`, `RestoreViewState`, scroll tracking) |
| `Window.Activate()` | Brings a window to the front | `EditorPaneView.ApplyPendingReveal` (a tab in a floating window) |
| `InputElement.AddHandler(event, handler, RoutingStrategies.Tunnel)` | Sees an event before the control under the pointer or focus does | `Shell/KeyMap.AttachTo` (`KeyDownEvent`: shortcuts are caught before the editor), `EditorPaneView.Initialize` (`PointerWheelChangedEvent`: Ctrl+wheel zoom), `Views/ExplorerView.axaml.cs` (`DoubleTappedEvent`) |
| `AvaloniaProperty.Register` / `RegisterAttached` | Declares a styled or attached property | `Shell/KeyMap.cs` (`CommandId`, `ShortcutTip`), `Views/SkImageControl.cs`, `Views/CollapsiblePane.cs`, `Views/DebugValueTreeView.axaml.cs`, `Views/GitGraphCell.cs` |
| `Control.LoadedEvent.AddClassHandler<MenuItem>` / `ToolTip.ToolTipOpeningEvent.AddClassHandler<Button>` | Runs code for *every* control of a type when it loads or opens | `KeyMap` constructor (shows each menu item's shortcut) |
| `Visual.GetVisualParent()` / `GetSelfAndVisualAncestors()` (`Avalonia.VisualTree`) | Walks up the visual tree | `DockViewCache.Detach`, `ExplorerView.OnDoubleTapped` |
| `ContentPresenter.UpdateChild()` | Makes a presenter drop or refresh its child now | `DockViewCache.Detach` (so a view can be moved to another tab strip) |
| `GetResourceObservable(key)` + `Bind` | Follows a theme resource as the theme changes | `MainWindow.CreateFloatingWindow` |
| `ImmediateDrawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>()` + `Lease()` (`Avalonia.Skia`) | Gets the Skia canvas behind Avalonia's renderer, for direct drawing | `Views/SkBitmapDrawOperation.Render` |
| `FluentTheme` (XAML) | The control theme | `App.axaml` |

### Avalonia.Remote.Protocol (a dependency of the `Avalonia` package)

The protocol Avalonia's designer host speaks. We use it to drive the live preview.

| API | What it does | Where we call it |
|---|---|---|
| `new BsonTcpTransport().Listen(IPAddress.Loopback, port, onConnection)` | Waits for the designer host to connect over TCP | `AvaloniaPreviewProcess.StartAsync` |
| `IAvaloniaRemoteTransportConnection.Send(message)` / `OnMessage` | Sends a message to the host; receives messages from it | `AvaloniaPreviewProcess` (`UpdateXamlAsync`, `InitializeConnectionAsync`, `OnConnectionMessage`, `SendInputAsync`) |
| `ClientSupportedPixelFormatsMessage`, `ClientRenderInfoMessage` | Tell the host which pixel formats and DPI to render at | `AvaloniaPreviewProcess.InitializeConnectionAsync`, `SetScalingAsync` |
| `UpdateXamlMessage` → `UpdateXamlResultMessage` | Sends the XAML text to render; the reply carries any error | `AvaloniaPreviewProcess.UpdateXamlAsync` / `OnConnectionMessage` |
| `FrameMessage` → `FrameReceivedMessage` | A rendered frame (pixels); the acknowledgement that asks for the next one | `AvaloniaPreviewProcess.OnConnectionMessage` |
| `InputEventMessageBase` (`Avalonia.Remote.Protocol.Input`) | Pointer and keyboard input forwarded to the preview | `PreviewPaneViewModel.SendInputAsync` |

## Avalonia.Controls.ColorPicker / Avalonia.Controls.DataGrid — app

Used only from XAML (and their themes are included in `App.axaml`).

| Control | Where |
|---|---|
| `ColorPicker` | `Views/EditorPaneView.axaml` (the colour swatch popup); driven from `Editor/ColorSwatches/ColorSwatchController.cs` |
| `DataGrid` | `Views/SettingsWindow.axaml`, `Views/VisualizerView.axaml` |

## AvaloniaEdit (`Avalonia.AvaloniaEdit`) — app

The text editor. Most of it is used under `Editor/`.

| API | What it does | Where we call it |
|---|---|---|
| `TextDocument` (`Text`, `TextLength`, `LineCount`) | The text model behind each open file | `Models/OpenDocument.cs` (one per open file) and everywhere in `Editor/` |
| `TextDocument.Changing` / `Changed` | Raised before or after each edit, with offset and removed/inserted text | `DocumentSync.OnChanging` (before: the LSP range must be in pre-edit coordinates), `EditorContext.OnDocumentChanged`, `PreviewPaneViewModel.OnDocumentChanged` |
| `TextDocument.Insert` / `Replace` | Edits the text | `TypingController` (auto-close), `LanguageSessionService.ApplyTextEdits`, `OpenDocument.ReloadFrom`, completion data classes |
| `TextDocument.RunUpdate()` | Groups several edits into one undo step | `TypingController.TryMakeNamespaceFileScoped`, `Editor/Commenting/CommentController.cs`, `Editor/Indentation/IndentationStrategies.cs`, `Editor/CodeCompletion/LspCompletionData.cs`, `ViewModels/TerminalViewModel.cs` |
| `TextDocument.GetLocation(offset)` | Offset → 1-based line and column | `DocumentSync`, `CompletionController`, `MainWindowViewModel.UpdateEditorStatus`, hover, signature help, code actions, navigation |
| `GetLineByNumber` / `GetLineByOffset` | A `DocumentLine` (offset, length) | `OpenDocument.ToOffset`, `ToggleBreakpoint`, `NavigationService.ToDebugLocation` / `ToSearchResult` |
| `UndoStack.MarkAsOriginalFile()` / `IsOriginalFile` | Marks "this is what's saved"; dirty = not at that point | `OpenDocument` (constructor, `Save`, `ReloadFrom`, `IsDirty`) |
| `TextDocument.CreateAnchor(offset)` (+ `SurviveDeletion`) | A position that moves with edits | `OpenDocument.ToggleBreakpoint`, code lens, colour swatches, snippet fields |
| `TextSegmentCollection<T>` | Ranges that move with edits, quick to look up by offset | `OpenDocument.Diagnostics`, `DiagnosticRenderer`, `SemanticColorizer`, `InlayHintGenerator` |
| `TextEditor.Document`, `CaretOffset`, `IsReadOnly`, `Options.*`, `ShowLineNumbers`, `FontFamily`, `FontSize` | Editor state and settings | `EditorPaneView.SetDocument` / `ApplyEditorSettings` / `Initialize` |
| `TextEditor.Select`, `ScrollTo(line, col)`, `Undo`, `Redo`, `Cut`, `Copy`, `Paste`, `SelectAll` | Editor actions | `EditorPaneView.ApplyPendingReveal`, `EditorPaneView.Receive(EditorActionMessage)` |
| `ApplicationCommands.Copy/Cut/Paste/SelectAll.Execute(null, TextArea)` | The same actions from the right-click menu | `EditorPaneView.OnCopyClick` etc. |
| `TextArea.TextEntering` / `TextEntered` | A typed character, before or after it goes in | `TypingController` (both), `CompletionController.ShowSnippetsOnly` |
| `TextArea.Caret.PositionChanged` | The caret moved | `EditorPaneView.Initialize` |
| `TextArea.LeftMargins` + `AbstractMargin` | Gutter columns | `BreakpointMargin`, `CodeActionMargin`, `FoldingCaretMargin`, `DiffLineNumberMargin` |
| `TextView.BackgroundRenderers` + `IBackgroundRenderer` | Draws behind the text (squiggles, highlights) | `DiagnosticRenderer`, `CurrentLineRenderer`, `SelectionRenderer`, `OccurrenceRenderer`, `CurrentStatementRenderer`, `DiffLineRenderer`, `SnippetFieldElement` |
| `DocumentColorizingTransformer` | Recolours text runs | `Editor/Highlighting/SemanticColorizer.cs` (semantic tokens over TextMate) |
| `VisualLineElementGenerator` + `InlineObjectElement` | Inserts controls or extra text into a line | `CodeLensGenerator`, `InlayHintGenerator`, `ColorSwatchGenerator`, `DebugInlineValueGenerator` |
| `TextView.InvalidateLayer(KnownLayer.X)` / `Redraw()` | Redraws a layer | `EditorPaneView.Receive(DiagnosticsChangedMessage)`, `ApplyEditorSettings`, and the controllers that draw |
| `TextView.GetVisualLine` / `GetPosition` / `GetVisualPosition` | Screen point ↔ document position | hover, margins, `OverviewRuler`, `WhitespaceSelectionController` |
| `CompletionWindow` (`StartOffset`, `CompletionList.CompletionData`, `SelectItem`, `RequestInsertion`, `Show`, `Closed`) + `ICompletionData` | The completion popup | `CompletionController`; data in `XamlEngineCompletionData`, `LspCompletionData`, `SnippetCompletionData` |
| `OverloadInsightWindow` (`Provider`, `Show`) + `IOverloadProvider` | The parameter-info popup | `SignatureHelpController`, `SignatureOverloadProvider` |
| `FoldingManager.Install` / `Uninstall` / `UpdateFoldings`, `XmlFoldingStrategy` | Code folding | `Editor/Folding/FoldingController.cs` (XML strategy for AXAML/XML; our own `CSharpFoldingStrategy` for C#) |
| `IIndentationStrategy` | What Enter indents to | `Editor/Indentation/IndentationStrategies.cs` |

`ScrollToOffset(h, v)` looks like an AvaloniaEdit method but is our own extension method in `Editor/TextEditorScrolling.cs`.

## AvaloniaEdit.TextMate — app

| API | What it does | Where we call it |
|---|---|---|
| `TextEditor.InstallTextMate(registryOptions)` → `TextMate.Installation` | Turns on TextMate highlighting for an editor | `EditorPaneView.Initialize`, `Views/DiffView.axaml.cs` |
| `Installation.SetGrammar(scope)` | Picks the grammar (`source.cs`, our AXAML scope...) | `TextMateController.SetGrammarForFile`, `DiffView` |
| `Installation.SetTheme(rawTheme)` | Applies a colour theme | `EditorPaneView.ApplyTheme`, `DiffView` |
| `Installation.AppliedTheme` / `TryGetThemeColor(key, out color)` | After a theme is applied: read its UI colours (`editor.background`...) | `Editor/Highlighting/TextMateController.cs` |
| `Installation.Dispose()` | Removes TextMate from the editor | `EditorPaneView.Shutdown` |

## TextMateSharp / TextMateSharp.Grammars — app and Core

| API | What it does | Where we call it |
|---|---|---|
| `new RegistryOptions(ThemeName.DarkPlus)` | The built-in grammars and themes | `AppServices.Build` (singleton) |
| `RegistryOptions.GetLanguageByExtension` / `GetScopeByLanguageId` | File extension → grammar scope | `EditorGrammars.ScopeForFile` |
| `RegistryOptions.LoadTheme(ThemeName)` / `GetTheme` / `GetInjections` / `GetDefaultTheme` | Built-in themes and grammar injections | `Models/EditorTheme.cs`, `EditorGrammars` (which implements `IRegistryOptions` and passes these through) |
| `IRegistryOptions` | What TextMate asks for grammars and themes | Implemented by `Editor/Highlighting/EditorGrammars.cs` (adds our AXAML grammar and colour layers) |
| `GrammarReader.ReadGrammarSync` / `ThemeReader.ReadThemeSync` | Parse a grammar or theme JSON file | `EditorGrammars` (our AXAML grammar, colour layers), `Models/EditorTheme.cs` (bundled themes) |
| `Theme.CreateFromRawTheme(raw, registry)` + `GetGuiColorDictionary()` | Resolves a theme so colours can be looked up by scope | `Editor/Highlighting/SemanticTokenColors.cs` |
| `ThemeName` enum | The names of the built-in themes | `Core/Services/Settings/ThemeIds.cs`, `Core/Models/EditorSettings.cs`, `Views/ThemeVariants.cs` |

## CommunityToolkit.Mvvm — app

| API | What it does | Where we use it |
|---|---|---|
| `ObservableObject` | Base class that raises `PropertyChanged` | Most view models (`MainWindowViewModel`, `WorkspaceViewModel`, `RunViewModel`...) |
| `[ObservableProperty] public partial T Name { get; set; }` | Source-generates a property that raises `PropertyChanged`, plus `OnNameChanged` hooks | e.g. `MainWindowViewModel.StatusText`, `RunViewModel.IsRunning`, `DocumentDockable.PreviewMode` |
| `[NotifyCanExecuteChangedFor(nameof(XCommand))]` | Re-checks a command's `CanExecute` when the property changes | `RunViewModel.IsDebugStopped` |
| `[RelayCommand]` (and `CanExecute = nameof(...)`) | Source-generates an `XCommand` property from method `X` (async methods give an `IAsyncRelayCommand`) | Every view model and `Commands/DocumentCommands.cs` |
| `RelayCommand` / `RelayCommand<T>` / `IRelayCommand` (made by hand) | A command from a lambda | `Commands/EditorCommands.cs` (`Make`), `ViewModels/DocumentDockable.cs` |
| `IAsyncRelayCommand.ExecuteAsync`, `NotifyCanExecuteChanged` | Runs a command and awaits it; re-checks `CanExecute` | `RunViewModel.StartOrContinueDebuggingAsync`, `OnIsDebuggingChanged` |
| `WeakReferenceMessenger.Default` (as `IMessenger`) | App-wide message bus that holds recipients weakly | Registered in `AppServices.Build` |
| `IRecipient<TMessage>` + `messenger.RegisterAll(this)` | Receive messages of a type | `MainWindowViewModel`, `DockLayoutViewModel`, `EditorPaneView`, `NavigationService`, `DebugSnapshotTracker`, `ProblemsViewModel`, `SearchViewModel`, `WatchViewModel`, `VisualizerManager`, `WorkspaceViewModel` |
| `messenger.Register<TRecipient, TMessage>(this, handler)` | Receive without implementing `IRecipient` | `PreviewSessions`, `XamlCacheKeeper` |
| `messenger.Send(message)` | Deliver to every registered recipient, synchronously | Throughout (see the message table in [architecture.md](architecture.md)) |
| `messenger.UnregisterAll(this)` | Stop receiving | `EditorPaneView.Shutdown`, `PreviewSessions.Dispose` |

## Dock (`Dock.Avalonia`, `Dock.Model.Mvvm`, `Dock.Avalonia.Themes.Fluent`) — app

| API | What it does | Where we call it |
|---|---|---|
| `Factory` (base class) | Creates and changes the dock model | `ViewModels/DockFactory.cs` subclasses it |
| `CreateRootDock()`, `CreateList<IDockable>()`, `new DocumentDock`, `new ToolDock`, `new ProportionalDock`, `new ProportionalDockSplitter` | Build the layout tree | `DockFactory.CreateLayout` |
| `Document` / `Tool` (`Dock.Model.Mvvm.Controls`) | Base classes for a tab | `DocumentDockable`, `PageDockable`, `ToolDockable` |
| `InitLayout(root)` | Wires owners and factory into a new layout | `DockLayoutViewModel` constructor |
| `AddDockable`, `InsertDockable`, `RemoveDockable(d, collapse)` | Add, insert or remove a tab or dock | `DockLayoutViewModel.OnDocumentsChanged` / `OpenPage` / `MoveTabsHome`, `DockFactory.PutBack` / `ShowBottomPanel` / `ShowExplorerColumn` / `RemoveTool` |
| `SetActiveDockable`, `SetFocusedDockable(dock, d)` | Bring a tab to the front and focus it | `DockLayoutViewModel.OnActiveDocumentChanged`, `ShowTool`, `Activate` |
| `SplitToWindow(dock, d, x, y, w, h)`, `FloatDockable(d)` | Move a tab into a floating window | `DockFactory.FloatTool`, `DockFactory.PutBack` |
| `CreateWindowFrom(d)` (override) | Makes the floating window model | `DockFactory.CreateWindowFrom` (sets the title) |
| `FindRoot(d, predicate)`, `RemoveWindow(window)` | Find a tab's root or window; remove a window | `DockLayoutViewModel.ShowTool`, `CaptureLayout`, `MoveTabsHome` |
| `IDockWindow.Save()`, `.Exit()`, `.SetActive()`, `X/Y/Width/Height` | Read a floating window's bounds; close or focus it | `DockLayoutViewModel.MoveTabsHome`, `CaptureLayout`, `CloseFloatingWindows`, `ShowTool` |
| Events: `ActiveDockableChanged`, `FocusedDockableChanged`, `GlobalDockTrackingChanged`, `DockableClosing` (cancellable), `DockableClosed`, `WindowClosing` (cancellable) | Layout changes made by the user | `DockLayoutViewModel` constructor |
| `DefaultHostWindowLocator`, `HostWindowLocator` | Which window class hosts floating tabs | `MainWindow` constructor (our `CreateFloatingWindow`) |
| `HostWindow` (`ToolChromeControlsWholeWindow`) | The floating window | `MainWindow.CreateFloatingWindow` |
| `DockControl` (XAML), `DockFluentTheme` (XAML) | Shows the layout; its theme | `Views/MainWindow.axaml`, `App.axaml` |

## Markdig — app

| API | What it does | Where we call it |
|---|---|---|
| `new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()` | A parser with tables, task lists, etc. turned on | `Views/MarkdownRenderer.cs`, `Editor/Hover/HoverContent.cs` |
| `Markdown.Parse(text, pipeline)` → `MarkdownDocument` | Markdown → syntax tree | `MarkdownRenderer.Render`, `HoverContent.FromMarkdown` |
| `Markdown.ToPlainText(text, pipeline)` | Markdown → plain text | `HoverContent.FromMarkdown` |
| `Markdig.Syntax` / `Markdig.Syntax.Inlines` types (`HeadingBlock`, `ParagraphBlock`, `ContainerInline`, `LinkInline`, `TaskList`...) | The syntax tree we walk to build Avalonia controls | `MarkdownRenderer` (`RenderBlock`, `TextFor`, `AddInlines`); used by `Views/MarkdownPreviewView.axaml.cs` |

## Microsoft.Extensions.DependencyInjection — app

| API | What it does | Where we call it |
|---|---|---|
| `new ServiceCollection()`, `AddSingleton<TService, TImpl>()`, `AddSingleton(factory)`, `AddSingleton(instance)` | Register services | `AppServices.Build` |
| `BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild, ValidateScopes })` | Build the container and check every registration up front | `AppServices.Build` |
| `GetRequiredService<T>()` | Resolve a service | `App.OnFrameworkInitializationCompleted`, `App.Start`, `App.UseDockViews` |
| `ServiceProvider.Dispose()` | Disposes every `IDisposable` singleton it created | `App` (`desktop.Exit`) |

## Xaml.Behaviors.Avalonia — app

| API | What it does | Where we call it |
|---|---|---|
| `Behavior<T>` (`OnAttached`, `OnDetaching`, `AssociatedObject`) | Attachable behaviour for a control | `Behaviors/FollowEndBehavior.cs` (keeps a `ScrollViewer` at the bottom) |
| `Interaction.Behaviors` (XAML) | Attaches it | `Views/OutputView.axaml`, `Views/ConsoleView.axaml`, `Views/TestExplorerView.axaml` |

## SkiaSharp (`SkiaSharp.NativeAssets.Linux.NoDependencies`; the managed `SkiaSharp` comes in through `Avalonia.Desktop` → `Avalonia.Skia`) — app

| API | What it does | Where we call it |
|---|---|---|
| `new SKImageInfo(w, h, colorType, alphaType)` + `SKBitmap.InstallPixels(info, ptr, rowBytes, ...)` | Wraps pixels we already hold, without copying | `Views/SkBitmapImage.cs` (preview frames) |
| `SKBitmap.Decode(bytes)` | Decodes PNG, JPEG and other image formats | `SkBitmapImage(byte[])` (the image viewer) |
| `SKImage.FromBitmap(bitmap)` | An immutable image to draw | `SkBitmapImage` |
| `SKCanvas.DrawImage(image, src, dst, new SKSamplingOptions(SKCubicResampler.Mitchell))` | Draws it scaled | `Views/SkBitmapDrawOperation.Render` |
| `SKColorType`, `SKAlphaType` | Pixel format enums | `AvaloniaPreviewProcess` (maps the preview's `PixelFormat`), `Models/RawImageData.cs` |

## ClosedXML — app

| API | What it does | Where we call it |
|---|---|---|
| `new XLWorkbook()`, `Worksheets.Add(name)` | A new workbook and sheet | `Services/Documents/ExcelService.OpenAsExcel` |
| `sheet.Cell(r, c)` (`.Value`, `.Style.Font.Bold`, `.Style.DateFormat.Format`) | Writes and formats a cell | `ExcelService` (`OpenAsExcel`, `WriteCell`) |
| `sheet.Range(...).SetAutoFilter()`, `SheetView.FreezeRows(1)`, `Columns(...).AdjustToContents(...)` | Filter, frozen header, column widths | `ExcelService.OpenAsExcel` |
| `workbook.SaveAs(path)` | Writes the `.xlsx` | `ExcelService.OpenAsExcel` |

Called from `ViewModels/VisualizerDockable.cs` (the debugger table visualizer's "Open in Excel").

---

## DiffPlex — Core

| API | What it does | Where we call it |
|---|---|---|
| `SideBySideDiffBuilder.Diff(left, right, ignoreWhiteSpace: false)` → `SideBySideDiffModel` | A line diff laid out as two columns | `Core/Services/Git/SideBySideDiff.cs` (`Compare`), called from `ViewModels/DiffDockable.cs` |
| `SideBySideDiffModel.OldText` / `NewText` → `DiffPiece` (`Position`, `Text`, `Type`) | Each column's lines | `SideBySideDiff.Lines` |
| `ChangeType` (`Unchanged`, `Modified`, `Imaginary`; anything else is treated as the side's own change) | What each line is | `SideBySideDiff.Lines` (mapped to our `DiffLineKind`; `Imaginary` → `Filler`) |

## dnlib — Core

Reads assemblies (types, properties, attributes) without loading them into the process. It is the metadata source for
AXAML completion.

| API | What it does | Where we call it |
|---|---|---|
| `new AssemblyResolver { EnableTypeDefCache, UseGAC }`, `PreSearchPaths`, `AddToCache`, `Clear` | Finds referenced assemblies | `Core/CompletionEngine/DnlibMetadataProvider.cs` |
| `new Resolver(asmResolver)`, `new ModuleContext(asmResolver, resolver)` | Resolves type references across assemblies | `DnlibMetadataProvider` |
| `AssemblyDef.Load(bytes, new ModuleCreationOptions(ctx) { TryToLoadPdbFromDisk = false })` | Loads one assembly's metadata | `DnlibMetadataProvider.LoadAssemblies` |
| `ITypeDefOrRef.ResolveTypeDef()`, `TypeSig.ToTypeDefOrRef()`, `GenericInstSig` | Follows a reference to its definition; generic type arguments | `DnlibMetadataProvider.GetTypeDef`, `Core/CompletionEngine/AssemblyWrapper.cs` |
| `TypeDef.Properties` / `Events` / `Methods` / `Fields` / `CustomAttributes`, `AssemblyDef.CustomAttributes` | What a type declares | `AssemblyWrapper.cs` (wraps them as `IAssemblyInformation` and the related interfaces for the engine) |
| `AssemblyDef` (`InternalsVisibleTo` attributes) | Which assemblies can see internals | `Core/CompletionEngine/SnlibExtension.cs` (`DnlibExtension.GetVisibleTo`) |

Reached through `XamlCompletionService.GetMetadataAsync` → `MetadataReader.GetForTargetAssembly` (`new MetadataReader(new DnlibMetadataProvider())`).

## ICSharpCode.Decompiler — Core (and one `catch` in the app)

| API | What it does | Where we call it |
|---|---|---|
| `new CSharpDecompiler(assemblyPath, new DecompilerSettings { FileScopedNamespaces = false })` | A decompiler for one assembly | `Core/Services/Xaml/XamlDecompilationService.cs` |
| `decompiler.DocumentationProvider = new XmlDocumentationProvider(xmlPath)` | Puts XML doc comments into the output | `XamlDecompilationService` |
| `decompiler.DecompileTypeAsString(new FullTypeName(name))` | C# source for one type | `XamlDecompilationService` |
| `DecompilerException` | Thrown when decompiling fails | Caught in `Editor/Navigation/NavigationCommands.GoToXamlDefinitionAsync` |

Reached from F12 in AXAML ([flows.md §7](flows.md#7-go-to-definition-f12)) through `XamlLanguageService.DecompileAsync` / `DecompileTypeAsync`.

## Microsoft.Diagnostics.Runtime (ClrMD) — Core

Reads objects from the debuggee's memory. The table visualizer uses it to show large collections quickly, without one
debugger request per row.

| API | What it does | Where we call it |
|---|---|---|
| `DataTarget.AttachToProcess(pid, suspend: false)` | Attaches to a running process for reading | `Core/Services/Debugging/HeapTables.TryRead` |
| `DataTarget.ClrVersions`, `ClrInfo.CreateRuntime()` / `CreateRuntime(dac, ignoreMismatch: true)` | The .NET runtime in that process | `HeapTables.TryRead`, `CreateRuntime` |
| `ClrRuntime.Heap.EnumerateObjects()` | Every object on the managed heap | `HeapTables.TryRead` (finds the collection by its fingerprint) |
| `ClrObject.Type`, `AsArray()`, `ReadField<T>`, `ReadObjectField`, `ReadValueTypeField`; `ClrValueType` (same); `ClrType.GetFieldByName`; `ClrArray.Rank`, `GetObjectValue`, `GetStructValue` | Read fields and array elements | `HeapTables` (`Matches`, `InnerList`, `Count`, `Read`, `Items`, the `Slot` helpers) |

Called from `Core/Services/Debugging/DebugValueProvider.Table.cs` (`Task.Run(() => HeapTables.TryRead(...))`).

---

## Microsoft.Build / Microsoft.Build.Locator — MSBuild host only

The host references `Microsoft.Build` with `ExcludeAssets="runtime"`. At run time `MSBuildLocator` loads the installed
SDK's copy instead.

| API | What it does | Where we call it |
|---|---|---|
| `MSBuildLocator.RegisterDefaults()` | Points assembly loading at the installed .NET SDK's MSBuild. Must run before any `Microsoft.Build` type is touched | `Host/Program.cs`, first statement |
| `new ProjectCollection(globalProperties)` | An evaluation context (`Configuration=Release`...) | `Host/Program.cs` `HandleRequest`, `Host/ItemProperties.cs`, `Host/ItemPaths.cs` |
| `Project.FromFile(path, new ProjectOptions { ProjectCollection })` | Evaluates a project | `HandleRequest` |
| `new Project(root, null, null, collection)` | Evaluates from an already-open project XML | `ItemProperties.Load` |
| `project.Items`, `project.GetItems(type)`, `ProjectItem.ItemType`, `EvaluatedInclude`, `GetMetadataValue(name)` | Evaluated items (files, references, `AvailableItemName`...) | `HandleRequest`, `Dependencies`, `ItemProperties` |
| `project.GetPropertyValue(name)` | Evaluated property (`AssemblyName`, `OutputPath`, `TargetFramework(s)`...) | `HandleRequest` |
| `project.Imports` → `ImportedProject.FullPath`, `project.DirectoryPath` | Imported files (for cache invalidation) | `LocalImports` |
| `project.ReevaluateIfNecessary()` | Evaluates again after the XML was edited | `ItemProperties` (`Set`, `ChangeType`, `SetMetadata`) |
| `ProjectRootElement.Open(path, collection, preserveFormatting: true)` | The project XML, for editing with formatting kept | `ItemProperties.Load`, `ItemPaths.Move` |
| `root.Items`, `root.ItemGroups`, `CreateItemElement`, `AddItem`, `AddItemGroup`, `AppendChild`, `RemoveChild`, `ProjectItemElement.Include/Update/Remove`, `AddMetadata`, `root.Save()` | Edit and save the project file | `ItemProperties` (build action and metadata), `ItemPaths.Move` (rename, move or delete) |
