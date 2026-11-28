namespace AXamlIDE.Services.Typing;

/// <summary>
/// The characters that, typed over a selection, put themselves around it instead of replacing it (as Visual Studio,
/// VS Code and Rider do): quotes and opening brackets. '&lt;' isn't one: over a selected '&gt;' it's a replacement
/// (a &gt; b becoming a &lt; b), and in AXAML it starts a tag.
/// </summary>
public static class SurroundPairs
{
    /// <summary>The character that goes after the selection when <paramref name="typed"/> goes before it, or null.</summary>
    public static char? ClosingFor(char typed) => typed switch
    {
        '"' => '"',
        '\'' => '\'',
        '`' => '`',
        '(' => ')',
        '[' => ']',
        '{' => '}',
        _ => null
    };
}
