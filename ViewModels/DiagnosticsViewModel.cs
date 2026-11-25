using System;
using System.Collections.Generic;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Debug tab's Diagnostics section (Visual Studio's Diagnostic Tools): the debugged program's memory and CPU,
/// read once a second while it runs and drawn as running graphs of the last two minutes. It keeps reading while the
/// program is paused (VS does too), so you can see the memory a step used. The graphs clear when a new session starts.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject,
    IRecipient<DebuggeeStartedMessage>, IRecipient<DebugSessionChangedMessage>
{
    /// <summary>How many readings the graphs show (one a second: two minutes).</summary>
    public const int HistoryLength = 120;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Queue<double> _memory = new();
    private readonly Queue<double> _cpu = new();
    private DispatcherTimer? _timer;
    private ProcessUsageSampler? _sampler;
    private long _peakBytes;

    public DiagnosticsViewModel(IMessenger messenger) => messenger.RegisterAll(this);

    /// <summary>Memory readings in MB, oldest first.</summary>
    [ObservableProperty] public partial IReadOnlyList<double> MemoryHistory { get; private set; } = [];

    /// <summary>CPU readings (percent of all processors), oldest first.</summary>
    [ObservableProperty] public partial IReadOnlyList<double> CpuHistory { get; private set; } = [];

    [ObservableProperty] public partial string MemoryText { get; private set; } = "";
    [ObservableProperty] public partial string PeakMemoryText { get; private set; } = "";
    [ObservableProperty] public partial string CpuText { get; private set; } = "";

    /// <summary>Shown instead of the graphs while there is nothing to graph.</summary>
    [ObservableProperty] public partial string? Message { get; private set; } = "Shown while a program is being debugged.";

    public void Receive(DebuggeeStartedMessage message)
    {
        Stop();
        Clear();
        try
        {
            _sampler = new ProcessUsageSampler(message.ProcessId);
        }
        catch (ArgumentException)
        {
            Message = "The program has already exited.";
            return;
        }

        Message = null;
        Add(_sampler.Sample());
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Add(_sampler?.Sample()));
        _timer.Start();
    }

    /// <summary>A session ended: stop reading, but leave the last graphs up (as VS does).</summary>
    public void Receive(DebugSessionChangedMessage message)
    {
        if (!message.IsDebugging)
            Stop();
    }

    /// <summary>Show these readings (the design-time sample data).</summary>
    public void Show(IEnumerable<ProcessUsage> readings)
    {
        Clear();
        Message = null;
        foreach (var reading in readings)
            Add(reading);
    }

    private void Add(ProcessUsage? reading)
    {
        if (reading is not { } usage)
        {
            Stop();   // the program exited
            return;
        }

        _peakBytes = Math.Max(_peakBytes, usage.MemoryBytes);
        Push(_memory, usage.MemoryBytes / (1024.0 * 1024.0));
        MemoryHistory = [.. _memory];
        MemoryText = FormatMegabytes(usage.MemoryBytes);
        PeakMemoryText = $"Peak {FormatMegabytes(_peakBytes)}";

        if (usage.CpuPercent is { } cpu)
        {
            Push(_cpu, cpu);
            CpuHistory = [.. _cpu];
            CpuText = $"{cpu:0} %";
        }
    }

    private static void Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength)
            history.Dequeue();
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private void Clear()
    {
        _memory.Clear();
        _cpu.Clear();
        _peakBytes = 0;
        MemoryHistory = [];
        CpuHistory = [];
        MemoryText = PeakMemoryText = CpuText = "";
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _sampler?.Dispose();
        _sampler = null;
    }
}
