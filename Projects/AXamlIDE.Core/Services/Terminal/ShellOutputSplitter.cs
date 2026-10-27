using System;
using System.Text;

namespace AXamlIDE.Services.Terminal;

/// <summary>
/// Splits a shell's standard output into the text the user sees and the end-of-command markers that
/// <see cref="ShellSession"/> has the shell print after each command:
///
///   RS exit-code US working-directory RS newline        (RS = U+001E, US = U+001F, characters programs don't print)
///
/// A marker can arrive split across chunks, so the start of one is held back until its closing RS turns up.
/// Feed every chunk through the same instance, in order.
/// </summary>
public sealed class ShellOutputSplitter
{
    public const char RecordSeparator = '\u001E';
    public const char UnitSeparator = '\u001F';

    private readonly StringBuilder _marker = new();
    private bool _inMarker;
    private bool _skipNewline;   // the newline that ends a marker isn't output

    /// <param name="chunk">The next piece of output.</param>
    /// <param name="text">Called with output to show (possibly more than once per chunk).</param>
    /// <param name="commandEnded">Called at each marker, with the exit code and the shell's working directory.</param>
    public void Feed(string chunk, Action<string> text, Action<int, string> commandEnded)
    {
        var plain = new StringBuilder(chunk.Length);
        foreach (var c in chunk)
        {
            if (_skipNewline)
            {
                if (c == '\r')
                    continue;          // Windows: "\r\n"
                _skipNewline = false;
                if (c == '\n')
                    continue;
            }

            if (!_inMarker)
            {
                if (c == RecordSeparator)
                    _inMarker = true;
                else
                    plain.Append(c);
                continue;
            }

            if (c != RecordSeparator)
            {
                _marker.Append(c);
                continue;
            }

            // The closing RS: everything before it is output, then the command has ended.
            if (plain.Length > 0)
            {
                text(plain.ToString());
                plain.Clear();
            }

            var (exitCode, directory) = ParseMarker(_marker.ToString());
            _marker.Clear();
            _inMarker = false;
            _skipNewline = true;
            commandEnded(exitCode, directory);
        }

        if (plain.Length > 0)
            text(plain.ToString());
    }

    /// <summary>"0␟/home/me" → (0, "/home/me"). A missing or unreadable exit code counts as 0.</summary>
    private static (int ExitCode, string Directory) ParseMarker(string marker)
    {
        var separator = marker.IndexOf(UnitSeparator);
        if (separator < 0)
            return (0, marker.Trim());
        var exitCode = int.TryParse(marker.AsSpan(0, separator).Trim(), out var code) ? code : 0;
        return (exitCode, marker[(separator + 1)..].Trim());
    }
}
