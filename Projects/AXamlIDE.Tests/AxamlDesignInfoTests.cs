using AXamlIDE.Services.Preview;

namespace AXamlIDE.Tests;

public class AxamlDesignInfoTests
{
    private const string Head =
        """
        xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d" xmlns:sample="using:Contoso.SampleData" xmlns:vm="clr-namespace:Contoso.ViewModels;assembly=Contoso"
        """;

    [Fact]
    public void Reads_the_class_the_static_design_data_and_the_design_size()
    {
        var info = AxamlDesignInfo.Read(
            $$"""<UserControl {{Head}} x:Class="Contoso.Views.OrdersView" Design.DataContext="{x:Static sample:DesignData.Orders}" d:DesignWidth="900" d:DesignHeight="260.5"/>""");

        Assert.Equal(new AxamlDesignInfo("Contoso.Views.OrdersView", "Contoso.SampleData.DesignData.Orders", 900, 260.5), info);
    }

    [Fact]
    public void An_element_design_data_context_is_made_from_its_type()
    {
        var info = AxamlDesignInfo.Read(
            $"""
             <Window {Head} x:Class="Contoso.MainWindow">
               <Design.DataContext><vm:MainViewModel Title="ignored"/></Design.DataContext>
               <TextBlock/>
             </Window>
             """);

        Assert.Equal("Contoso.ViewModels.MainViewModel", info.DataContext);
        Assert.Null(info.Width);
    }

    [Theory]
    [InlineData("d:DataContext=\"{x:Static Member=sample:DesignData.Main}\"", "Contoso.SampleData.DesignData.Main")]
    [InlineData("Design.DataContext=\"{Binding Something}\"", null)]
    [InlineData("Design.DataContext=\"{x:Static DesignData.Main}\"", null)]   // no prefix: not the app's namespace
    public void Only_static_members_of_the_apps_types_are_taken(string attribute, string? expected)
        => Assert.Equal(expected, AxamlDesignInfo.Read($"<UserControl {Head} {attribute}/>").DataContext);

    [Fact]
    public void Broken_xaml_gives_nothing()
        => Assert.Equal(new AxamlDesignInfo(null, null, null, null), AxamlDesignInfo.Read("<UserControl"));
}
