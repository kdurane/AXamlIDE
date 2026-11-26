using Avalonia;
using Avalonia.Data.Converters;
using AXamlIDE.Services.Testing;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// How a <see cref="TestOutcome"/> looks in the Test Explorer, as in VS Code's Testing view: a green tick, a red
/// cross, a grey dash for skipped, a circle for not run, and a clock while running (outlined when out of date). Colours are the
/// Test*Brush resources (Styles/ThemeResources.axaml), from VS Code's testing.icon* theme colours.
/// </summary>
public static class TestConverters
{
    /// <summary>A <see cref="TestNode"/>'s icon (a result from an earlier run than the latest gets the outline version,
    /// as in Visual Studio's Test Explorer), or a bare <see cref="TestOutcome"/>'s.</summary>
    public static readonly IValueConverter Icon = new FuncValueConverter<object?, object?>(value =>
    {
        var (outcome, stale) = value switch
        {
            TestNode node => (node.Outcome, node.IsStale),
            TestOutcome bare => (bare, false),
            _ => (TestOutcome.NotRun, false)
        };
        var key = outcome switch
        {
            TestOutcome.Passed => stale ? "Icon.CheckCircleOutline" : "Icon.CheckCircle",
            TestOutcome.Failed => stale ? "Icon.CloseCircleOutline" : "Icon.CloseCircle",
            TestOutcome.Skipped => stale ? "Icon.MinusCircleOutline" : "Icon.MinusCircle",
            TestOutcome.Running => "Icon.ClockOutline",
            _ => "Icon.CircleOutline"
        };
        return Application.Current?.TryGetResource(key, null, out var icon) == true ? icon : AvaloniaProperty.UnsetValue;
    });

    public static readonly IValueConverter Brush = new FuncValueConverter<TestOutcome, object?>(outcome =>
        ThemeBrushes.Get(outcome switch
        {
            TestOutcome.Passed => "TestPassedBrush",
            TestOutcome.Failed => "TestFailedBrush",
            TestOutcome.Skipped => "TestSkippedBrush",
            _ => "TreeViewItemForeground"
        }) ?? AvaloniaProperty.UnsetValue);
}
