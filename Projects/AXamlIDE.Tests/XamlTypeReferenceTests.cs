using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Tests;

public sealed class XamlTypeReferenceTests
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:My.App.ViewModels"
                     xmlns:local="clr-namespace:My.App.Controls;assembly=My.App"

        """;

    private static XamlTypeReference? At(string bodyWithCaret)
    {
        var text = Header + bodyWithCaret;
        var offset = text.IndexOf('|');
        return XamlTypeReference.At(text.Remove(offset, 1), offset);
    }

    [Theory]
    [InlineData("""x:DataType="vm:NuGet|Dockable">""", "My.App.ViewModels.NuGetDockable", false)]
    [InlineData("""x:DataType="v|m:NuGetDockable">""", "My.App.ViewModels.NuGetDockable", false)]
    [InlineData("""<DataTemplate DataType="{x:Type vm:It|em}" />""", "My.App.ViewModels.Item", false)]
    [InlineData("""<TextBlock Text="{x:Static vm:Strings.Ti|tle}" />""", "My.App.ViewModels.Strings", false)]
    [InlineData("""<local:My|Control />""", "My.App.Controls.MyControl", true)]
    [InlineData("""<Border local:Attach|ed.Value="1" />""", "My.App.Controls.Attached", false)]
    public void Resolves_the_prefixed_type(string body, string fullName, bool inTag)
    {
        var found = At(body);

        Assert.NotNull(found);
        Assert.Equal(fullName, found.FullName);
        Assert.Equal(inTag, found.InTag);
    }

    [Theory]
    [InlineData("""<But|ton />""")]                         // Avalonia's default namespace
    [InlineData("""x:Na|me="Foo" """)]                      // x: is the XAML language, not a CLR namespace
    [InlineData("""<TextBlock Text="Time: 1|0" />""")]      // a colon in plain text
    [InlineData("""<TextBlock Text="{Binding Na|me}" />""")]
    public void Ignores_what_is_not_a_CLR_type(string body) => Assert.Null(At(body));
}
