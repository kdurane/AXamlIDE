using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Models;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.CodeActions;

/// <summary>
/// Quick actions (lightbulb margin, Ctrl+., the hover card's Quick Fix... link).
/// Inserts its margin at index 1, so construct it after the breakpoint margin (index 0) is added.
/// </summary>
public sealed class CodeActionController
{
    private readonly EditorContext _ctx;
    private readonly WorkspaceEditService _edits;
    private readonly CodeActionMargin _margin;

    /// <summary>Raised just before the flyout opens (HoverController hides its tooltip).</summary>
    public event Action? FlyoutOpening;

    public CodeActionController(EditorContext ctx, WorkspaceEditService edits)
    {
        _ctx = ctx;
        _edits = edits;

        _margin = new CodeActionMargin(
            (line, anchor) => ShowCodeActionsAsync(line),
            () => _ctx.Languages.ClientFor(_ctx.Active)?.SupportsCodeActions == true);
        _ctx.Editor.TextArea.LeftMargins.Insert(1, _margin);

        _ctx.Attached += doc => _margin.SetDocument(doc);
    }

    /// <summary>Called when the active document's diagnostics change, to redraw the lightbulbs.</summary>
    public void RefreshMargin(OpenDocument document) => _margin.SetDocument(document);

    public async Task<bool> ShowCodeActionsAsync(int? requestedLine = null, string? diagnosticHeader = null)
    {
        var editor = _ctx.Editor;

        if (_ctx.Active is not { } document
            || _ctx.Languages.ConnectionFor(document) is not { Client: var client, Sync: var sync })
        {
            _ctx.Status("Open a document with an active language server to request quick actions.");
            return false;
        }

        if (!client.SupportsCodeActions)
        {
            _ctx.Status("The active language server does not support quick actions.");
            return false;
        }

        try
        {
            sync.Flush();
            var startOffset = editor.SelectionLength > 0
                ? editor.SelectionStart
                : requestedLine is { } lineNumber
                    ? document.Document.GetLineByNumber(lineNumber).Offset
                    : document.Document.GetLineByOffset(editor.CaretOffset).Offset;
            var endOffset = editor.SelectionLength > 0
                ? editor.SelectionStart + editor.SelectionLength
                : requestedLine is { } requestedLineNumber
                    ? document.Document.GetLineByNumber(requestedLineNumber).EndOffset
                    : document.Document.GetLineByOffset(editor.CaretOffset).EndOffset;
            var start = document.Document.GetLocation(startOffset);
            var end = document.Document.GetLocation(endOffset);
            var range = new LspRange(start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1);
            var diagnostics = document.Diagnostics.Select(segment =>
            {
                var diagnosticStart = document.Document.GetLocation(segment.StartOffset);
                var diagnosticEnd = document.Document.GetLocation(segment.EndOffset);
                return new LspDiagnostic(
                    diagnosticStart.Line - 1, diagnosticStart.Column - 1,
                    diagnosticEnd.Line - 1, diagnosticEnd.Column - 1,
                    segment.Severity, segment.Message, segment.RawDiagnostic);
            }).Where(diagnostic => LspHelpers.RangesOverlap(
                range,
                new LspRange(
                    diagnostic.StartLine,
                    diagnostic.StartChar,
                    diagnostic.EndLine,
                    diagnostic.EndChar)))
                .ToArray();
            var actions = await client.CodeActionsAsync(document.Uri, range, diagnostics);
            if (diagnostics.Length > 0)
            {
                actions = [.. actions.Where(action => action.Kind == "quickfix")];
            }
            if (!ReferenceEquals(document, _ctx.Active))
                return false;
            if (actions.Count == 0)
            {
                _ctx.Status("No quick actions are available at the caret or selection.");
                return false;
            }

            var header = diagnosticHeader ?? string.Join(
                Environment.NewLine,
                diagnostics.Select(diagnostic => diagnostic.Message).Distinct(StringComparer.Ordinal));
            FlyoutOpening?.Invoke();
            ShowCodeActionFlyout(actions, client, header, startOffset);
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Status($"Quick action failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The quick-fix list: the diagnostics it's for (in their colour), then one item per fix. A MenuFlyout, so
    /// it's light dismissable and keyboard-driven (it takes focus on purpose: it was asked for).
    /// </summary>
    private void ShowCodeActionFlyout(IReadOnlyList<LspCodeAction> actions, LspClient client, string? diagnosticHeader, int offset)
    {
        var flyout = new MenuFlyout();
        if (!string.IsNullOrWhiteSpace(diagnosticHeader))
        {
            flyout.Items.Add(new MenuItem
            {
                IsEnabled = false,
                Header = new TextBlock
                {
                    Text = diagnosticHeader,
                    Foreground = HoverCard.DiagnosticBrush(LspDiagnosticSeverity.Error),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360
                }
            });
            flyout.Items.Add(new Separator());
        }

        foreach (var action in actions)
            flyout.Items.Add(CreateActionItem(action, client));

        var editor = _ctx.Editor;
        var view = editor.TextArea.TextView;
        var document = editor.Document;
        var line = document.GetLineByOffset(offset);
        var textStart = line.Offset + TextUtilities.GetLeadingWhitespace(document, line).Length;
        var position = view.GetVisualPosition(new TextViewPosition(document.GetLocation(textStart)), VisualYPosition.LineBottom)
                       - view.ScrollOffset;
        var point = view.TranslatePoint(position, editor) ?? default;

        flyout.Placement = PlacementMode.AnchorAndGravity;
        flyout.PlacementAnchor = PopupAnchor.TopLeft;     // measured from the editor's top-left corner...
        flyout.PlacementGravity = PopupGravity.BottomRight; // ...the menu opens down and to the right of that point
        flyout.HorizontalOffset = point.X;
        flyout.VerticalOffset = point.Y;
        flyout.ShowAt(editor);
    }

    /// <summary>
    /// One quick action. Roslyn's groups ("Suppress or configure issues") and fix-alls become submenus, as in
    /// Visual Studio: the client is meant to offer their choices, not send them back to the server.
    /// </summary>
    private MenuItem CreateActionItem(LspCodeAction action, LspClient client)
    {
        var item = NewItem(action.Title, withIcon: true);
        var nested = RoslynCodeActions.Nested(action);
        var fixAllData = RoslynCodeActions.CommandName(action) == RoslynCodeActions.FixAllCommand
            ? RoslynCodeActions.CommandData(action)
            : null;

        if (nested.Count > 0)
            AddNestedItems(item.Items, action.Title, nested, client);
        else if (fixAllData is { } data && RoslynCodeActions.FixAllFlavors(data).Count > 0)
            AddFixAllItems(item.Items, data, action.Title, client);
        else
            item.Click += async (_, _) => await ApplyCodeActionAsync(action, client);
        return item;
    }

    /// <summary>
    /// A group's actions, as submenus following each one's path: "Suppress CA1822" &gt; "in Source",
    /// "Configure CA1822 severity" &gt; "Warning".
    /// </summary>
    private void AddNestedItems(ItemCollection items, string groupTitle, IReadOnlyList<LspCodeAction> nested, LspClient client)
    {
        foreach (var action in nested)
        {
            var path = RoslynCodeActions.MenuPath(action, groupTitle);
            var owner = items;
            foreach (var segment in path.SkipLast(1))
            {
                var submenu = owner.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, segment));
                if (submenu is null)
                {
                    submenu = NewItem(segment, withIcon: false);
                    owner.Add(submenu);
                }
                owner = submenu.Items;
            }

            var data = RoslynCodeActions.CommandData(action) ?? RoslynCodeActions.Data(action);
            if (data is { } fixAll && RoslynCodeActions.FixAllFlavors(fixAll).Count > 0)
            {
                // Its path ends like the single fix's ("in Source"): its title says it's the fix-all ("Fix All: in Source")
                var fixAllItem = NewItem(action.Title, withIcon: false);
                AddFixAllItems(fixAllItem.Items, fixAll, action.Title, client);
                owner.Add(fixAllItem);
            }
            else
            {
                var leaf = NewItem(path[^1], withIcon: false);
                leaf.Click += async (_, _) => await ApplyCodeActionAsync(action, client);
                owner.Add(leaf);
            }
        }
    }

    /// <summary>"Document", "Project", "Solution": one item per scope the fix-all offers.</summary>
    private void AddFixAllItems(ItemCollection items, JsonElement data, string title, LspClient client)
    {
        foreach (var scope in RoslynCodeActions.FixAllFlavors(data))
        {
            var item = NewItem(ScopeLabel(scope), withIcon: false);
            item.Click += async (_, _) => await ApplyFixAllAsync(data, scope, title, client);
            items.Add(item);
        }
    }

    /// <summary>"ContainingMember" → "Containing member".</summary>
    private static string ScopeLabel(string scope)
    {
        var label = new System.Text.StringBuilder();
        foreach (var c in scope)
        {
            if (char.IsUpper(c) && label.Length > 0)
                label.Append(' ').Append(char.ToLowerInvariant(c));
            else
                label.Append(c);
        }
        return label.ToString();
    }

    private static MenuItem NewItem(string text, bool withIcon) => new()
    {
        Tag = text,
        Header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
        Icon = withIcon
            ? new PathIcon
            {
                Data = LightbulbIcon,
                Width = 14,
                Height = 14,
                Foreground = HoverCard.DiagnosticBrush(LspDiagnosticSeverity.Warning)
            }
            : null
    };

    private async Task ApplyFixAllAsync(JsonElement data, string scope, string title, LspClient client)
    {
        try
        {
            var response = await client.ResolveFixAllAsync(data, scope);
            var applied = response.TryGetProperty("edit", out var edit) && _edits.Apply(edit);
            _ctx.Status(applied
                ? $"Applied '{title}' ({scope}). Review and save modified documents."
                : $"'{title}' ({scope}) had nothing to change.");
        }
        catch (Exception ex)
        {
            _ctx.Status($"Quick action failed: {ex.Message}");
        }
    }

    private static readonly Geometry LightbulbIcon = Geometry.Parse(
        "M12 2a7 7 0 0 0-4 12.75V17a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-2.25A7 7 0 0 0 12 2m2 13.5V17h-4v-1.5l-.8-.55a5 5 0 1 1 5.6 0zM10 21h4v1h-4z");

    private async Task ApplyCodeActionAsync(LspCodeAction action, LspClient client)
    {
        try
        {
            var selected = client.SupportsCodeActionResolve
                ? await client.ResolveCodeActionAsync(action)
                : action;
            var didWork = false;
            if (selected.Edit is { } edit && _edits.Apply(edit))
                didWork = true;

            if (selected.Command is { } command)
            {
                if (command.ValueKind != JsonValueKind.Object
                    || !command.TryGetProperty("command", out var commandName)
                    || commandName.GetString() is not { Length: > 0 } name)
                    throw new InvalidOperationException($"Quick action '{selected.Title}' returned an invalid command.");

                var arguments = command.TryGetProperty("arguments", out var values)
                    ? values.Clone()
                    : (JsonElement?)null;

                if (name == RoslynCodeActions.NestedCommand)
                {
                    // Only known once resolved: offer the group's choices where the caret is
                    var flyout = new MenuFlyout();
                    AddNestedItems(flyout.Items, selected.Title, RoslynCodeActions.Nested(selected), client);
                    if (flyout.Items.Count > 0)
                        flyout.ShowAt(_ctx.Editor);
                    return;
                }
                if (name == RoslynCodeActions.FixAllCommand)
                {
                    if (arguments is not { ValueKind: JsonValueKind.Array } args
                        || args.GetArrayLength() == 0)
                    {
                        throw new InvalidOperationException(
                            "Roslyn Fix All did not provide its arguments.");
                    }
                    var response = await client.ResolveFixAllAsync(args[0], "Document");

                    if (response.TryGetProperty("edit", out var fixAllEdit) && _edits.Apply(fixAllEdit))
                        didWork = true;
                }
                else
                {
                    await client.ExecuteCommandAsync(name, arguments);
                    didWork = true;
                }
            }

            _ctx.Status(didWork
                ? $"Applied '{selected.Title}'. Review and save modified documents."
                : $"'{selected.Title}' did not contain an applicable edit or command.");
        }
        catch (Exception ex)
        {
            _ctx.Status($"Quick action failed: {ex.Message}");
        }
    }
}
