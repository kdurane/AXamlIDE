using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace AXamlIDE.Services.Diagnostics;

/// <summary>One point on the timeline: <see cref="Elapsed"/> is from the start of its scope.</summary>
public sealed record PerfMark(string Scope, string Name, TimeSpan Elapsed, DateTime At);

/// <summary>
/// How long the slow things take, as named milestones: "startup" (from the process starting: window shown...),
/// "open" (from choosing a solution or folder: projects read, tree shown, files restored, each language server
/// started and its projects loaded, the first solution-wide results) and "build". Each mark is written to
/// <c>perf.log</c> in <see cref="LogFile.Folder"/> as <c>perf: open/roslyn-projects-loaded +8421 ms</c> and kept in
/// memory for Help > View Timings Log and the MCP tool get_timings, so "it feels slow" becomes a number.
/// A scope's run starts again with <see cref="Begin"/>; a name is only marked once per run (the first time is the
/// one that matters). Any thread.
/// </summary>
public static class PerfTimeline
{
    public const string Startup = "startup";
    public const string Open = "open";
    public const string Build = "build";

    private sealed class Run(DateTime started, string? subject)
    {
        public DateTime Started { get; } = started;
        public string? Subject { get; } = subject;
        public List<PerfMark> Marks { get; } = [];
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Run> Runs = new(StringComparer.Ordinal);

    /// <summary>The file the marks go to.</summary>
    public static string FilePath { get; } = LogFile.PathOf("perf.log");

    /// <summary>Where they're written: <see cref="FilePath"/>, or a test's own file.</summary>
    internal static string WriteTo { get; set; } = FilePath;

    /// <summary>Forget every run (tests).</summary>
    internal static void Clear()
    {
        lock (Gate)
            Runs.Clear();
    }

    /// <summary>
    /// Start (or start again) a scope, now. <paramref name="subject"/> says what it's about (the solution's name,
    /// the project being built). Startup needs no call: it starts when the process did.
    /// </summary>
    public static void Begin(string scope, string? subject = null)
    {
        var now = DateTime.UtcNow;
        lock (Gate)
            Runs[scope] = new Run(now, subject);
        Write($"{Stamp(now)}  perf: {scope} started{(subject is null ? "" : $" ({subject})")}");
    }

    /// <summary>A milestone in a scope that has started (marks in a scope that hasn't, or a name already marked in
    /// this run, are ignored). <paramref name="detail"/> goes on the end of the line ("failed", "3 projects").</summary>
    public static void Mark(string scope, string name, string? detail = null)
    {
        var now = DateTime.UtcNow;
        PerfMark mark;
        DateTime? processStarted = null;
        lock (Gate)
        {
            if (!Runs.TryGetValue(scope, out var run))
            {
                if (scope != Startup)
                    return;
                Runs[scope] = run = new Run(ProcessStarted(), null);
                processStarted = run.Started;
            }
            if (run.Marks.Any(existing => existing.Name == name))
                return;
            mark = new PerfMark(scope, name, now - run.Started, now);
            run.Marks.Add(mark);
        }
        if (processStarted is { } started)
            Write($"{Stamp(started)}  perf: {Startup} started (process {Environment.ProcessId})");
        Write($"{Stamp(now)}  perf: {scope}/{name} +{Milliseconds(mark.Elapsed)} ms{(detail is null ? "" : $"  {detail}")}");
    }

    /// <summary>Every scope's latest run, startup first, as text (what get_timings answers).</summary>
    public static string Describe()
    {
        List<(string Scope, Run Run, List<PerfMark> Marks)> runs;
        lock (Gate)
            runs = [.. Runs.Select(pair => (pair.Key, pair.Value, pair.Value.Marks.ToList()))];
        if (runs.Count == 0)
            return "Nothing timed yet.";

        var order = new[] { Startup, Open, Build };
        var lines = new List<string>();
        foreach (var (scope, run, marks) in runs.OrderBy(r => Array.IndexOf(order, r.Scope) is var i and >= 0 ? i : order.Length))
        {
            lines.Add($"{scope}{(run.Subject is null ? "" : $" ({run.Subject})")}, started {Stamp(run.Started)}:");
            foreach (var mark in marks)
                lines.Add($"  +{Milliseconds(mark.Elapsed),7} ms  {mark.Name}");
            if (marks.Count == 0)
                lines.Add("  (no milestones yet)");
        }
        using var process = Process.GetCurrentProcess();
        lines.Add($"Working set {process.WorkingSet64 / (1024 * 1024)} MB, managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB.");
        return string.Join('\n', lines);
    }

    private static DateTime ProcessStarted()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTime.UtcNow;   // the first mark is then +0
        }
    }

    private static string Milliseconds(TimeSpan elapsed) =>
        ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static void Write(string line)
    {
        Debug.WriteLine(line);
        LogFile.Append(WriteTo, line + "\n");
    }
}
