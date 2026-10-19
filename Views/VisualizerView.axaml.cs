using System;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The visualizer tab's view. The table's columns depend on the data (an object's members), so they're made here
/// when <see cref="VisualizerDockable.Columns"/> changes: one text column per header, bound to that cell of the row.
/// The text editor's text follows <see cref="VisualizerDockable.Text"/>.
/// </summary>
public partial class VisualizerView : UserControl
{
    public VisualizerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not VisualizerDockable page)
            return;
        page.PropertyChanged += OnPagePropertyChanged;
        BuildColumns(page);
        TextView.Text = page.Text;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VisualizerDockable page)
            return;
        if (e.PropertyName == nameof(VisualizerDockable.Columns))
            BuildColumns(page);
        else if (e.PropertyName == nameof(VisualizerDockable.Text))
            TextView.Text = page.Text;
    }

    private void BuildColumns(VisualizerDockable page)
    {
        Table.Columns.Clear();
        for (var i = 0; i < page.Columns.Count; i++)
            Table.Columns.Add(new DataGridTextColumn
            {
                Header = page.Columns[i],
                // One-way: the default (two-way) would write the shown text back into the row, turning every cell into a string.
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay },
                CanUserSort = true,
                CustomSortComparer = new CellComparer(i)
            });
    }

    /// <summary>
    /// Sorts rows by one cell: numbers as numbers, dates as dates (cells are typed per column), empty cells first;
    /// cells of different types (never in one column, but just in case) by their text.
    /// </summary>
    private sealed class CellComparer(int column) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (x as object?[]) is { } left && column < left.Length ? left[column] : null;
            var b = (y as object?[]) is { } right && column < right.Length ? right[column] : null;
            if (a is null || b is null)
                return a is null ? b is null ? 0 : -1 : 1;
            if (a.GetType() == b.GetType() && a is IComparable comparable)
                return comparable.CompareTo(b);
            return string.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
    }
}
