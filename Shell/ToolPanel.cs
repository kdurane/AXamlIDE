using System;
using Avalonia.Controls;

namespace AXamlIDE.Shell;

/// <summary>
/// One tool panel (Problems, Output, Search...): an id, a title, and its view. The view is created on
/// first use and then kept, so it can be moved between tab strips or windows without losing state.
/// The view gets its own view model as DataContext and must not depend on the window it is in.
/// </summary>
public sealed class ToolPanel(string id, string title, Func<Control> createView)
{
    private Control? _view;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public Control View => _view ??= createView();
}
