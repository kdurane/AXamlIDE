using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Terminal;
using Xunit;

namespace AXamlIDE.Tests;

public class TerminalTests
{
    private const char Esc = '\u001B';
    private const char RS = ShellOutputSplitter.RecordSeparator;
    private const char US = ShellOutputSplitter.UnitSeparator;

    [Fact]
    public void Colour_codes_and_titles_are_removed()
    {
        var stripper = new AnsiStripper();
        Assert.Equal("red plain", stripper.Strip($"{Esc}[1;31mred{Esc}[0m plain"));
        Assert.Equal("after", stripper.Strip($"{Esc}]0;window title\u0007after"));
        Assert.Equal("x", stripper.Strip($"{Esc}]8;;link{Esc}\\x"));
    }

    [Fact]
    public void A_code_split_between_chunks_is_still_removed()
    {
        var stripper = new AnsiStripper();
        Assert.Equal("a", stripper.Strip($"a{Esc}[3"));
        Assert.Equal("b", stripper.Strip("2mb"));
    }

    [Fact]
    public void Markers_end_commands_and_are_not_shown()
    {
        var splitter = new ShellOutputSplitter();
        var text = new StringBuilder();
        var ends = new List<(int, string)>();

        splitter.Feed($"hello\n{RS}0{US}/home/me{RS}\nnext", t => text.Append(t), (code, dir) => ends.Add((code, dir)));

        Assert.Equal("hello\nnext", text.ToString());
        Assert.Equal([(0, "/home/me")], ends);
    }

    [Fact]
    public void A_marker_split_between_chunks_is_held_until_complete()
    {
        var splitter = new ShellOutputSplitter();
        var text = new StringBuilder();
        var ends = new List<(int, string)>();
        void Feed(string chunk) => splitter.Feed(chunk, t => text.Append(t), (code, dir) => ends.Add((code, dir)));

        Feed($"no newline{RS}12{US}/tm");
        Assert.Empty(ends);
        Feed($"p{RS}\r\n");

        Assert.Equal("no newline", text.ToString());
        Assert.Equal([(12, "/tmp")], ends);
    }

    [Fact]
    public async Task The_shell_runs_commands_and_follows_cd()
    {
        if (OperatingSystem.IsWindows())
            return;   // PowerShell isn't on every build machine

        var folder = Directory.CreateTempSubdirectory("axamlide-shell-").FullName;
        var output = new StringBuilder();
        var finished = new SemaphoreSlim(0);
        var exitCodes = new List<int>();
        using var shell = ShellSession.Start(Environment.CurrentDirectory);
        shell.OutputReceived += (text, _) => { lock (output) output.Append(text); };
        shell.CommandFinished += code => { exitCodes.Add(code); finished.Release(); };

        try
        {
            shell.Run("echo hello");
            Assert.True(await finished.WaitAsync(TimeSpan.FromSeconds(10)));
            shell.Run($"cd '{folder}'");
            Assert.True(await finished.WaitAsync(TimeSpan.FromSeconds(10)));
            shell.Run("false");
            Assert.True(await finished.WaitAsync(TimeSpan.FromSeconds(10)));

            lock (output)
                Assert.Equal("hello\n", output.ToString());
            Assert.Equal([0, 0, 1], exitCodes);
            Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(shell.WorkingDirectory));
            Assert.False(shell.IsBusy);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
