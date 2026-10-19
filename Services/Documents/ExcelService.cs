using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// Hands a table to the spreadsheet app: writes it as .xlsx (ClosedXML) or .csv to a file under the temp folder and
/// opens that with the system's default app (Excel, LibreOffice Calc...), like VS's "Export to Excel". Each export gets
/// its own file, since an open workbook is locked. Used by the debugger's visualizer (ViewModels/VisualizerDockable).
/// </summary>
public sealed class ExcelService
{
    /// <summary>Where exports go: %TEMP%/AXamlIDE/Exports.</summary>
    public static string ExportFolder { get; } = Path.Combine(Path.GetTempPath(), "AXamlIDE", "Exports");

    /// <summary>Writes the table as a workbook (bold header row, filter, typed cells) and opens it. Returns the path.</summary>
    public string OpenAsExcel(string name, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(name));
        for (var c = 0; c < headers.Count; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
                WriteCell(sheet.Cell(r, c + 1), row[c]);
            r++;
        }

        if (headers.Count > 0)
        {
            sheet.Range(1, 1, Math.Max(1, r - 1), headers.Count).SetAutoFilter();
            sheet.SheetView.FreezeRows(1);
            sheet.Columns(1, headers.Count).AdjustToContents(1, Math.Min(r - 1, 200));   // the first rows are enough to size by
        }

        var path = NewFile(name, ".xlsx");
        workbook.SaveAs(path);
        Open(path);
        return path;
    }

    /// <summary>Writes the table as CSV (UTF-8 with a BOM, so Excel reads accents) and opens it. Returns the path.</summary>
    public string OpenAsCsv(string name, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", headers.Select(Quote)));
        foreach (var row in rows)
            csv.AppendLine(string.Join(",", row.Select(cell => Quote(CsvText(cell)))));

        var path = NewFile(name, ".csv");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Open(path);
        return path;
    }

    private static void WriteCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case DateTime date:
                cell.Value = date;
                cell.Style.DateFormat.Format = date.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                break;
            case DateOnly date:
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case bool flag:
                cell.Value = flag;
                break;
            case decimal or double or float or int or long or short or byte or uint or ulong or ushort or sbyte:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static string CsvText(object? cell) => cell switch
    {
        null => "",
        DateTime date => date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => cell.ToString() ?? ""
    };

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>A sheet name Excel accepts: at most 31 characters, none of : \ / ? * [ ].</summary>
    private static string SheetName(string name)
    {
        var clean = string.Concat(name.Select(ch => ":\\/?*[]".Contains(ch) ? '_' : ch)).Trim('\'');
        return clean.Length == 0 ? "Sheet1" : clean[..Math.Min(31, clean.Length)];
    }

    private static string NewFile(string name, string extension)
    {
        Directory.CreateDirectory(ExportFolder);
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(ch => invalid.Contains(ch) || ch is ' ' or '[' or ']' or '(' or ')' ? '_' : ch));
        if (clean.Length > 60)
            clean = clean[..60];
        return Path.Combine(ExportFolder, $"{clean}-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}");
    }

    /// <summary>Opens the file in the app the system associates with it (xdg-open on Linux, open on macOS).</summary>
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
