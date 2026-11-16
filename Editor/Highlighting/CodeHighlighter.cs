using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AXamlIDE.Services.Settings;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Colours a Markdown code block (the Claude panel's replies, the Markdown preview) the way the editor would: the
/// fence's language picks the editor's TextMate grammar, and the colours come from the editor's current theme.
/// A block with no language, or one there's no grammar for, is left plain.
/// </summary>
public sealed class CodeHighlighter
{
    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromMilliseconds(200);

    private readonly EditorGrammars _grammars;
    private readonly IEditorSettings _settings;

    // One registry per theme: it holds the theme's colour map and the grammars it has loaded.
    private (IRawTheme Theme, Registry Registry, IReadOnlyList<string> Colors)? _current;
    private readonly Dictionary<string, IBrush> _brushes = [];

    public CodeHighlighter(EditorGrammars grammars, IEditorSettings settings)
    {
        _grammars = grammars;
        _settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEditorSettings.SelectedTheme))
                ThemeChanged?.Invoke();
        };
    }

    /// <summary>The editor theme changed: code already shown should be coloured again.</summary>
    public event Action? ThemeChanged;

    /// <summary>The code as coloured runs, or null when the language has no grammar (show it plain).</summary>
    public IReadOnlyList<Inline>? Highlight(string? language, string code)
    {
        if (_grammars.ScopeForLanguage(language) is not { } scope)
            return null;

        var (registry, colors) = RegistryForTheme();
        if (registry.LoadGrammar(scope) is not { } grammar)
            return null;

        var inlines = new List<Inline>();
        IStateStack? state = null;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                inlines.Add(new LineBreak());
            var line = lines[i];
            var result = grammar.TokenizeLine2(line, state, LineTimeLimit);
            state = result.RuleStack;

            // Tokens come in pairs: where it starts, then its packed colour and font style.
            var tokens = result.Tokens;
            for (var t = 0; t < tokens.Length; t += 2)
            {
                var start = Math.Min(tokens[t], line.Length);
                var end = t + 2 < tokens.Length ? Math.Min(tokens[t + 2], line.Length) : line.Length;
                if (end <= start)
                    continue;
                inlines.Add(RunFor(line[start..end], tokens[t + 1], colors));
            }
        }
        return inlines;
    }

    private Run RunFor(string text, int metadata, IReadOnlyList<string> colors)
    {
        var run = new Run(text);
        // Colour ids count from 1 (colors[0] is id 1). Id 1 is the theme's default text colour, which some themes
        // leave as black, so plain text keeps the code block's own foreground instead.
        var foreground = EncodedTokenAttributes.GetForeground(metadata);
        if (foreground > 1 && foreground <= colors.Count && BrushFor(colors[foreground - 1]) is { } brush)
            run.Foreground = brush;

        var style = EncodedTokenAttributes.GetFontStyle(metadata);
        if (style > 0)   // -1 is "not set"
        {
            if ((style & TextMateSharp.Themes.FontStyle.Italic) != 0)
                run.FontStyle = Avalonia.Media.FontStyle.Italic;
            if ((style & TextMateSharp.Themes.FontStyle.Bold) != 0)
                run.FontWeight = FontWeight.Bold;
        }
        return run;
    }

    private IBrush? BrushFor(string color)
    {
        if (_brushes.TryGetValue(color, out var brush))
            return brush;
        if (!Color.TryParse(color, out var parsed))
            return null;
        return _brushes[color] = new SolidColorBrush(parsed).ToImmutable();
    }

    private (Registry Registry, IReadOnlyList<string> Colors) RegistryForTheme()
    {
        var theme = _grammars.LoadTheme(_settings.SelectedTheme);   // cached by EditorGrammars, same object until it changes
        if (_current is { } current && ReferenceEquals(current.Theme, theme))
            return (current.Registry, current.Colors);

        var registry = new Registry(_grammars);
        registry.SetTheme(theme);
        var colors = registry.GetColorMap().ToList();
        _current = (theme, registry, colors);
        _brushes.Clear();
        return (registry, colors);
    }
}
