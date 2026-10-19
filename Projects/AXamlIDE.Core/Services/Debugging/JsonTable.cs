using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Debugging;

/// <summary>
/// A JSON array laid out as a table with typed columns, for the visualizer: a string holding a DB / API response
/// is read in one evaluate and shown like a DataTable, sorting numbers as numbers and dates as dates.
/// </summary>
/// <param name="Columns">"#" (the row's index), then one per property, in order of first appearance.</param>
/// <param name="Rows">Cells typed per column: long, decimal, double, bool, DateTime, Guid, string; null for null/missing.</param>
/// <param name="Path">Where the array was ("$" for the root, "$.data" for a property).</param>
public sealed record JsonTable(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, string Path);

public static class JsonTables
{
    /// <summary>
    /// The table in <paramref name="json"/>: the root array, or else the largest array among the root object's
    /// properties (an API's {"data": [...], "total": 8000}). Null when it isn't JSON or has no array.
    /// </summary>
    public static JsonTable? TryRead(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('[' or '{'))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var (array, path) = FindArray(document.RootElement);
            return array is { } found ? Read(found, path) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (JsonElement? Array, string Path) FindArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return (root, "$");
        if (root.ValueKind != JsonValueKind.Object)
            return (null, "");

        JsonElement? best = null;
        var bestPath = "";
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > (best?.GetArrayLength() ?? -1))
                (best, bestPath) = (property.Value, "$." + property.Name);
        return (best, bestPath);
    }

    private static JsonTable Read(JsonElement array, string path)
    {
        var elements = array.EnumerateArray().ToList();
        var objects = elements.Count > 0 && elements.All(element => element.ValueKind is JsonValueKind.Object or JsonValueKind.Null);

        // Columns: properties in order of first appearance (or one Value column for an array of values).
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (objects)
        {
            foreach (var element in elements.Where(element => element.ValueKind == JsonValueKind.Object))
                foreach (var property in element.EnumerateObject())
                    if (seen.Add(property.Name))
                        columns.Add(property.Name);
        }
        else
        {
            columns.Add("Value");
        }

        JsonElement? Cell(JsonElement element, string column)
            => !objects ? element : element.ValueKind == JsonValueKind.Object && element.TryGetProperty(column, out var value) ? value : null;

        var types = columns.Select(column => ColumnType(elements.Select(element => Cell(element, column)))).ToArray();
        var rows = new List<object?[]>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            var row = new object?[columns.Count + 1];
            row[0] = (long)i;
            for (var c = 0; c < columns.Count; c++)
                row[c + 1] = Cell(elements[i], columns[c]) is { } value ? Convert(value, types[c]) : null;
            rows.Add(row);
        }
        return new JsonTable(["#", .. columns], rows, path);
    }

    // ---------------------------------------------------------------- types (after the user's ConvertJsonElement)

    /// <summary>The one type every value in a column can be read as, so the column sorts properly.</summary>
    internal static Type ColumnType(IEnumerable<JsonElement?> cells)
    {
        var values = cells.Where(cell => cell is { ValueKind: not JsonValueKind.Null }).Select(cell => cell!.Value).ToList();
        if (values.Count == 0)
            return typeof(string);

        if (values.All(value => value.ValueKind == JsonValueKind.Number))
        {
            if (values.All(value => value.TryGetInt64(out _)))
                return typeof(long);
            return values.All(value => value.TryGetDecimal(out _)) ? typeof(decimal) : typeof(double);
        }
        if (values.All(value => value.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return typeof(bool);
        if (values.All(value => value.ValueKind == JsonValueKind.String))
        {
            var texts = values.Select(value => value.GetString()!).ToList();
            if (texts.All(LooksLikeDate))
                return typeof(DateTime);
            if (texts.All(text => Guid.TryParse(text, out _)))
                return typeof(Guid);
            return typeof(string);
        }
        if (values.All(value => DateObject(value) is not null))
            return typeof(DateTime);
        return typeof(string);
    }

    /// <summary>A cell as its column's type; objects and arrays as compact JSON text.</summary>
    internal static object? Convert(JsonElement element, Type type)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (type == typeof(long)) return element.GetInt64();
        if (type == typeof(decimal)) return element.GetDecimal();
        if (type == typeof(double)) return element.GetDouble();
        if (type == typeof(bool)) return element.GetBoolean();
        if (type == typeof(Guid)) return Guid.Parse(element.GetString()!);
        if (type == typeof(DateTime))
            return element.ValueKind == JsonValueKind.String
                ? DateTime.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : DateObject(element);
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            _ => element.ToString()
        };
    }

    /// <summary>ISO-style dates only ("2026-10-19", "2026-10-19T14:30:00Z"), not any string DateTime.Parse accepts.</summary>
    private static bool LooksLikeDate(string text)
        => text.Length >= 10 && char.IsDigit(text[0]) && text[4] == '-' && text[7] == '-'
           && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    /// <summary>
    /// A date written as an object, as some DB APIs do:
    /// {"isValidDateTime": true, "year": 2026, "month": 10, "day": 19, "hour": 14, "minute": 30, "second": 0}.
    /// </summary>
    private static DateTime? DateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("isValidDateTime", out var valid) || valid.ValueKind != JsonValueKind.True)
            return null;
        if (element.TryGetProperty("year", out var y) && element.TryGetProperty("month", out var mo) && element.TryGetProperty("day", out var d)
            && element.TryGetProperty("hour", out var h) && element.TryGetProperty("minute", out var mi) && element.TryGetProperty("second", out var s))
        {
            try
            {
                return new DateTime(y.GetInt32(), mo.GetInt32(), d.GetInt32(), h.GetInt32(), mi.GetInt32(), s.GetInt32());
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return null;
    }
}
