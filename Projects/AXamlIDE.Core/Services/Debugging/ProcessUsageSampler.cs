using System;
using System.Diagnostics;

namespace AXamlIDE.Services.Debugging;

/// <summary>One reading of a process: its memory (working set) and how busy it kept the machine since the last one.</summary>
/// <param name="MemoryBytes">The process's working set.</param>
/// <param name="CpuPercent">CPU time used since the last reading, as a percentage of all processors (Visual Studio's
/// Diagnostic Tools measure); null for the first reading, which has nothing to compare with.</param>
public readonly record struct ProcessUsage(long MemoryBytes, double? CpuPercent);

/// <summary>
/// Reads a process's memory and CPU, for the debugger's Diagnostics graph. Each <see cref="Sample"/> compares the CPU
/// time used with the time that passed since the one before.
/// </summary>
public sealed class ProcessUsageSampler : IDisposable
{
    private readonly Process _process;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastCpu;
    private TimeSpan _lastTime;

    /// <summary>Watch the process with this id. Throws ArgumentException if there's no such process.</summary>
    public ProcessUsageSampler(int processId) => _process = Process.GetProcessById(processId);

    public int ProcessId => _process.Id;

    /// <summary>Read the process now; null once it has exited (or can't be read).</summary>
    public ProcessUsage? Sample()
    {
        try
        {
            _process.Refresh();
            if (_process.HasExited)
                return null;
            var memory = _process.WorkingSet64;
            var cpu = _process.TotalProcessorTime;
            var now = _clock.Elapsed;
            var percent = _lastCpu is { } lastCpu ? CpuPercent(cpu - lastCpu, now - _lastTime, Environment.ProcessorCount) : (double?)null;
            _lastCpu = cpu;
            _lastTime = now;
            return new ProcessUsage(memory, percent);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>CPU time used in a stretch of wall time, as a percentage of all the processors (0 to 100).</summary>
    public static double CpuPercent(TimeSpan cpuUsed, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0)
            return 0;
        var percent = cpuUsed.TotalSeconds / (elapsed.TotalSeconds * processorCount) * 100;
        return Math.Clamp(percent, 0, 100);
    }

    public void Dispose() => _process.Dispose();
}
