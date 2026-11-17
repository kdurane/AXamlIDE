using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AXamlIDE.Views;

/// <summary>Small converters for the Claude panel (Views/AgentChatView.axaml).</summary>
public static class AgentChatConverters
{
    /// <summary>A full path as just its file name (the tool call's file links).</summary>
    public static readonly IValueConverter FileName = new FuncValueConverter<string?, string>(path => Path.GetFileName(path) ?? "");

    /// <summary>Bold while true (the plan's step in progress).</summary>
    public static readonly IValueConverter BoldWhen = new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.SemiBold : FontWeight.Normal);
}
