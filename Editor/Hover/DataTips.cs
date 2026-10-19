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

/// <summary>A DataTip ready to show: its view and the text it's about.</summary>
public sealed record DataTip(Control View, (int Start, int End) Range);

/// <summary>
/// Visual Studio's DataTips: while the debugger is paused, hovering an expression in C# (a variable, person.Home,
/// people[i].Name) shows its value, expandable like the Locals window, with the magnifier for visualizers and
/// Add Watch on its menu. Shown by HoverController in place of the hover card; when the expression can't be
/// evaluated (a type name, a method) the normal hover card shows instead.
/// </summary>
public sealed class DataTips(DebugSnapshotTracker debug, IMessenger messenger, IClipboardService clipboard)
{
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
        var view = new Border
        {
            Classes = { "dataTip" },
            Child = new DebugValueTreeView
            {
                DataContext = tree,
                ShowHeader = false,
                ShowTypeColumn = false,
                IsEditable = false,
                NameColumnWidth = Math.Clamp(found.Expression.Length * 8 + 40, 100, 260),
                MinWidth = 320,
                MaxWidth = 760,
                MaxHeight = 380,
                HorizontalAlignment = HorizontalAlignment.Left
            }
        };
        return new DataTip(view, (line.Offset + found.Start, line.Offset + found.Start + found.Length));
    }
}
