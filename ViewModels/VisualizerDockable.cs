using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>How the text visualizer shows a string (Visual Studio's Text / JSON / XML visualizers).</summary>
public enum TextVisualizerFormat { Text, Json, Xml }

/// <summary>
/// A debugger visualizer as a document tab (Views/VisualizerView), like Visual Studio's magnifier:
///   - a collection (array, List, Dictionary, a LINQ Results View...) as a sortable table: one row per item, a
///     column per member for objects, a grid for a 2D array; filter box; Copy as CSV;
///   - a string as text, formatted as JSON or XML when it is one.
/// The values are read once (objects row by row, with progress); they're kept after the debuggee moves on, and
/// the tab says so. Opened by VisualizerManager; one tab per expression, refreshed when opened again.
/// </summary>
public sealed partial class VisualizerDockable : PageDockable
{
    private readonly IClipboardService _clipboard;
    private CancellationTokenSource _load = new();
    private List<string[]> _allRows = [];

    public VisualizerDockable(string expression, IClipboardService clipboard)
    {
        _clipboard = clipboard;
        Expression = expression;
        Id = IdFor(expression);
        Title = $"Visualizer: {expression}";
        CanPin = false;
    }

    public static string IdFor(string expression) => "visualizer:" + expression;

    public string Expression { get; }

    [ObservableProperty] public partial bool IsTable { get; set; }
    [ObservableProperty] public partial bool IsText { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    /// <summary>The debuggee has run since these values were read.</summary>
    [ObservableProperty] public partial bool IsStale { get; set; }

    // ---------------------------------------------------------------- table

    /// <summary>Column headers (Views/VisualizerView builds the grid's columns from them).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> Columns { get; set; } = [];

    /// <summary>The rows shown (filtered); each is the cells' text, in column order (arrays: the grid binds [i]).</summary>
    public ObservableCollection<string[]> Rows { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => ApplyFilter();

    // ---------------------------------------------------------------- text

    private string _rawText = "";

    [ObservableProperty] public partial string Text { get; set; } = "";

    [ObservableProperty] public partial TextVisualizerFormat Format { get; set; }

    public IReadOnlyList<TextVisualizerFormat> Formats { get; } = Enum.GetValues<TextVisualizerFormat>();

    partial void OnFormatChanged(TextVisualizerFormat value) => Text = Formatted(_rawText, value, out _);

    // ---------------------------------------------------------------- loading

    /// <summary>Read <paramref name="value"/> (again) at the current stop.</summary>
    public async Task LoadAsync(DebugValue value, DebugValueProvider provider)
    {
        _load.Cancel();
        _load = new CancellationTokenSource();
        var ct = _load.Token;
        IsStale = false;

        if (value.Kind == DebugValueKind.Text)
        {
            (IsText, IsTable) = (true, false);
            _rawText = DebugValueProvider.Unquote(value.Display) ?? value.Display;
            var detected = Detect(_rawText);
            Format = detected;
            Text = Formatted(_rawText, detected, out _);
            Status = $"{_rawText.Length:N0} characters";
            return;
        }

        (IsText, IsTable) = (false, true);
        IsLoading = true;
        Status = "Reading the items...";
        Rows.Clear();
        _allRows = [];
        try
        {
            var progress = new Progress<int>(rows => { if (!ct.IsCancellationRequested) Status = $"Reading the items... {rows:N0}"; });
            var table = await provider.TableAsync(value, progress, ct);
            if (ct.IsCancellationRequested)
                return;
            Columns = table.Columns;
            _allRows = table.Rows.Select(row => row.ToArray()).ToList();
            ApplyFilter();
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

    private void ApplyFilter()
    {
        Rows.Clear();
        var filter = Filter.Trim();
        foreach (var row in _allRows)
            if (filter.Length == 0 || row.Any(cell => cell.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                Rows.Add(row);
    }

    /// <summary>The visible rows as CSV (Excel and friends open it), like VS's Export to CSV.</summary>
    [RelayCommand]
    private Task CopyAsCsv()
    {
        static string Cell(string value) => value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", Columns.Select(Cell)));
        foreach (var row in Rows)
            csv.AppendLine(string.Join(",", row.Select(Cell)));
        return _clipboard.SetTextAsync(csv.ToString());
    }

    [RelayCommand]
    private Task CopyText() => _clipboard.SetTextAsync(Text);

    // ---------------------------------------------------------------- text formats

    private static TextVisualizerFormat Detect(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return Formatted(text, TextVisualizerFormat.Json, out var ok) is not null && ok ? TextVisualizerFormat.Json : TextVisualizerFormat.Text;
        if (trimmed.StartsWith('<'))
            return Formatted(text, TextVisualizerFormat.Xml, out var ok) is not null && ok ? TextVisualizerFormat.Xml : TextVisualizerFormat.Text;
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
