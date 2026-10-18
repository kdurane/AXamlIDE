using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaEdit.CodeCompletion;
using AXamlIDE.Models;

namespace AXamlIDE.Views.Editor.SignatureHelp;

/// <summary>
/// Signature help popup (parameter hints). Re-requests as the caret moves while it is open.
/// </summary>
public sealed class SignatureHelpController
{
    private readonly EditorContext _ctx;

    private OverloadInsightWindow? _insightWindow;
    private int _signatureHelpSequence;
    private CancellationTokenSource? _signatureHelpCts;

    public SignatureHelpController(EditorContext ctx)
    {
        _ctx = ctx;

        ctx.Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_insightWindow?.IsVisible == true && _ctx.Active is { } doc)
                _ = RequestAsync(doc, triggerCharacter: null, isRetrigger: true);
        };

        ctx.Detaching += _ => Close();
    }

    public bool IsVisible => _insightWindow?.IsVisible == true;

    public async Task RequestAsync(OpenDocument doc, string? triggerCharacter, bool isRetrigger = false)
    {
        if (!ReferenceEquals(doc, _ctx.Active) || doc.Client is not { } client || doc.Sync is null)
            return;

        doc.Sync.Flush();
        var requestId = ++_signatureHelpSequence;
        _signatureHelpCts?.Cancel();
        var cancellation = _signatureHelpCts = new CancellationTokenSource();
        var location = doc.Document.GetLocation(_ctx.Editor.CaretOffset);

        try
        {
            var help = await client.SignatureHelpAsync(
                doc.Uri,
                location.Line - 1,
                location.Column - 1,
                triggerCharacter,
                isRetrigger,
                cancellation.Token);

            if (cancellation.IsCancellationRequested
                || requestId != _signatureHelpSequence
                || !ReferenceEquals(doc, _ctx.Active))
                return;

            if (help is null)
            {
                Close();
                return;
            }

            var items = help.Signatures.Select(signature =>
            {
                var activeParameter = help.ActiveParameter ?? signature.ActiveParameter;
                var contentParts = new List<string>();
                if (signature.Documentation is { Length: > 0 } documentation)
                    contentParts.Add(documentation);

                if (activeParameter is not ({ } parameterIndex and >= 0)
                    || parameterIndex >= signature.Parameters.Count)
                    return new SignatureInsightItem(
                        signature.Label,
                        string.Join(Environment.NewLine + Environment.NewLine, contentParts));
                var parameter = signature.Parameters[parameterIndex];
                var parameterText = $"Active parameter: {parameter.Label}";
                if (!string.IsNullOrWhiteSpace(parameter.Documentation))
                    parameterText += Environment.NewLine + parameter.Documentation;
                contentParts.Add(parameterText);

                return new SignatureInsightItem(
                    signature.Label,
                    string.Join(Environment.NewLine + Environment.NewLine, contentParts));
            }).ToArray();

            Show(items, help.ActiveSignature);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Signature help failed: {ex}");
            if (requestId == _signatureHelpSequence)
                Close();
        }
        finally
        {
            if (ReferenceEquals(_signatureHelpCts, cancellation))
                _signatureHelpCts = null;
            cancellation.Dispose();
        }
    }

    private void Show(IReadOnlyList<SignatureInsightItem> items, int activeSignature = 0)
    {
        if (items.Count == 0)
        {
            Close();
            return;
        }

        if (_insightWindow is null)
        {
            _insightWindow = new OverloadInsightWindow(_ctx.Editor.TextArea);
            _insightWindow.Closed += (_, _) => _insightWindow = null;
        }

        _insightWindow.Provider = new SignatureOverloadProvider(
            items,
            Math.Clamp(activeSignature, 0, items.Count - 1));

        if (!_insightWindow.IsVisible)
            _insightWindow.Show();
    }

    public void Close()
    {
        _signatureHelpSequence++;
        _signatureHelpCts?.Cancel();
        _insightWindow?.Close();
        _insightWindow = null;
    }
}
