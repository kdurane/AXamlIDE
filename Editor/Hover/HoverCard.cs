using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.Hover;

/// <summary>
/// The hover card's look, shared by C# and AXAML (and the diagnostics shown in it):
///
///   ● error / warning message           (in the diagnostic's colour)
///     Quick Fix...  Ctrl+.              (when the language server has quick fixes)
///   ─────────────
///   signature                           (the editor's font)
///   documentation                       (normal text)
///
/// Colours are the Hover*/Diagnostic*/LinkBrush resources (Styles/ThemeResources.axaml), so it follows the theme.
/// </summary>
public static class HoverCard
{
    private const double MaxWidth = 560;

    /// <param name="onQuickFix">Shows the quick fixes; null when there are none to offer.</param>
    public static Control Create(HoverContent content, FontFamily codeFont, double codeFontSize, Action? onQuickFix)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = MaxWidth };

        foreach (var diagnostic in content.Diagnostics)
            panel.Children.Add(DiagnosticRow(diagnostic));

        if (onQuickFix is not null && content.Diagnostics.Count > 0)
            panel.Children.Add(QuickFixLink(onQuickFix));

        var hasSymbol = content.Signature is not null || content.Documentation is not null;
        if (content.Diagnostics.Count > 0 && hasSymbol)
            panel.Children.Add(new Border { Height = 1, Background = Brush("HoverBorderBrush"), Margin = new Thickness(0, 2) });

        if (content.Signature is { } signature)
            panel.Children.Add(new SelectableTextBlock
            {
                Text = signature,
                FontFamily = codeFont,
                FontSize = codeFontSize,
                TextWrapping = TextWrapping.Wrap
            });

        if (content.Documentation is { } documentation)
            panel.Children.Add(new SelectableTextBlock { Text = documentation, TextWrapping = TextWrapping.Wrap, Opacity = 0.9 });

        return new Border
        {
            Background = Brush("HoverBackgroundBrush"),
            BorderBrush = Brush("HoverBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.FromArgb(70, 0, 0, 0) }),
            Child = new ScrollViewer
            {
                MaxHeight = 400,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = panel
            },
            [TextElement.ForegroundProperty] = Brush("HoverForegroundBrush"),
            [TextElement.FontSizeProperty] = 12.0
        };
    }

    private static Control DiagnosticRow(HoverDiagnostic diagnostic)
    {
        var brush = Brush(diagnostic.Severity switch
        {
            LspDiagnosticSeverity.Error => "DiagnosticErrorBrush",
            LspDiagnosticSeverity.Warning => "DiagnosticWarningBrush",
            _ => "DiagnosticInfoBrush"
        });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush, Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        var message = new SelectableTextBlock { Text = diagnostic.Message, TextWrapping = TextWrapping.Wrap, Foreground = brush };
        Grid.SetColumn(message, 1);
        row.Children.Add(message);
        return row;
    }

    private static Control QuickFixLink(Action onQuickFix)
    {
        var link = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(13, 0, 0, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = false,   // Enter must stay with the editor
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Quick Fix...", Foreground = Brush("LinkBrush"), TextDecorations = TextDecorations.Underline },
                    new TextBlock { Text = "Ctrl+.", Opacity = 0.6 }
                }
            }
        };
        link.Click += (_, _) => onQuickFix();
        return link;
    }

    private static IBrush? Brush(string key)
        => Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true ? brush as IBrush : null;

    /// <summary>The severity colour of the worst diagnostic (for squiggles and markers).</summary>
    public static IBrush? DiagnosticBrush(int severity) => Brush(severity switch
    {
        LspDiagnosticSeverity.Error => "DiagnosticErrorBrush",
        LspDiagnosticSeverity.Warning => "DiagnosticWarningBrush",
        _ => "DiagnosticInfoBrush"
    });
}
