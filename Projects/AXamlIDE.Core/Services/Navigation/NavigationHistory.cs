using System;
using System.Collections.Generic;

namespace AXamlIDE.Services.Navigation;

/// <summary>A place the user was: a file and an offset in its text.</summary>
public sealed record NavigationPoint(string Path, int Offset);

/// <summary>
/// Visual Studio's Navigate Backward / Forward (Ctrl+-, Ctrl+Shift+-): the places the user jumped from and to
/// (Go to Definition, a search result, a problem, switching tabs...). <see cref="Record"/> is called before and
/// after every jump; going back moves along the list without adding to it. Nearby points in the same file (the
/// same line, give or take) count as one, so stepping through a method doesn't fill the list.
/// </summary>
public sealed class NavigationHistory
{
    private const int Capacity = 100;

    private readonly List<NavigationPoint> _points = [];
    private readonly StringComparer _paths;
    private int _index = -1;   // the point the user is at

    public NavigationHistory(StringComparer? pathComparer = null)
        => _paths = pathComparer ?? (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Set while a Back / Forward is being applied: those moves aren't recorded.</summary>
    public bool IsNavigating { get; private set; }

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _points.Count - 1;

    /// <summary>The points, oldest first.</summary>
    public IReadOnlyList<NavigationPoint> Points => _points;

    /// <summary>The list or the current point changed (the Back button's drop-down follows it).</summary>
    public event Action? Changed;

    /// <summary>Where Back would go, nearest first: up to <paramref name="max"/> points before the current one.</summary>
    public IReadOnlyList<NavigationPoint> BackPoints(int max)
    {
        var points = new List<NavigationPoint>();
        for (var i = _index - 1; i >= 0 && points.Count < max; i--)
            points.Add(_points[i]);
        return points;
    }

    /// <summary>
    /// The user is here now. Anything after the current point (undone by Back) is dropped, as in Visual Studio.
    /// The same place twice (or a few characters apart on the same line) isn't recorded again; a move to a place
    /// already at the current point just stays there.
    /// </summary>
    public void Record(NavigationPoint point, int sameLineTolerance = 0)
    {
        if (IsNavigating)
            return;

        if (_index >= 0 && Near(_points[_index], point, sameLineTolerance))
        {
            _points[_index] = point;   // the exact spot, for a later Back
            Changed?.Invoke();
            return;
        }

        if (_index < _points.Count - 1)
            _points.RemoveRange(_index + 1, _points.Count - _index - 1);

        _points.Add(point);
        if (_points.Count > Capacity)
            _points.RemoveAt(0);
        _index = _points.Count - 1;
        Changed?.Invoke();
    }

    /// <summary>The previous point (now the current one), or null when at the start.</summary>
    public NavigationPoint? Back() => Back(1);

    /// <summary>The point <paramref name="steps"/> back (now the current one: Forward comes back through the ones
    /// skipped), or null when there aren't that many. The Back button's drop-down.</summary>
    public NavigationPoint? Back(int steps)
    {
        if (steps < 1 || _index - steps < 0)
            return null;
        _index -= steps;
        Changed?.Invoke();
        return _points[_index];
    }

    /// <summary>The next point (now the current one), or null when at the end.</summary>
    public NavigationPoint? Forward()
    {
        if (!CanGoForward)
            return null;
        _index++;
        Changed?.Invoke();
        return _points[_index];
    }

    /// <summary>Run <paramref name="apply"/> for a Back / Forward move, without it being recorded as a new jump.</summary>
    public void Navigating(Action apply)
    {
        IsNavigating = true;
        try
        {
            apply();
        }
        finally
        {
            IsNavigating = false;
        }
    }

    /// <summary>A file was deleted or renamed: forget the places in it.</summary>
    public void Forget(string path)
    {
        for (var i = _points.Count - 1; i >= 0; i--)
        {
            if (!_paths.Equals(_points[i].Path, path))
                continue;
            _points.RemoveAt(i);
            if (i <= _index)
                _index--;
        }
        if (_index < 0 && _points.Count > 0)
            _index = 0;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _points.Clear();
        _index = -1;
        Changed?.Invoke();
    }

    private bool Near(NavigationPoint a, NavigationPoint b, int tolerance)
        => _paths.Equals(a.Path, b.Path) && Math.Abs(a.Offset - b.Offset) <= tolerance;
}
