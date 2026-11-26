using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One row of the Call Stack: a frame, or "[External Code]" for a run of frames without source (with Just My Code
/// off and SharpDbg, each such frame has its own row instead, and gets its source when it's opened).
/// </summary>
public sealed partial class CallStackRow(DebugFrame? frame, string name, string location, bool isTop) : ObservableObject
{

    /// <summary>Null for [External Code].</summary>
    public DebugFrame? Frame { get; } = frame;
    public string Name { get; } = name;
    public string Location { get; } = location;
    public bool IsTop { get; } = isTop;

    /// <summary>The frame with its source: <see cref="Frame"/> itself, or what decompiling it gave; null without source.</summary>
    public DebugFrame? Source { get; init; } = frame?.Path is null ? null : frame;

    public bool IsExternal => Source?.Path is null;

    /// <summary>The frame Locals / Watch / DataTips are looking at (VS's arrow).</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// The Call Stack window (Visual Studio's): the stopped thread's frames, top first, external code collapsed.
/// Double-click (or Enter) a frame to look at it: Locals, Watch and DataTips switch to it and the editor goes
/// to its line.
/// </summary>
public sealed partial class CallStackViewModel : ObservableObject
{
    private readonly DebugSnapshotTracker _debug;
    private readonly IMessenger _messenger;
    private readonly IClipboardService _clipboard;
    private StopSnapshot? _shown;

    public CallStackViewModel(DebugSnapshotTracker debug, IMessenger messenger, IClipboardService clipboard)
    {
        _debug = debug;
        _messenger = messenger;
        _clipboard = clipboard;
        debug.Changed += Rebuild;
    }

    public ObservableCollection<CallStackRow> Frames { get; } = [];

    [ObservableProperty] public partial CallStackRow? Selected { get; set; }

    public bool IsEmpty => Frames.Count == 0;

    private void Rebuild()
    {
        var stop = _debug.Current;
        if (stop is null)
        {
            _shown = null;
            Frames.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        // Same stop, another frame picked: only the marker moves.
        if (ReferenceEquals(stop, _shown))
        {
            foreach (var row in Frames)
                row.IsSelected = row.Frame is not null && row.Frame == _debug.SelectedFrame;
            return;
        }
        _shown = stop;

        Frames.Clear();
        var first = true;
        foreach (var frame in stop.Frames)
        {
            if (frame.Path is null && stop.ResolveFrame is not null)
            {
                // Just My Code off: each frame of the framework or a package, decompiled when opened.
                Frames.Add(new CallStackRow(frame, frame.Name, "", first) { IsSelected = frame == _debug.SelectedFrame });
            }
            else if (frame.Path is null)
            {
                if (Frames.LastOrDefault() is not { IsExternal: true })
                    Frames.Add(new CallStackRow(null, "[External Code]", "", first));
            }
            else
            {
                Frames.Add(new CallStackRow(frame, frame.Name, $"{Path.GetFileName(frame.Path)}:{frame.Line}", first)
                {
                    IsSelected = frame == _debug.SelectedFrame
                });
            }
            first = false;
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Look at a frame: Locals / Watch switch to it, and the editor shows its line. A frame without source is
    /// decompiled first when the debugger can (Just My Code off, SharpDbg); its row then shows where that is.
    /// </summary>
    [RelayCommand]
    private async Task Activate(CallStackRow? row)
    {
        if (row?.Frame is not { } frame)
            return;
        var source = row.Source;
        if (source is null && _shown is { ResolveFrame: { } resolve } stop)
        {
            DebugFrame? resolved = null;
            try
            {
                resolved = await resolve(frame, System.Threading.CancellationToken.None);
            }
            catch (System.Exception ex) when (ex is not System.OperationCanceledException)
            {
                _messenger.Send(new StatusMessage($"Couldn't decompile {frame.Name}: {ex.Message}"));
            }
            if (resolved?.Path is null || !ReferenceEquals(stop, _shown))
                return;
            source = resolved;
            var at = Frames.IndexOf(row);
            if (at >= 0)
                Frames[at] = new CallStackRow(frame, frame.Name, $"{Path.GetFileName(resolved.Path)}:{resolved.Line}", row.IsTop)
                    { Source = resolved, IsSelected = row.IsSelected };
        }
        if (source?.Path is not { } path)
            return;
        _debug.SelectFrame(frame);
        _messenger.Send(new NavigateToDebugLocationMessage(path, source.Line));
    }

    /// <summary>The whole stack as text, like VS's Copy All.</summary>
    [RelayCommand]
    private Task CopyAll()
    {
        var text = new StringBuilder();
        foreach (var row in Frames)
            text.AppendLine(row.Location.Length == 0 ? row.Name : $"{row.Name} in {row.Source?.Path}:line {row.Source?.Line}");
        return _clipboard.SetTextAsync(text.ToString());
    }
}
