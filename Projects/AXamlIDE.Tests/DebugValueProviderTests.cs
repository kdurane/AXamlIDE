using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Debugging;
using Xunit;

namespace AXamlIDE.Tests;

public class DebugValueProviderTests
{
    /// <summary>Canned DAP answers, shaped like netcoredbg's (see the probe notes in the plan).</summary>
    private sealed class FakeDebugger : IDebugEvaluator
    {
        public Dictionary<int, List<DebugVariable>> Children { get; } = [];
        public Dictionary<string, DebugVariable> Values { get; } = [];
        public List<DebugVariable> Locals { get; } = [];
        public List<string> Evaluated { get; } = [];

        public Task<IReadOnlyList<DebugVariable>> GetLocalsAsync(int frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DebugVariable>>(Locals);

        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DebugVariable>>(Children.GetValueOrDefault(variablesReference, []));

        public Task<DebugEvaluation> EvaluateAsync(int frameId, string expression, string context, CancellationToken ct = default)
        {
            Evaluated.Add(expression);
            return Task.FromResult(Values.TryGetValue(expression, out var value)
                ? new DebugEvaluation(value, null)
                : new DebugEvaluation(null, $"The name '{expression}' does not exist in the current context"));
        }

        public Task<DebugEvaluation> SetExpressionAsync(int frameId, string expression, string value, CancellationToken ct = default)
        {
            Values[expression] = new DebugVariable(expression, value, "int", 0, expression);
            return Task.FromResult(new DebugEvaluation(Values[expression], null));
        }

        public IReadOnlyCollection<string> LoadedModules => [];

        public int? ProcessId => null;

        public void Value(string expression, string value, string type = "int", int reference = 0)
            => Values[expression] = new DebugVariable(expression, value, type, reference, expression);
    }

    private static DebugVariable Var(string name, string value, string type, int reference = 0, string? evaluateName = null)
        => new(name, value, type, reference, evaluateName);

    [Fact]
    public async Task Objects_show_their_ToString_override_and_hide_noise()
    {
        var debugger = new FakeDebugger();
        debugger.Locals.Add(Var("person", "{Person}", "Person", 1, "person"));
        debugger.Locals.Add(Var("plain", "{Thing}", "MyApp.Thing", 2, "plain"));
        debugger.Value("person.ToString()", "\"Person Ada\"", "string");
        debugger.Value("plain.ToString()", "\"MyApp.Thing\"", "string");
        debugger.Children[1] =
        [
            Var("Name", "\"Ada\"", "string", 0, "person.Name"),
            Var("Age", "36", "int", 0, "person.Age"),
            Var("System.Collections.IList.IsReadOnly", "false", "bool"),
            Var("Item", "{System.Reflection.TargetParameterCountException}", "System.Reflection.TargetParameterCountException", 9),
            Var("Static members", "", "", 3, "Person"),
        ];
        var provider = new DebugValueProvider(debugger, frameId: 1);

        var locals = await provider.LocalsAsync();
        Assert.Equal("{Person Ada}", locals[0].Display);
        Assert.Equal("{Thing}", locals[1].Display);   // not overridden: the adapter's text

        var members = await provider.ChildrenAsync(locals[0]);
        Assert.Equal(["Name", "Age", "Static members"], members.Select(member => member.Name));
        Assert.Equal(DebugValueKind.Text, members[0].Kind);
        Assert.Equal("person.Age", members[1].Expression);
        Assert.Equal(DebugValueKind.Group, members[2].Kind);
    }

    [Fact]
    public async Task A_list_shows_count_and_only_its_items()
    {
        var debugger = new FakeDebugger();
        debugger.Value("people", "{System.Collections.Generic.List<int>}", "System.Collections.Generic.List<int>", 1);
        debugger.Value("people.Count", "2");
        debugger.Children[1] =
        [
            Var("_items", "{int[4]}", "int[]", 2, "people._items"),
            Var("_size", "2", "int"),
            Var("Count", "2", "int"),
        ];
        debugger.Children[2] = [Var("[0]", "7", "int"), Var("[1]", "8", "int"), Var("[2]", "0", "int"), Var("[3]", "0", "int")];
        var provider = new DebugValueProvider(debugger, 1);

        var list = await provider.EvaluateAsync("people", "hover");
        Assert.Equal("Count = 2", list.Display);
        Assert.Equal(DebugValueKind.Collection, list.Kind);

        var items = await provider.ChildrenAsync(list);
        Assert.Equal(["[0]", "[1]", "Raw View"], items.Select(item => item.Name));
        Assert.Equal("people[1]", items[1].Expression);
        Assert.Equal("8", items[1].Display);
    }

    [Fact]
    public async Task A_dictionary_lists_used_entries_by_key()
    {
        var debugger = new FakeDebugger();
        debugger.Value("ages", "{Dictionary}", "System.Collections.Generic.Dictionary<string, int>", 1);
        debugger.Value("ages.Count", "2");
        debugger.Children[1] = [Var("_entries", "{Entry[3]}", "Entry[]", 2), Var("_count", "3", "int"), Var("_freeCount", "1", "int")];
        debugger.Children[2] = [Var("[0]", "{Entry}", "Entry", 10), Var("[1]", "{Entry}", "Entry", 11), Var("[2]", "{Entry}", "Entry", 12)];
        debugger.Children[10] = [Var("next", "-1", "int"), Var("key", "\"Ada\"", "string"), Var("value", "36", "int")];
        debugger.Children[11] = [Var("next", "-3", "int"), Var("key", "null", "string"), Var("value", "0", "int")];   // freed
        debugger.Children[12] = [Var("next", "0", "int"), Var("key", "\"Bob\"", "string"), Var("value", "40", "int")];
        var provider = new DebugValueProvider(debugger, 1);

        var items = await provider.ChildrenAsync(await provider.EvaluateAsync("ages", "hover"));
        Assert.Equal(["[\"Ada\"]", "[\"Bob\"]", "Raw View"], items.Select(item => item.Name));
        Assert.Equal("ages[\"Bob\"]", items[1].Expression);
        Assert.Equal("40", items[1].Display);
    }

    [Fact]
    public async Task Framework_private_fields_go_under_non_public_members()
    {
        var debugger = new FakeDebugger();
        debugger.Value("when", "{System.DateTime}", "System.DateTime", 1);
        debugger.Value("when.ToString()", "\"19/10/2026 14:30:00\"", "string");
        debugger.Children[1] = [Var("_dateData", "1", "ulong"), Var("Day", "19", "int"), Var("Hour", "14", "int")];
        var provider = new DebugValueProvider(debugger, 1);

        var when = await provider.EvaluateAsync("when", "hover");
        Assert.Equal("{19/10/2026 14:30:00}", when.Display);
        var members = await provider.ChildrenAsync(when);
        Assert.Equal(["Day", "Hour", "Non-Public members"], members.Select(member => member.Name));
        Assert.Equal(["_dateData"], (await provider.ChildrenAsync(members[2])).Select(member => member.Name));
    }

    [Fact]
    public async Task Long_arrays_stop_with_a_more_row()
    {
        var debugger = new FakeDebugger();
        debugger.Value("big", "{int[1500]}", "int[]", 1);
        debugger.Children[1] = Enumerable.Range(0, 1500).Select(i => Var($"[{i}]", i.ToString(), "int")).ToList();
        var provider = new DebugValueProvider(debugger, 1);

        var big = await provider.EvaluateAsync("big", "hover");
        Assert.Equal(1500, big.Count);
        var items = await provider.ChildrenAsync(big);
        Assert.Equal(DebugValueProvider.MaxChildren + 1, items.Count);
        Assert.Equal(DebugValueKind.More, items[^1].Kind);
        Assert.Equal("big[999]", items[999].Expression);
    }

    [Fact]
    public async Task Tables_lay_out_objects_values_and_grids()
    {
        var debugger = new FakeDebugger();
        debugger.Value("numbers", "{int[2]}", "int[]", 1);
        debugger.Children[1] = [Var("[0]", "1", "int"), Var("[1]", "2", "int")];
        debugger.Value("grid", "{int[2, 2]}", "int[,]", 2);
        debugger.Children[2] = [Var("[0, 0]", "1", "int"), Var("[0, 1]", "2", "int"), Var("[1, 0]", "3", "int"), Var("[1, 1]", "4", "int")];
        debugger.Value("people", "{Person[2]}", "Person[]", 3);
        debugger.Children[3] = [Var("[0]", "{Person}", "Person", 4), Var("[1]", "{Person}", "Person", 5)];
        debugger.Children[4] = [Var("Name", "\"Ada\"", "string"), Var("Age", "36", "int")];
        debugger.Children[5] = [Var("Name", "\"Bob\"", "string"), Var("Age", "40", "int"), Var("Email", "\"b@x\"", "string")];
        var provider = new DebugValueProvider(debugger, 1);

        var values = await provider.TableAsync(await provider.EvaluateAsync("numbers", "hover"));
        Assert.Equal(["Index", "Value"], values.Columns);
        Assert.Equal(["[1]", "2"], values.Rows[1]);

        var grid = await provider.TableAsync(await provider.EvaluateAsync("grid", "hover"));
        Assert.Equal(["", "[0]", "[1]"], grid.Columns);
        Assert.Equal(["[1]", "3", "4"], grid.Rows[1]);

        var people = await provider.TableAsync(await provider.EvaluateAsync("people", "hover"));
        Assert.Equal(["Index", "Name", "Age", "Email"], people.Columns);
        Assert.Equal(["[0]", "\"Ada\"", "36", ""], people.Rows[0]);
        Assert.Equal(["[1]", "\"Bob\"", "40", "\"b@x\""], people.Rows[1]);
    }

    [Fact]
    public async Task Debugger_display_formats_members()
    {
        var provider = new DebugValueProvider(new FakeDebugger
        {
            Values =
            {
                ["home.City"] = new DebugVariable("home.City", "\"London\"", "string", 0),
                ["home.Street"] = new DebugVariable("home.Street", "\"1 Main St\"", "string", 0),
            }
        }, 1);
        Assert.Equal("London, \"1 Main St\"", await provider.FormatDisplayAsync("{City,nq}, {Street}", "home", default));
        Assert.Null(await provider.FormatDisplayAsync("{City + Street}", "home", default));
    }

    [Fact]
    public async Task Errors_and_edits()
    {
        var debugger = new FakeDebugger();
        debugger.Value("total", "32");
        var provider = new DebugValueProvider(debugger, 1);

        var missing = await provider.EvaluateAsync("nope", "watch");
        Assert.Equal(DebugValueKind.Error, missing.Kind);
        Assert.Contains("does not exist", missing.Display);

        var total = await provider.EvaluateAsync("total", "watch");
        Assert.True(total.CanEdit);
        Assert.Equal("99", (await provider.SetValueAsync(total, "99")).Display);
    }

    [Fact]
    public async Task Enumerables_get_a_results_view_that_enumerates_when_expanded()
    {
        var debugger = new FakeDebugger();
        debugger.Value("query", "{SelectIterator}", "System.Linq.Enumerable.SelectIterator<int, int>", 1);
        debugger.Children[1] = [Var("_source", "{int[2]}", "int[]", 2)];
        debugger.Value("query.GetType().GetInterface(\"IEnumerable`1\")", "{System.RuntimeType}", "System.RuntimeType", 5);
        debugger.Value("query.ToArray()", "{int[2]}", "int[]", 3);
        debugger.Children[3] = [Var("[0]", "2", "int"), Var("[1]", "4", "int")];
        var provider = new DebugValueProvider(debugger, 1);

        var members = await provider.ChildrenAsync(await provider.EvaluateAsync("query", "hover"));
        var results = members[^1];
        Assert.Equal("Results View", results.Name);
        Assert.True(results.CanVisualize);
        Assert.DoesNotContain("query.ToArray()", debugger.Evaluated);   // not until it's expanded

        var items = await provider.ChildrenAsync(results);
        Assert.Equal(["2", "4"], items.Select(item => item.Display));
        Assert.Equal("query.ToArray()[1]", items[1].Expression);
    }

    [Theory]
    [InlineData("Person Ada", "Person", true)]
    [InlineData("Person", "Person", false)]
    [InlineData("MyApp.Person", "MyApp.Person", false)]
    [InlineData("System.Collections.Generic.List`1[Person]", "System.Collections.Generic.List<Person>", false)]
    [InlineData("Outer+Inner", "Outer.Inner", false)]
    [InlineData("Person { Name = Ada }", "Person", true)]
    public void ToString_override_detection(string toString, string type, bool overridden)
        => Assert.Equal(overridden, DebugValueProvider.IsOverridden(toString, type));

    [Theory]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("\"\\u0041\"", "A")]
    [InlineData("42", null)]
    public void Unquote(string display, string? text) => Assert.Equal(text, DebugValueProvider.Unquote(display));

    [Theory]
    [InlineData("        var city = person.Home.City;", "City", "person.Home.City")]
    [InlineData("        var city = person.Home.City;", "Home", "person.Home")]
    [InlineData("        var city = person.Home.City;", "person", "person")]
    [InlineData("        Console.WriteLine(this.total);", "total", "this.total")]
    [InlineData("        var n = people[i + 1].Name;", "Name", "people[i + 1].Name")]
    [InlineData("        var n = people?.Count;", "Count", "people.Count")]
    [InlineData("        var n = Get().Name;", "Name", null)]
    [InlineData("        person.Describe();", "Describe", null)]
    [InlineData("        var city = person.Home.City;", "var", null)]
    [InlineData("        return 42;", "42", null)]
    public void Expression_under_the_mouse(string line, string hovered, string? expected)
    {
        var column = line.IndexOf(hovered) + 1;
        Assert.Equal(expected, DebugExpressionLocator.Find(line, column)?.Expression);
    }
}
