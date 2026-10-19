using AXamlIDE.Services.Debugging;
using Xunit;

namespace AXamlIDE.Tests;

public class DebugTypeNamesTests
{
    [Theory]
    [InlineData("System.Collections.Generic.List<MyApp.Models.Person>", "List<Person>", "Person", "List")]
    [InlineData("MyApp.Models.Person[]", "Person[]", "Person", "array")]
    [InlineData("int[,]", "int[,]", "int", "array")]
    [InlineData("System.Collections.Generic.Dictionary<string, MyApp.Person>", "Dictionary<string, Person>", "string → Person", "Dictionary")]
    [InlineData("System.Collections.ObjectModel.ObservableCollection<System.Collections.Generic.List<int>>", "ObservableCollection<List<int>>", "List<int>", "ObservableCollection")]
    [InlineData("MyApp.Person", "Person", null, "Person")]
    public void Names(string type, string shortName, string? element, string kind)
    {
        Assert.Equal(shortName, DebugTypeNames.Short(type));
        Assert.Equal(element, DebugTypeNames.ElementType(type));
        Assert.Equal(kind, DebugTypeNames.CollectionKind(type));
    }
}
