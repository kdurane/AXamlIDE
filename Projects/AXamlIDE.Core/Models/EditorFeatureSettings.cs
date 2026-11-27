namespace AXamlIDE.Models;

/// <summary>
/// The editor's helpers that people like to switch off: things that type for you, pop up, or add to what's shown.
/// All on by default, as in Visual Studio and Rider. Errors and warnings aren't here on purpose: they always show.
///
/// Saved as it is in settings.json ("Features"); one missing from the file (added in a later version) keeps its default.
/// </summary>
public sealed record EditorFeatureSettings
{
    // ---- Typing

    /// <summary>Typing { adds the } (C#, and in an AXAML attribute value). VS: "Automatic brace completion".</summary>
    public bool AutoCloseBraces { get; init; } = true;

    /// <summary>Typing the > of an AXAML start tag adds its end tag. VS: "Auto insert close tag".</summary>
    public bool AutoCloseXamlTags { get; init; } = true;

    /// <summary>Typing ; after "namespace X" makes the namespace file-scoped.</summary>
    public bool FileScopedNamespaceOnSemicolon { get; init; } = true;

    // ---- Formatting

    /// <summary>Typing } formats the block it closes (the language server's on-type formatting). VS: "Automatically format completed block on }".</summary>
    public bool FormatBlockOnBrace { get; init; } = true;

    /// <summary>Typing ; formats the statement it ends. VS: "Automatically format completed statement on ;".</summary>
    public bool FormatStatementOnSemicolon { get; init; } = true;

    /// <summary>Format-on-save includes AXAML (when format-on-save is on). Off: AXAML is formatted only by Format Document.</summary>
    public bool FormatXamlOnSave { get; init; } = true;

    /// <summary>Spaces per indent level when formatting AXAML. A project's Settings.XamlStyler "IndentSize" wins over it.</summary>
    public int XamlIndentSize { get; init; } = 4;

    // ---- IntelliSense

    /// <summary>The completion list opens as a name is typed (else: Ctrl+Space, or after a '.'). VS: "Show completion list after a character is typed".</summary>
    public bool CompletionWhileTyping { get; init; } = true;

    /// <summary>Completion offers types from namespaces that aren't imported yet, adding the using.</summary>
    public bool CompletionFromUnimportedNamespaces { get; init; } = true;

    /// <summary>Parameter info opens on '(' and ','. (Ctrl+Shift+Space still opens it.)</summary>
    public bool ParameterInfoWhileTyping { get; init; } = true;

    /// <summary>
    /// Snippets (class, prop, ctor...) are offered in the completion list. Rider: "Show live templates in completion";
    /// VS Code: editor.snippetSuggestions. Insert Snippet (Ctrl+J) still lists them.
    /// </summary>
    public bool SnippetsInCompletion { get; init; } = true;

    /// <summary>Tab right after a snippet's shortcut ("prop" + Tab) expands it. VS Code: editor.tabCompletion.</summary>
    public bool TabExpandsSnippets { get; init; } = true;

    /// <summary>Resting the pointer on a name shows what it is (Quick Info). Errors under the pointer still show.</summary>
    public bool QuickInfoOnHover { get; init; } = true;

    // ---- Code display

    /// <summary>Names are coloured by what they are (class, parameter, local...), not just by syntax.</summary>
    public bool SemanticHighlighting { get; init; } = true;

    /// <summary>"3 references" above declarations (C#).</summary>
    public bool CodeLens { get; init; } = true;

    /// <summary>With CodeLens: who last changed each member, and when ("Kieron, 28 days ago"), from Git, as VS shows.</summary>
    public bool CodeLensAuthor { get; init; } = true;

    /// <summary>Parameter names before literal arguments: Process(force: true).</summary>
    public bool InlayParameterHints { get; init; } = true;

    /// <summary>The types behind var, lambda parameters and new().</summary>
    public bool InlayTypeHints { get; init; } = true;

    /// <summary>Other places the selected text appears are highlighted.</summary>
    public bool HighlightSelectionOccurrences { get; init; } = true;

    /// <summary>A swatch beside colour values, which opens a colour picker.</summary>
    public bool ColorSwatches { get; init; } = true;

    // ---- Code analysis

    /// <summary>
    /// Every file in the solution is analysed, not just the open ones: the Problems list and the status bar show the
    /// errors in closed files too. Off by default, as in Visual Studio and Rider ("solution-wide analysis"): on a big
    /// solution it keeps the language server busy and uses a lot of memory.
    /// </summary>
    public bool SolutionWideAnalysis { get; init; }

    // ---- Solution Explorer

    /// <summary>
    /// The explorer selects the file of the tab being edited, opening folders to show it. VS: "Track Active Item in
    /// Solution Explorer", off by default there too. Off, the header's Sync with Active Document does it on demand.
    /// </summary>
    public bool TrackActiveItem { get; init; }

    // ---- Debugging

    /// <summary>"name = value" at the end of the lines around the current statement while paused.</summary>
    public bool InlineDebugValues { get; init; } = true;

    /// <summary>
    /// Just My Code, as in Visual Studio: stepping and stops stay in the project's own code. Off, Step Into goes into
    /// the framework and packages too, and Call Stack lists their frames; with SharpDbg their code is decompiled to
    /// read and step through (DecompiledSources). Takes effect when debugging next starts.
    /// </summary>
    public bool JustMyCode { get; init; } = true;

    /// <summary>Break as soon as any exception is thrown; off: only when user code doesn't handle it (VS's default).</summary>
    public bool BreakOnAllExceptions { get; init; }

    /// <summary>
    /// Keep each workspace's breakpoints when the IDE closes (in its .axamlide/workspace.json), as Visual Studio and
    /// VS Code do. Off, they last until the workspace is closed. Either way a closed file keeps its breakpoints.
    /// </summary>
    public bool RememberBreakpoints { get; init; } = true;

    /// <summary>Which debugger to use: Automatic (SharpDbg when a .NET 11 runtime is installed, otherwise netcoredbg), or always one of them.</summary>
    public Services.Debugging.DebuggerChoice Debugger { get; init; } = Services.Debugging.DebuggerChoice.Automatic;

    // ---- Claude

    /// <summary>
    /// The Claude panel's conversations can use the IDE's own tools (render_xaml, get_problems...: Services/Mcp). Off,
    /// their calls are turned away straight away, in a conversation that's going on too. Claude Code in a terminal
    /// isn't affected (it's set up there, with claude mcp).
    /// </summary>
    public bool ClaudeIdeTools { get; init; } = true;
}
