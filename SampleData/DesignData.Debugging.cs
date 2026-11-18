using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Editor;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.SampleData;

/// <summary>
/// The debugger's windows, paused in a made-up OrdersViewModel.Refresh: a fake debug adapter (<see cref="SampleDebugger"/>)
/// answers the same questions netcoredbg would, so Call Stack, Locals, Watch, the DataTip and the visualizer fill
/// themselves through their real code.
/// </summary>
public static partial class DesignData
{
    private static readonly Lazy<StopSnapshot> LazyStop = new(() =>
    {
        var debugger = new SampleDebugger();
        var stop = new StopSnapshot("breakpoint", 1,
        [
            new DebugFrame(1, "Contoso.App.ViewModels.OrdersViewModel.Refresh()", SamplePath("Contoso.App/ViewModels/OrdersViewModel.cs"), 57),
            new DebugFrame(2, "Contoso.App.ViewModels.OrdersViewModel.OnFilterChanged(string value)", SamplePath("Contoso.App/ViewModels/OrdersViewModel.cs"), 31),
            new DebugFrame(3, "CommunityToolkit.Mvvm.ComponentModel.ObservableObject.SetProperty<string>()", null, 0),
            new DebugFrame(4, "Avalonia.Threading.Dispatcher.Invoke()", null, 0),
            new DebugFrame(5, "Contoso.App.Program.Main(string[] args)", SamplePath("Contoso.App/Program.cs"), 12)
        ], new Dictionary<string, string> { ["Filter"] = "\"open\"" }, null, debugger, new DebuggerAttributeIndex(() => []));
        // Everything that listens for the stop has to exist before it's announced (the messenger only reaches
        // recipients already registered), as the app's do by the time anything is debugged.
        _ = Get<DebugSnapshotTracker>();
        _ = Get<CallStackViewModel>();
        _ = Get<LocalsViewModel>();
        _ = Get<WatchViewModel>();
        Get<RunViewModel>().IsDebugStopped = true;
        Send(new DebugSnapshotChangedMessage(stop));
        return stop;
    });

    /// <summary>Paused: the Locals, Watch and Call Stack windows (and the editor's debug state) have something to show.</summary>
    private static void Pause() => _ = LazyStop.Value;

    public static CallStackViewModel CallStack
    {
        get
        {
            Pause();
            return Get<CallStackViewModel>();
        }
    }

    public static LocalsViewModel Locals
    {
        get
        {
            Pause();
            return Get<LocalsViewModel>();
        }
    }

    public static WatchViewModel Watch => LazyWatch.Value;

    private static readonly Lazy<WatchViewModel> LazyWatch = new(() =>
    {
        Pause();
        var watch = Get<WatchViewModel>();
        foreach (var expression in (string[])["orders.Count", "order.Customer.Name", "order.Total * 1.2m", "missing"])
            Send(new AddWatchMessage(expression));
        return watch;
    });

    /// <summary>The value tree on its own (Locals and Watch are made of one).</summary>
    public static DebugValueTree DebugValues => Locals.Tree;

    public static CollectionDataTip CollectionDataTip => LazyDataTip.Value;

    private static readonly Lazy<CollectionDataTip> LazyDataTip = new(() =>
    {
        var stop = LazyStop.Value;
        var provider = new DebugValueProvider(stop.Debugger, 1, stop.Attributes);
        var orders = provider.EvaluateAsync("orders", "hover").GetAwaiter().GetResult();   // the fake answers at once
        return new CollectionDataTip(orders, provider, Get<IMessenger>(), () => { });
    });

    public static VisualizerDockable Visualizer => LazyVisualizer.Value;

    private static readonly Lazy<VisualizerDockable> LazyVisualizer = new(() =>
    {
        var stop = LazyStop.Value;
        var provider = new DebugValueProvider(stop.Debugger, 1, stop.Attributes);
        var json = provider.EvaluateAsync("ordersJson", "hover").GetAwaiter().GetResult();
        var page = new VisualizerDockable("ordersJson", Get<IClipboardService>(), Get<ExcelService>());
        _ = page.LoadAsync(json, provider);
        return page;
    });

    /// <summary>A debug adapter with a few values: what netcoredbg would say about them (see DebugValueProviderTests).</summary>
    private sealed class SampleDebugger : IDebugEvaluator
    {
        private readonly Dictionary<int, IReadOnlyList<DebugVariable>> _children = [];
        private readonly Dictionary<string, DebugVariable> _values = [];
        private readonly List<DebugVariable> _locals = [];

        public SampleDebugger()
        {
            Local("this", "{Contoso.App.ViewModels.OrdersViewModel}", "Contoso.App.ViewModels.OrdersViewModel", 1);
            Local("orders", "{System.Collections.Generic.List<Contoso.App.Models.Order>}", "System.Collections.Generic.List<Contoso.App.Models.Order>", 2);
            Local("order", "{Contoso.App.Models.Order}", "Contoso.App.Models.Order", 4);
            Local("filter", "\"open\"", "string");
            Local("count", "3", "int");
            Local("includeClosed", "false", "bool");
            Local("ordersJson", "\"[{\\\"id\\\":1041,\\\"customer\\\":\\\"Ada\\\",\\\"total\\\":120.5},{\\\"id\\\":1042,\\\"customer\\\":\\\"Grace\\\",\\\"total\\\":89},{\\\"id\\\":1043,\\\"customer\\\":null,\\\"total\\\":12.25}]\"", "string");

            _children[1] = [Var("Filter", "\"open\"", "string", 0, "this.Filter"), Var("IsBusy", "false", "bool", 0, "this.IsBusy"),
                Var("Orders", "{System.Collections.Generic.List<Contoso.App.Models.Order>}", "System.Collections.Generic.List<Contoso.App.Models.Order>", 2, "this.Orders")];
            Value("orders.Count", "3");
            _children[2] = [Var("_items", "{Contoso.App.Models.Order[4]}", "Contoso.App.Models.Order[]", 3, "orders._items"), Var("_size", "3", "int"), Var("Count", "3", "int")];
            _children[3] = [Var("[0]", "{Contoso.App.Models.Order}", "Contoso.App.Models.Order", 4), Var("[1]", "{Contoso.App.Models.Order}", "Contoso.App.Models.Order", 5),
                Var("[2]", "{Contoso.App.Models.Order}", "Contoso.App.Models.Order", 6), Var("[3]", "null", "Contoso.App.Models.Order")];
            Order(4, "orders[0]", 1041, "Ada", "120.5");
            Order(5, "orders[1]", 1042, "Grace", "89");
            Order(6, "orders[2]", 1043, null, "12.25");
            Value("order", "{Contoso.App.Models.Order}", "Contoso.App.Models.Order", 4);
            Value("order.ToString()", "\"Order 1041 (Ada)\"", "string");
            Value("order.Customer.Name", "\"Ada\"", "string");
            Value("order.Total * 1.2m", "144.6", "decimal");
            Value("orders", "{System.Collections.Generic.List<Contoso.App.Models.Order>}", "System.Collections.Generic.List<Contoso.App.Models.Order>", 2);
        }

        private void Order(int reference, string expression, int id, string? customer, string total)
        {
            _children[reference] =
            [
                Var("Id", id.ToString(), "int", 0, expression + ".Id"),
                Var("Customer", customer is null ? "null" : "{Contoso.App.Models.Customer}", "Contoso.App.Models.Customer", customer is null ? 0 : reference + 100, expression + ".Customer"),
                Var("Total", total, "decimal", 0, expression + ".Total")
            ];
            if (customer is not null)
                _children[reference + 100] = [Var("Name", $"\"{customer}\"", "string", 0, expression + ".Customer.Name")];
            Value(expression + ".ToString()", $"\"Order {id} ({customer ?? "no customer"})\"", "string");
        }

        private void Local(string name, string value, string type, int reference = 0)
        {
            _locals.Add(Var(name, value, type, reference, name));
            Value(name, value, type, reference);
        }

        private void Value(string expression, string value, string type = "int", int reference = 0)
            => _values[expression] = Var(expression, value, type, reference, expression);

        private static DebugVariable Var(string name, string value, string type, int reference = 0, string? evaluateName = null)
            => new(name, value, type, reference, evaluateName);

        public Task<IReadOnlyList<DebugVariable>> GetLocalsAsync(int frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DebugVariable>>(frameId == 1 ? _locals : []);

        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
            => Task.FromResult(_children.GetValueOrDefault(variablesReference, []));

        public Task<DebugEvaluation> EvaluateAsync(int frameId, string expression, string context, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(expression, out var value)
                ? new DebugEvaluation(value, null)
                : new DebugEvaluation(null, $"error CS0103: The name '{expression}' does not exist in the current context"));

        public Task<DebugEvaluation> SetExpressionAsync(int frameId, string expression, string value, CancellationToken ct = default)
            => Task.FromResult(new DebugEvaluation(null, "Values can't be changed in the designer."));

        public IReadOnlyCollection<string> LoadedModules => [];

        public int? ProcessId => null;
    }
}
