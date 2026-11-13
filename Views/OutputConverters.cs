using Avalonia.Data.Converters;
using AXamlIDE.Services.Running;

namespace AXamlIDE.Views;

/// <summary>What an Output line is, for the Output panel's styling (OutputView.axaml): error, warning, or a link to a file.</summary>
public static class OutputConverters
{
    public static readonly IValueConverter IsError =
        new FuncValueConverter<string?, bool>(line => line is not null && BuildOutputParser.TryParseLine(line) is { IsError: true });

    public static readonly IValueConverter IsWarning =
        new FuncValueConverter<string?, bool>(line => line is not null && BuildOutputParser.TryParseLine(line) is { IsError: false });

    /// <summary>The line names a file and line: double-clicking it opens the file.</summary>
    public static readonly IValueConverter HasLocation =
        new FuncValueConverter<string?, bool>(line => line is not null && BuildOutputParser.TryParseLine(line) is { HasLocation: true });
}
