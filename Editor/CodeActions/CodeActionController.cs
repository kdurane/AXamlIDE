using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AXamlIDE.Editor.Navigation;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.CodeActions;

/// <summary>
/// Quick actions (lightbulb margin, Ctrl+., hover on a diagnostic).
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
            (line, anchor) => ShowCodeActionsAsync(line, anchor),
            () => _ctx.Active?.Client?.SupportsCodeActions == true);
        _ctx.Editor.TextArea.LeftMargins.Insert(1, _margin);

        _ctx.Attached += doc => _margin.SetDocument(doc);
    }

    /// <summary>Called when the active document's diagnostics change, to redraw the lightbulbs.</summary>
    public void RefreshMargin(OpenDocument document) => _margin.SetDocument(document);

    public async Task<bool> ShowCodeActionsAsync(
        int? requestedLine = null,
        Control? anchor = null,
        string? diagnosticHeader = null,
        bool showAtPointer = false)
    {
        var editor = _ctx.Editor;

        if (_ctx.Active is not { Client: { } client, Sync: { } sync } document)
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
            ShowCodeActionFlyout(actions, client, anchor ?? editor, header, true);
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Status($"Quick action failed: {ex.Message}");
            return false;
        }
    }

    private void ShowCodeActionFlyout(IReadOnlyList<LspCodeAction> actions, LspClient client,
        Control anchor, string? diagnosticHeader, bool showAtPointer)
    {
        var flyout = new MenuFlyout();
        if (!string.IsNullOrWhiteSpace(diagnosticHeader))
        {
            flyout.Items.Add(new MenuItem
            {
                IsEnabled = false,
                FontSize = 10,
                Padding = new Thickness(8, 6),
                Header = new TextBlock
                {
                    Text = diagnosticHeader,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.Salmon,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                    HorizontalAlignment = HorizontalAlignment.Left
                }
            });
            flyout.Items.Add(new Separator());
        }

        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                FontSize = 10,
                Padding = new Thickness(8, 4),
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                              {
                                  new PathIcon
                                  {
                                      Data = Geometry.Parse(
                                          "M12 2a7 7 0 0 0-4 12.75V17a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-2.25A7 7 0 0 0 12 2m2 13.5V17h-4v-1.5l-.8-.55a5 5 0 1 1 5.6 0zM10 21h4v1h-4z"),
                                      Width = 14,
                                      Height = 14,
                                      Foreground = Brushes.DarkGoldenrod
                                  },
                                  new TextBlock
                                  {
                                      Text = action.Title,
                                      FontSize = 10,
                                      VerticalAlignment = VerticalAlignment.Center,
                                      TextWrapping = TextWrapping.Wrap
                                  }
                              }
                }
            };
            item.Click += async (_, _) => await ApplyCodeActionAsync(action, client);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor, showAtPointer);
    }

    private async Task ApplyCodeActionAsync(LspCodeAction action, LspClient client)
    {
        try
        {
            var selected = client.SupportsCodeActionResolve
                ? await client.ResolveCodeActionAsync(action)
                : action;
            var didWork = false;
            if (selected.Edit is { } edit)
            {
                var replacements = LspHelpers.ParseWorkspaceEdit(edit);
                if (replacements.Count > 0)
                {
                    _edits.Apply(replacements);
                    didWork = true;
                }
            }

            if (selected.Command is { } command)
            {
                if (command.ValueKind != JsonValueKind.Object
                    || !command.TryGetProperty("command", out var commandName)
                    || commandName.GetString() is not { Length: > 0 } name)
                    throw new InvalidOperationException($"Quick action '{selected.Title}' returned an invalid command.");

                var arguments = command.TryGetProperty("arguments", out var values)
                    ? values.Clone()
                    : (JsonElement?)null;

                if (name == "roslyn.client.fixAllCodeAction")
                {
                    if (arguments is not { ValueKind: JsonValueKind.Array } args
                        || args.GetArrayLength() == 0)
                    {
                        throw new InvalidOperationException(
                            "Roslyn Fix All did not provide its arguments.");
                    }
                    var response = await client.ResolveFixAllAsync(args[0], "Document");

                    if (response.TryGetProperty("edit", out var fixAllEdit))
                    {
                        var replacements = LspHelpers.ParseWorkspaceEdit(fixAllEdit);
                        if (replacements.Count > 0)
                        {
                            _edits.Apply(replacements);
                            didWork = true;
                        }
                    }
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
