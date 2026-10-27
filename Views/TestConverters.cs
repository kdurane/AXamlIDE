using Avalonia;
using Avalonia.Data.Converters;
using AXamlIDE.Services.Testing;

namespace AXamlIDE.Views;

/// <summary>
/// How a <see cref="TestOutcome"/> looks in the Test Explorer, as in VS Code's Testing view: a green tick, a red
/// cross, a grey dash for skipped, a circle for not run, and a clock while running. Colours are the
/// Test*Brush resources (Styles/ThemeResources.axaml), from VS Code's testing.icon* theme colours.
/// </summary>
public static class TestConverters
{
    public static readonly IValueConverter Icon = new FuncValueConverter<TestOutcome, object?>(outcome =>
        Application.Current?.TryGetResource(outcome switch
        {
            TestOutcome.Passed => "Icon.CheckCircle",
            TestOutcome.Failed => "Icon.CloseCircle",
            TestOutcome.Skipped => "Icon.MinusCircle",
            TestOutcome.Running => "Icon.ClockOutline",
            _ => "Icon.CircleOutline"
        }, null, out var icon) == true ? icon : AvaloniaProperty.UnsetValue);

    public static readonly IValueConverter Brush = new FuncValueConverter<TestOutcome, object?>(outcome =>
        ThemeBrushes.Get(outcome switch
        {
            TestOutcome.Passed => "TestPassedBrush",
            TestOutcome.Failed => "TestFailedBrush",
            TestOutcome.Skipped => "TestSkippedBrush",
            _ => "TreeViewItemForeground"
        }) ?? AvaloniaProperty.UnsetValue);
}
