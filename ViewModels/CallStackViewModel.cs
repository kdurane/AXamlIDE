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

/// <summary>One row of the Call Stack: a frame, or "[External Code]" for a run of frames without source.</summary>
public sealed partial class CallStackRow : ObservableObject
{
    public CallStackRow(DebugFrame? frame, string name, string location, bool isTop)
    {
        Frame = frame;
        Name = name;
        Location = location;
        IsTop = isTop;
    }

    /// <summary>Null for [External Code].</summary>
    public DebugFrame? Frame { get; }
    public string Name { get; }
    public string Location { get; }
    public bool IsTop { get; }
    public bool IsExternal => Frame?.Path is null;

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
            if (frame.Path is null)
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

    /// <summary>Look at a frame: Locals / Watch switch to it, and the editor shows its line.</summary>
    [RelayCommand]
    private void Activate(CallStackRow? row)
    {
        if (row?.Frame is not { Path: { } path } frame)
            return;
        _debug.SelectFrame(frame);
        _messenger.Send(new NavigateToDebugLocationMessage(path, frame.Line));
    }

    /// <summary>The whole stack as text, like VS's Copy All.</summary>
    [RelayCommand]
    private Task CopyAll()
    {
        var text = new StringBuilder();
        foreach (var row in Frames)
            text.AppendLine(row.Location.Length == 0 ? row.Name : $"{row.Name} in {row.Frame?.Path}:line {row.Frame?.Line}");
        return _clipboard.SetTextAsync(text.ToString());
    }
}
