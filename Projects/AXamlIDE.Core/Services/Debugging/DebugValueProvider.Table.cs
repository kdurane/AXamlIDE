using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Debugging;

public sealed partial class DebugValueProvider
{
    /// <summary>Rows the table visualizer reads at most (objects take a request per row, ~2 ms each, so they stream).</summary>
    public const int MaxTableRows = 100_000;

    /// <summary>Rows whose cells get the evaluated display (ToString, [DebuggerDisplay]); later rows show the raw one.</summary>
    private const int TableRefineRows = 20;

    /// <summary>How often a streaming table is reported (rows).</summary>
    private const int TableReportEvery = 250;

    /// <summary>
    /// A collection as a table, like Visual Studio's IEnumerable visualizer: one row per item; for objects, a column
    /// per member (public ones, in order of first appearance); for simple values one Value column; a 2D array as a
    /// grid (rows by columns).
    /// </summary>
    /// <param name="partial">Objects are read one request per row (netcoredbg: ~2-4 ms each), so the rows come in
    /// chunks: the columns so far and the rows read since the last report, after the first rows and then every
    /// <see cref="TableReportEvery"/>. The visualizer shows them while the rest load.</param>
    public async Task<DebugTable> TableAsync(DebugValue collection, IProgress<DebugTable>? partial = null, CancellationToken ct = default)
    {
        var items = (await ItemsAsync(collection, MaxTableRows, ct).ConfigureAwait(false)).Where(item => item.Kind != DebugValueKind.More).ToList();
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
        var reported = 0;
        DebugTable Snapshot(int from) => new(
            ["Index", .. columns],
            rows.Skip(from).Select(cells => (IReadOnlyList<string>)[cells[""], .. columns.Select(column => cells.GetValueOrDefault(column, ""))]).ToList(),
            total);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (partial is not null && (rows.Count == 50 || rows.Count > 0 && rows.Count % TableReportEvery == 0))
            {
                partial.Report(Snapshot(reported));
                reported = rows.Count;
            }

            var cells = new Dictionary<string, string> { [""] = item.Name };
            var refine = rows.Count < TableRefineRows;
            if (item.Children is MemberChildren memberChildren)
            {
                var members = await MembersAsync(memberChildren, item.Expression, ct).ConfigureAwait(false);
                foreach (var member in members.Where(member => member.Kind != DebugValueKind.Group))
                {
                    var shown = refine ? await RefineAsync(member, ct).ConfigureAwait(false) : member;
                    if (!columns.Contains(member.Name))
                        columns.Add(member.Name);
                    cells[member.Name] = shown.Display;
                }
            }
            else
            {
                if (!columns.Contains("Value"))
                    columns.Insert(0, "Value");
                cells["Value"] = (refine ? await RefineAsync(item, ct).ConfigureAwait(false) : item).Display;
            }
            rows.Add(cells);
        }

        return Snapshot(0);
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
