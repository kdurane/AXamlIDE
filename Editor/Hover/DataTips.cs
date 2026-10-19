using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using AXamlIDE.Models;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;
using AXamlIDE.ViewModels;
using AXamlIDE.Views;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Editor.Hover;

/// <summary>A DataTip ready to show: its view, the text it's about, where to place it, and its rows.</summary>
/// <param name="AnchorOffset">The start of the word under the pointer (the tip opens under it).</param>
public sealed record DataTip(Control View, (int Start, int End) Range, int AnchorOffset, DebugValueTree Tree);

/// <summary>
/// Visual Studio's DataTips: while the debugger is paused, hovering an expression in C# (a variable, person.Home,
/// people[i].Name) shows its value, expandable like the Locals window, with the magnifier for visualizers and
/// Add Watch on its menu. Shown by HoverController in place of the hover card; when the expression can't be
/// evaluated (a type name, a method) the normal hover card shows instead.
/// </summary>
public sealed class DataTips(DebugSnapshotTracker debug, IMessenger messenger, IClipboardService clipboard)
{
    private const double RowHeight = 22;     // DebugValueTreeView's rows
    private const double MaxHeight = 420;

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    /// <summary>The DataTip for the expression at <paramref name="offset"/>, or null (not paused, nothing to evaluate).</summary>
    public async Task<DataTip?> CreateAsync(OpenDocument document, int offset)
    {
        if (debug.Values is not { } values || document.LanguageId != LanguageIds.CSharp)
            return null;

        var line = document.Document.GetLineByOffset(offset);
        if (DebugExpressionLocator.Find(document.Document.GetText(line), offset - line.Offset) is not { } found)
            return null;

        DebugValue value;
        try
        {
            value = await values.EvaluateAsync(found.Expression, "hover");
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
        if (value.Kind == DebugValueKind.Error || !ReferenceEquals(values, debug.Values))
            return null;

        var tree = new DebugValueTree(messenger, clipboard);
        await tree.SetRootsAsync([value], values, markChanges: false);
        var treeView = new DebugValueTreeView
        {
            DataContext = tree,
            ShowHeader = false,
            ShowTypeColumn = false,
            IsEditable = false,
            NameColumnWidth = Math.Clamp(found.Expression.Length * 8 + 40, 100, 260),
            MinWidth = 320,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        // A virtualizing list measured in a popup doesn't ask for more room as rows are added, so size it from
        // the rows: the tip grows (up to a limit, then scrolls) as it's expanded.
        void Fit() => treeView.Height = Math.Min(tree.Rows.Count * RowHeight + 6, MaxHeight);
        tree.Rows.CollectionChanged += (_, _) => Fit();
        Fit();

        var view = new Border { Classes = { "dataTip" }, Child = treeView };
        var start = line.Offset + found.Start;
        var word = offset;
        while (word > start && IsIdentifierChar(document.Document.GetCharAt(word - 1)))
            word--;
        return new DataTip(view, (start, start + found.Length), word, tree);
    }
}
