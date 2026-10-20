using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The C# outline drop-down above the editor: types and members of the active document,
/// fetched from the language server 300ms after the user stops typing or switches tabs.
/// Picking an entry sends <see cref="NavigateToOutlineItemMessage"/>.
/// </summary>
public sealed partial class OutlineViewModel : ObservableObject, IDisposable
{
    private readonly DocumentManager _documents;
    private readonly IMessenger _messenger;
    private readonly LanguageSessionService _languages;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private OpenDocument? _document;          // the active document whose text we are watching
    private CancellationTokenSource? _requestCts;
    private int _requestSequence;

    public OutlineViewModel(DocumentManager documents, LanguageSessionService languages, IMessenger messenger)
    {
        _documents = documents;
        _messenger = messenger;
        _languages = languages;

        _timer.Tick += OnTimer;
        _documents.ActiveChanged += OnActiveDocumentChanged;
        languages.ClientAttached += document =>
        {
            if (ReferenceEquals(document, _documents.Active))
                Dispatcher.UIThread.Post(QueueUpdate);
        };
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsVisible));
    }

    public ObservableCollection<DocumentOutlineItem> Items { get; } = [];

    public bool IsVisible => Items.Count > 0;

    [ObservableProperty] public partial DocumentOutlineItem? SelectedItem { get; set; }

    partial void OnSelectedItemChanged(DocumentOutlineItem? value)
    {
        if (value is not null)
            _messenger.Send(new NavigateToOutlineItemMessage(value));
    }

    // ------------------------------------------------------------ triggers

    private void OnActiveDocumentChanged(OpenDocument? document)
    {
        _document?.Document.Changed -= OnTextChanged;
        _document = document;
        _document?.Document.Changed += OnTextChanged;

        Clear();
        if (document is null)
            _timer.Stop();
        else
            QueueUpdate();
    }

    private void OnTextChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_document?.LanguageId == LanguageIds.CSharp)
            QueueUpdate();
    }

    private void QueueUpdate()
    {
        _timer.Stop();
        _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_documents.Active is { } document)
            _ = UpdateAsync(document);
        else
            Clear();
    }

    // ------------------------------------------------------------ fetching

    private async Task UpdateAsync(OpenDocument document)
    {
        var requestSequence = Interlocked.Increment(ref _requestSequence);
        _requestCts?.Cancel();
        _requestCts?.Dispose();
        _requestCts = null;

        if (!ReferenceEquals(_documents.Active, document)
            || document.LanguageId != LanguageIds.CSharp
            || _languages.ClientFor(document) is not { } client)
        {
            Clear();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _requestCts = cancellation;
        try
        {
            var symbols = await client.DocumentSymbolsAsync(document.Uri, cancellation.Token);
            if (cancellation.IsCancellationRequested
                || requestSequence != Volatile.Read(ref _requestSequence)
                || !ReferenceEquals(_documents.Active, document))
                return;

            var items = symbols
                .Where(symbol => IsOutlineKind(symbol.Kind))
                .Select(symbol =>
                {
                    var start = document.OffsetOf(symbol.Range.StartLine, symbol.Range.StartChar);
                    var end = document.OffsetOf(symbol.Range.EndLine, symbol.Range.EndChar);
                    var navigationOffset = document.OffsetOf(
                        symbol.SelectionRange.StartLine,
                        symbol.SelectionRange.StartChar);
                    return new DocumentOutlineItem(
                        symbol.Name,
                        symbol.Kind,
                        start,
                        Math.Max(start, end),
                        navigationOffset);
                })
                .ToArray();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (requestSequence != Volatile.Read(ref _requestSequence)
                    || !ReferenceEquals(_documents.Active, document))
                    return;

                Items.Clear();
                foreach (var item in items)
                    Items.Add(item);
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[outline] Could not retrieve symbols for '{document.Path}': {ex}");
            if (requestSequence == Volatile.Read(ref _requestSequence)
                && ReferenceEquals(_documents.Active, document))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Clear();
                    _messenger.Send(new StatusMessage($"Couldn't update C# outline: {ex.Message}"));
                });
            }
        }
        finally
        {
            if (ReferenceEquals(_requestCts, cancellation))
                _requestCts = null;
            cancellation.Dispose();
        }
    }

    private void Clear()
    {
        Interlocked.Increment(ref _requestSequence);
        _requestCts?.Cancel();
        Items.Clear();
        SelectedItem = null;
    }

    private static bool IsOutlineKind(int kind)
        => kind is LspSymbolKind.Module or LspSymbolKind.Namespace or LspSymbolKind.Class
            or LspSymbolKind.Method or LspSymbolKind.Property or LspSymbolKind.Field
            or LspSymbolKind.Constructor or LspSymbolKind.Enum or LspSymbolKind.Interface
            or LspSymbolKind.Function or LspSymbolKind.Struct or LspSymbolKind.Event
            or LspSymbolKind.TypeParameter;

    public void Dispose()
    {
        _timer.Stop();
        _requestCts?.Cancel();
        _requestCts?.Dispose();
        _requestCts = null;
        _document?.Document.Changed -= OnTextChanged;
    }
}
