using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Debugging;

public sealed partial class DebugValueProvider
{
    /// <summary>Rows the table visualizer reads (objects take a request per row, a few ms each).</summary>
    public const int MaxTableRows = 2000;

    /// <summary>
    /// A collection as a table, like Visual Studio's IEnumerable visualizer: one row per item; for objects, a column
    /// per member (public ones, in order of first appearance); for simple values one Value column; a 2D array as a
    /// grid (rows by columns).
    /// </summary>
    /// <param name="progress">Rows read so far (objects are read one by one).</param>
    public async Task<DebugTable> TableAsync(DebugValue collection, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var items = (await ItemsAsync(collection, MaxTableRows, ct)).Where(item => item.Kind != DebugValueKind.More).ToList();
        var total = collection.Count ?? items.Count;

        if (TwoDimensional(items) is { } grid)
            return grid with { TotalCount = total };

        if (items.All(item => item.Kind is DebugValueKind.Value or DebugValueKind.Text or DebugValueKind.Error || !item.HasChildren))
        {
            var refined = await RefineAllAsync(items, ct);
            return new DebugTable(["Index", "Value"], refined.Select(item => (IReadOnlyList<string>)[item.Name, item.Display]).ToList(), total);
        }

        var columns = new List<string>();
        var rows = new List<Dictionary<string, string>>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count % 25 == 0)
                progress?.Report(rows.Count);
            var cells = new Dictionary<string, string> { [""] = item.Name };
            if (item.Children is MemberChildren)
            {
                var members = await MembersAsync((MemberChildren)item.Children, item.Expression, ct);
                foreach (var member in members.Where(member => member.Kind != DebugValueKind.Group))
                {
                    var shown = rows.Count < RefineLimit ? await RefineAsync(member, ct) : member;
                    if (!columns.Contains(member.Name))
                        columns.Add(member.Name);
                    cells[member.Name] = shown.Display;
                }
            }
            else
            {
                if (!columns.Contains("Value"))
                    columns.Insert(0, "Value");
                cells["Value"] = (rows.Count < RefineLimit ? await RefineAsync(item, ct) : item).Display;
            }
            rows.Add(cells);
        }

        return new DebugTable(
            ["Index", .. columns],
            rows.Select(cells => (IReadOnlyList<string>)[cells[""], .. columns.Select(column => cells.GetValueOrDefault(column, ""))]).ToList(),
            total);
    }

    /// <summary>A rectangular array's "[r, c]" items as a grid: a row per r, a column per c.</summary>
    private static DebugTable? TwoDimensional(List<DebugValue> items)
    {
        if (items.Count == 0 || !items[0].Name.Contains(','))
            return null;
        var cells = new Dictionary<(string Row, string Column), string>();
        var rowNames = new List<string>();
        var columnNames = new List<string>();
        foreach (var item in items)
        {
            var parts = item.Name.Trim('[', ']').Split(',');
            if (parts.Length != 2)
                return null;
            var (row, column) = (parts[0].Trim(), parts[1].Trim());
            if (!rowNames.Contains(row)) rowNames.Add(row);
            if (!columnNames.Contains(column)) columnNames.Add(column);
            cells[(row, column)] = item.Display;
        }
        return new DebugTable(
            ["", .. columnNames.Select(column => $"[{column}]")],
            rowNames.Select(row => (IReadOnlyList<string>)[$"[{row}]", .. columnNames.Select(column => cells.GetValueOrDefault((row, column), ""))]).ToList(),
            items.Count);
    }
}
