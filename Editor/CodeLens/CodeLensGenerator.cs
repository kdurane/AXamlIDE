using System;
using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.CodeLens;

/// <summary>
/// A resolved code lens, anchored to its symbol (the anchor moves with edits until the lenses are fetched again).
/// </summary>
internal sealed record CodeLensItem(TextAnchor Anchor, string Title, LspCodeLens Lens);

/// <summary>
/// Puts each code lens (<see cref="CodeLensControl"/>) into the line it belongs above: the declaration's line, or
/// the first of the attribute lines above it ([ObservableProperty]...), at that line's indentation, as Visual
/// Studio does. Set <see cref="Items"/> and redraw the text view to change them.
/// </summary>
internal sealed class CodeLensGenerator(Func<CodeLensItem, CodeLensControl> createControl) : VisualLineElementGenerator
{
    public IReadOnlyList<CodeLensItem> Items { get; set; } = [];

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var document = CurrentContext.Document;
        var best = -1;
        foreach (var item in Items)
        {
            if (item.Anchor.IsDeleted)
                continue;
            var offset = LensOffset(document, item);
            if (offset >= startOffset && (best < 0 || offset < best))
                best = offset;
        }
        return best;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var document = CurrentContext.Document;
        // Several lenses on one line (rare): Roslyn gives one per member, so show the first.
        var item = Items.FirstOrDefault(lens => !lens.Anchor.IsDeleted && LensOffset(document, lens) == offset);
        return item is null ? null : new InlineObjectElement(0, createControl(item));
    }

    /// <summary>Where the lens goes: after the indentation of the declaration's first line (attributes included).</summary>
    public static int LensOffset(TextDocument document, CodeLensItem item)
    {
        var line = document.GetLineByNumber(item.Anchor.Line);
        while (line.PreviousLine is { } previous && IsAttributeLine(document, previous))
            line = previous;
        return TextUtilities.GetLeadingWhitespace(document, line).EndOffset;
    }

    private static bool IsAttributeLine(TextDocument document, DocumentLine line)
        => document.GetText(line).TrimStart().StartsWith('[');
}
