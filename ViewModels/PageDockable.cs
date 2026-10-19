using System;
using Dock.Model.Mvvm.Controls;

namespace AXamlIDE.ViewModels;

/// <summary>
/// A document tab that isn't a file: a diff (DiffDockable) or the NuGet manager (NuGetDockable). There's one tab
/// per <see cref="Dock.Model.Core.IDockable.Id"/>: opening the same page again brings the open tab back
/// (DockLayoutViewModel.OpenPage). Disposed when its tab is closed; its view comes from Views/DockViewCache.
/// </summary>
public abstract class PageDockable : Document, IDisposable
{
    public abstract void Dispose();
}
