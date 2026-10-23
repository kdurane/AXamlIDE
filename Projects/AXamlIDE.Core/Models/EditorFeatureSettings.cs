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

    // ---- IntelliSense

    /// <summary>The completion list opens as a name is typed (else: Ctrl+Space, or after a '.'). VS: "Show completion list after a character is typed".</summary>
    public bool CompletionWhileTyping { get; init; } = true;

    /// <summary>Completion offers types from namespaces that aren't imported yet, adding the using.</summary>
    public bool CompletionFromUnimportedNamespaces { get; init; } = true;

    /// <summary>Parameter info opens on '(' and ','. (Ctrl+Shift+Space still opens it.)</summary>
    public bool ParameterInfoWhileTyping { get; init; } = true;

    /// <summary>Resting the pointer on a name shows what it is (Quick Info). Errors under the pointer still show.</summary>
    public bool QuickInfoOnHover { get; init; } = true;

    // ---- Code display

    /// <summary>Names are coloured by what they are (class, parameter, local...), not just by syntax.</summary>
    public bool SemanticHighlighting { get; init; } = true;

    /// <summary>"3 references" above declarations (C#).</summary>
    public bool CodeLens { get; init; } = true;

    /// <summary>Parameter names before literal arguments: Process(force: true).</summary>
    public bool InlayParameterHints { get; init; } = true;

    /// <summary>The types behind var, lambda parameters and new().</summary>
    public bool InlayTypeHints { get; init; } = true;

    /// <summary>Other places the selected text appears are highlighted.</summary>
    public bool HighlightSelectionOccurrences { get; init; } = true;

    /// <summary>A swatch beside colour values, which opens a colour picker.</summary>
    public bool ColorSwatches { get; init; } = true;

    // ---- Debugging

    /// <summary>"name = value" at the end of the lines around the current statement while paused.</summary>
    public bool InlineDebugValues { get; init; } = true;
}
