using System.ComponentModel;
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
                Binding = new Binding($"[{i}]"),   // a row is a list of cell texts
                CanUserSort = true
            });
    }
}
