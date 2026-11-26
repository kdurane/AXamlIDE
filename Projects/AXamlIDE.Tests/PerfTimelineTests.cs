using System;
using System.IO;
using AXamlIDE.Services.Diagnostics;
using Xunit;

namespace AXamlIDE.Tests;

public sealed class PerfTimelineTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"perf-{Guid.NewGuid():N}.log");

    public PerfTimelineTests()
    {
        PerfTimeline.WriteTo = _log;
        PerfTimeline.Clear();
    }

    public void Dispose()
    {
        PerfTimeline.Clear();
        PerfTimeline.WriteTo = PerfTimeline.FilePath;
        File.Delete(_log);
    }

    [Fact]
    public void A_mark_before_its_scope_begins_is_ignored()
    {
        PerfTimeline.Mark(PerfTimeline.Open, "tree-shown");

        Assert.DoesNotContain("tree-shown", PerfTimeline.Describe());
        Assert.False(File.Exists(_log));
    }

    [Fact]
    public void Marks_are_logged_with_their_scope_and_elapsed_time()
    {
        PerfTimeline.Begin(PerfTimeline.Open, "App.slnx");
        PerfTimeline.Mark(PerfTimeline.Open, "projects-read", "3 projects");

        var log = File.ReadAllText(_log);
        Assert.Contains("perf: open started (App.slnx)", log);
        Assert.Matches(@"perf: open/projects-read \+\d+ ms  3 projects", log);
        Assert.Contains("open (App.slnx)", PerfTimeline.Describe());
    }

    [Fact]
    public void Only_the_first_of_a_name_counts_until_the_scope_begins_again()
    {
        PerfTimeline.Begin(PerfTimeline.Build, "Build");
        PerfTimeline.Mark(PerfTimeline.Build, "finished", "first");
        PerfTimeline.Mark(PerfTimeline.Build, "finished", "second");
        Assert.Single(File.ReadAllLines(_log), line => line.Contains("build/finished"));

        PerfTimeline.Begin(PerfTimeline.Build, "Rebuild");
        PerfTimeline.Mark(PerfTimeline.Build, "finished", "third");
        Assert.Equal(2, Array.FindAll(File.ReadAllLines(_log), line => line.Contains("build/finished")).Length);
        Assert.Contains("build (Rebuild)", PerfTimeline.Describe());
    }

    [Fact]
    public void Startup_needs_no_begin_and_counts_from_the_process_start()
    {
        PerfTimeline.Mark(PerfTimeline.Startup, "main");

        var description = PerfTimeline.Describe();
        Assert.StartsWith("startup, started", description);
        Assert.Contains("main", description);
        Assert.Contains("perf: startup started (process", File.ReadAllText(_log));
    }
}
