using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>How the text visualizer shows a string: as it is, pretty-printed, or (a JSON array) as a table.</summary>
public enum TextVisualizerFormat { Text, Json, Xml, Table }

/// <summary>
/// A debugger visualizer as a document tab (Views/VisualizerView), like Visual Studio's magnifier:
///   - a collection (array, List, Dictionary, a LINQ Results View...) as a sortable table: one row per item, a
///     column per member for objects, a grid for a 2D array; filter box; Open in Excel / as CSV. Objects are read one request
///     per row (~2 ms each in netcoredbg), so rows stream in while the rest load;
///   - a string as text, pretty-printed as JSON or XML, or (a JSON array, e.g. a DB / API response) as a typed table:
///     read in one evaluate, so thousands of rows show at once (Core's JsonTables).
/// Cells are typed per column (numbers, dates, bools), so sorting is numeric / by date. The values are kept after
/// the debuggee moves on, and the tab says so. Opened by VisualizerManager; one tab per expression.
/// </summary>
public sealed partial class VisualizerDockable : PageDockable
{
    private readonly IClipboardService _clipboard;
    private readonly ExcelService _excel;
    private CancellationTokenSource _load = new();
    private List<object?[]> _allRows = [];
    private Type[] _columnTypes = [];
    private TypedTable? _jsonTable;
    private string _rawText = "";

    public VisualizerDockable(string expression, IClipboardService clipboard, ExcelService excel)
    {
        _clipboard = clipboard;
        _excel = excel;
        Expression = expression;
        Id = IdFor(expression);
        Title = $"Visualizer: {expression}";
        CanPin = false;
    }

    public static string IdFor(string expression) => "visualizer:" + expression;

    public string Expression { get; }

    /// <summary>The grid is shown (a collection, or a JSON string in Table format).</summary>
    [ObservableProperty] public partial bool IsTable { get; set; }

    /// <summary>The text editor is shown (a string, not as a table).</summary>
    [ObservableProperty] public partial bool IsText { get; set; }

    /// <summary>A string: the format box is shown.</summary>
    [ObservableProperty] public partial bool IsString { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    /// <summary>The debuggee has run since these values were read.</summary>
    [ObservableProperty] public partial bool IsStale { get; set; }

    // ---------------------------------------------------------------- table

    /// <summary>Column headers (Views/VisualizerView builds the grid's columns from them).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> Columns { get; set; } = [];

    /// <summary>The rows shown (filtered); each is the cells in column order, typed (the grid binds [i]).</summary>
    public ObservableCollection<object?[]> Rows { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => ShowRows(_allRows);

    // ColumnTypes / Typed / Fits below type a debugger table's display texts.

    // ---------------------------------------------------------------- text

    [ObservableProperty] public partial string Text { get; set; } = "";

    [ObservableProperty] public partial TextVisualizerFormat Format { get; set; }

    /// <summary>The formats this string can be shown in (Table only for a JSON array).</summary>
    [ObservableProperty] public partial IReadOnlyList<TextVisualizerFormat> Formats { get; set; } = [];

    partial void OnFormatChanged(TextVisualizerFormat value)
    {
        if (!IsString)
            return;
        if (value == TextVisualizerFormat.Table && _jsonTable is { } table)
        {
            (IsText, IsTable) = (false, true);
            SetTypedTable(table.Columns, table.Rows.ToList());
            Status = $"{table.Rows.Count:N0} rows ({table.Source})";
            return;
        }
        (IsText, IsTable) = (true, false);
        Text = Formatted(_rawText, value, out _);
        Status = $"{_rawText.Length:N0} characters";
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Read <paramref name="value"/> (again) at the current stop.</summary>
    public async Task LoadAsync(DebugValue value, DebugValueProvider provider)
    {
        _load.Cancel();
        _load = new CancellationTokenSource();
        var ct = _load.Token;
        IsStale = false;
        Filter = "";

        if (value.Kind == DebugValueKind.Text)
        {
            IsString = true;
            _rawText = DebugValueProvider.Unquote(value.Display) ?? value.Display;
            _jsonTable = await Task.Run(() => JsonTables.TryRead(_rawText), ct);
            Formats = _jsonTable is null
                ? [TextVisualizerFormat.Text, TextVisualizerFormat.Json, TextVisualizerFormat.Xml]
                : [TextVisualizerFormat.Table, TextVisualizerFormat.Json, TextVisualizerFormat.Text];
            var format = _jsonTable is not null ? TextVisualizerFormat.Table : Detect(_rawText);
            if (Format == format)
                OnFormatChanged(format);
            else
                Format = format;
            return;
        }

        (IsString, IsText, IsTable) = (false, false, true);
        IsLoading = true;
        Status = "Reading the items...";
        _raw = [];
        SetTypedTable([], []);
        try
        {
            // Straight from the paused process's memory: the whole collection, typed, in a fraction of a second.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (await Task.Run(() => provider.MemoryTableAsync(value, ct), ct) is { } memory)
            {
                if (ct.IsCancellationRequested)
                    return;
                SetTypedTable(memory.Columns, memory.Rows.ToList());
                Status = (memory.Rows.Count < memory.TotalCount
                    ? $"Showing the first {memory.Rows.Count:N0} of {memory.TotalCount:N0} items"
                    : $"{memory.TotalCount:N0} items") + $" (read from memory in {watch.Elapsed.TotalSeconds:0.0} s)";
                return;
            }

            // Otherwise through the debugger, one object per request: rows arrive in chunks, off the UI thread.
            var read = 0;
            var chunks = new Progress<DebugTable>(chunk =>
            {
                if (ct.IsCancellationRequested)
                    return;
                AddDebugRows(chunk.Columns, chunk.Rows);
                read += chunk.Rows.Count;
                Status = $"Reading the items... {read:N0} of {chunk.TotalCount:N0}";
            });
            var table = await Task.Run(() => provider.TableAsync(value, chunks, ct), ct);
            if (ct.IsCancellationRequested)
                return;
            // Anything not reported yet (the last chunk), or the whole table when it came in one go.
            if (table.Columns.SequenceEqual(Columns) && table.Rows.Count >= _raw.Count)
                AddDebugRows(table.Columns, table.Rows.Skip(_raw.Count).ToList());
            else
            {
                _raw = [];
                AddDebugRows(table.Columns, table.Rows);
            }
            Status = table.Rows.Count < table.TotalCount
                ? $"Showing the first {table.Rows.Count:N0} of {table.TotalCount:N0} items"
                : $"{table.TotalCount:N0} items";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't read the items: {(ex is DebugAdapterException adapter ? adapter.AdapterMessage : ex.Message)}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    // A debugger table's cells as the debugger shows them, kept so a column can be re-typed when needed.
    private List<string[]> _raw = [];

    /// <summary>
    /// Add rows of display texts from the debugger. Columns are typed from the rows so far (all numbers -> numbers,
    /// true/false -> bool), so a column never mixes types and sorts properly; new rows are appended as they come,
    /// and only a new column or a value that doesn't fit its column's type re-types (and re-shows) the table.
    /// </summary>
    private void AddDebugRows(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var added = rows.Select(row => row.ToArray()).ToList();
        if (!columns.SequenceEqual(Columns))
        {
            // New members turned up: line the earlier rows up with the new columns, then re-type everything.
            var old = Columns.ToList();
            _raw = _raw.Select(row => columns.Select(column => old.IndexOf(column) is var at and >= 0 && at < row.Length ? row[at] : "").ToArray()).ToList();
            _raw.AddRange(added);
            Retype(columns);
            return;
        }

        _raw.AddRange(added);
        var fits = added.All(row => row.Select((cell, c) => c < _columnTypes.Length && Fits(cell, _columnTypes[c])).All(ok => ok));
        if (!fits)
        {
            Retype(columns);
            return;
        }
        var typed = added.Select(row => row.Select((cell, c) => Typed(cell, _columnTypes[c])).ToArray()).ToList();
        _allRows.AddRange(typed);
        AddRows(typed);
    }

    private void Retype(IReadOnlyList<string> columns)
    {
        var types = ColumnTypes(columns.Count, _raw);
        SetTypedTable(columns, _raw.Select(row => row.Select((cell, c) => Typed(cell, types[c])).ToArray()).ToList());
        _columnTypes = types;
    }

    /// <summary>Show a table whose cells are already typed (a JSON table, or a re-typed debugger table).</summary>
    private void SetTypedTable(IReadOnlyList<string> columns, List<object?[]> rows)
    {
        _columnTypes = [];
        _allRows = rows;
        Columns = columns;
        ShowRows(rows);
    }

    private void ShowRows(IEnumerable<object?[]> rows)
    {
        Rows.Clear();
        AddRows(rows);
    }

    private void AddRows(IEnumerable<object?[]> rows)
    {
        var filter = Filter.Trim();
        foreach (var row in rows)
            if (filter.Length == 0 || row.Any(cell => Cell(cell).Contains(filter, StringComparison.OrdinalIgnoreCase)))
                Rows.Add(row);
    }

    /// <summary>"[12]" (an item's index) -> "12", so the Index column sorts as numbers.</summary>
    private static string Unbracket(string cell) => cell.Length > 2 && cell[0] == '[' && cell[^1] == ']' ? cell[1..^1] : cell;

    private static bool Fits(string cell, Type type)
        => type == typeof(string) || cell.Length == 0 || cell == "null"
           || type == typeof(long) && long.TryParse(Unbracket(cell), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
           || type == typeof(decimal) && decimal.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
           || type == typeof(bool) && cell is "true" or "false";

    /// <summary>Per column: long / decimal / bool when every non-empty cell reads as one, else text.</summary>
    private static Type[] ColumnTypes(int count, List<string[]> rows)
    {
        var types = new Type[count];
        for (var c = 0; c < count; c++)
        {
            var cells = rows.Select(row => c < row.Length ? row[c] : null).Where(cell => !string.IsNullOrEmpty(cell) && cell != "null").ToList();
            types[c] = cells.Count == 0 ? typeof(string)
                : cells.All(cell => long.TryParse(Unbracket(cell!), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) ? typeof(long)
                : cells.All(cell => decimal.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) ? typeof(decimal)
                : cells.All(cell => cell is "true" or "false") ? typeof(bool)
                : typeof(string);
        }
        return types;
    }

    private static object? Typed(string? cell, Type type)
    {
        if (cell is null || cell == "null" || type == typeof(string) && cell.Length == 0)
            return type == typeof(string) ? cell : null;
        if (type == typeof(long)) return long.Parse(Unbracket(cell), NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return decimal.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return cell == "true";
        return cell;
    }

    private static string Cell(object? cell) => cell switch
    {
        null => "",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => cell.ToString() ?? ""
    };

    /// <summary>The visible rows in a new workbook, opened in the spreadsheet app (VS's Export to Excel).</summary>
    [RelayCommand]
    private void OpenInExcel() => Export(() => _excel.OpenAsExcel(Expression, Columns, Rows));

    /// <summary>The visible rows as a .csv file, opened in the app the system uses for CSV.</summary>
    [RelayCommand]
    private void OpenAsCsv() => Export(() => _excel.OpenAsCsv(Expression, Columns, Rows));

    private void Export(Func<string> write)
    {
        try
        {
            ExportStatus = "Opened " + write();
        }
        catch (Exception ex)
        {
            ExportStatus = "Couldn't export: " + ex.Message;
        }
    }

    /// <summary>Where the last export went (or why it failed), shown in the toolbar.</summary>
    [ObservableProperty] public partial string ExportStatus { get; set; } = "";

    [RelayCommand]
    private Task CopyText() => _clipboard.SetTextAsync(Text);

    // ---------------------------------------------------------------- text formats

    private static TextVisualizerFormat Detect(string text)
    {
        var trimmed = text.TrimStart();
        if ((trimmed.StartsWith('{') || trimmed.StartsWith('[')) && Formatted(text, TextVisualizerFormat.Json, out var json) is not null && json)
            return TextVisualizerFormat.Json;
        if (trimmed.StartsWith('<') && Formatted(text, TextVisualizerFormat.Xml, out var xml) is not null && xml)
            return TextVisualizerFormat.Xml;
        return TextVisualizerFormat.Text;
    }

    /// <summary>The text pretty-printed as JSON / XML; as it is when it isn't valid (<paramref name="ok"/> false).</summary>
    public static string Formatted(string text, TextVisualizerFormat format, out bool ok)
    {
        ok = true;
        try
        {
            return format switch
            {
                TextVisualizerFormat.Json => JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, new JsonSerializerOptions { WriteIndented = true }),
                TextVisualizerFormat.Xml => XDocument.Parse(text).ToString(),
                _ => text
            };
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException)
        {
            ok = false;
            return text;
        }
    }

    public override void Dispose() => _load.Cancel();
}
