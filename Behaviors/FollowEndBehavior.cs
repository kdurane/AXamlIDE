using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace AXamlIDE.Behaviors;

/// <summary>
/// Keeps a ScrollViewer at the bottom as lines are added, like Visual Studio's Output window:
/// scrolling up stops following, scrolling back to the bottom starts it again.
/// </summary>
public sealed class FollowEndBehavior : Behavior<ScrollViewer>
{
    private bool _following = true;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is { } scrollViewer)
            scrollViewer.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } scrollViewer)
            scrollViewer.ScrollChanged -= OnScrollChanged;
        base.OnDetaching();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (AssociatedObject is not { } scrollViewer)
            return;

        if (e.ExtentDelta.Y != 0)
        {
            // Content grew or was cleared. The offset hasn't caught up yet, so "at the bottom?" would
            // wrongly say no here; only act on the last answer.
            if (scrollViewer.Extent.Height <= scrollViewer.Viewport.Height)
                _following = true;   // cleared for a new build, or still fits: follow from the start
            else if (_following)
                ScrollToBottom(scrollViewer);
            return;
        }

        // The user scrolled, or the panel was resized.
        _following = scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - 1;
    }

    /// <summary>Like ScrollToEnd, but keeps the horizontal position.</summary>
    private static void ScrollToBottom(ScrollViewer scrollViewer)
    {
        var bottom = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, bottom);
    }
}
