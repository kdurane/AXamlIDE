using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;

namespace AXamlIDE.Behaviors;

/// <summary>
/// Keeps a ScrollViewer at the bottom as lines are added, like Visual Studio's Output window:
/// scrolling up stops following, scrolling back to the bottom starts it again.
/// </summary>
/// <remarks>
/// Only the user's own scrolling (wheel, keys, dragging the bar or the content) can stop it following. In a
/// virtualized list the offset also moves by itself, as rows that come into view are measured and the guessed
/// heights of the others are corrected; read as "the user scrolled up", that left the view stuck part way up
/// (the Claude panel, as a long reply came in).
/// </remarks>
public sealed class FollowEndBehavior : Behavior<ScrollViewer>
{
    private bool _following = true;

    /// <summary>A wheel turn or key press is being handled; cleared once its scroll has gone through layout.</summary>
    private bool _userScrolled;

    /// <summary>A button is held down in the viewer: dragging the scroll bar's thumb, or selecting text.</summary>
    private bool _pointerDown;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is not { } scrollViewer)
            return;
        scrollViewer.ScrollChanged += OnScrollChanged;
        // Tunnel and handled too: the scroll bar and the content handle these before they would bubble up here.
        const RoutingStrategies routes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnUserScroll, routes, handledEventsToo: true);
        scrollViewer.AddHandler(InputElement.KeyDownEvent, OnUserScroll, routes, handledEventsToo: true);
        scrollViewer.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, routes, handledEventsToo: true);
        scrollViewer.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, routes, handledEventsToo: true);
        scrollViewer.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerReleased, routes, handledEventsToo: true);
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } scrollViewer)
        {
            scrollViewer.ScrollChanged -= OnScrollChanged;
            scrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnUserScroll);
            scrollViewer.RemoveHandler(InputElement.KeyDownEvent, OnUserScroll);
            scrollViewer.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            scrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            scrollViewer.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerReleased);
        }
        base.OnDetaching();
    }

    private void OnUserScroll(object? sender, RoutedEventArgs e)
    {
        if (_userScrolled)
            return;
        _userScrolled = true;
        // ScrollChanged comes after the layout pass the scroll causes; Background runs after that.
        Dispatcher.UIThread.Post(() => _userScrolled = false, DispatcherPriority.Background);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => _pointerDown = true;

    private void OnPointerReleased(object? sender, RoutedEventArgs e) => _pointerDown = false;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (AssociatedObject is not { } scrollViewer)
            return;

        if (scrollViewer.Extent.Height <= scrollViewer.Viewport.Height)
        {
            _following = true;   // cleared for a new build, or still fits: follow from the start
            return;
        }

        if (_userScrolled || _pointerDown)
        {
            _following = IsAtBottom(scrollViewer);   // up to read back, or back down to follow again
            return;
        }

        // Content grew, rows were measured, or the panel was resized: put it back at the bottom if it was there.
        if (_following && !IsAtBottom(scrollViewer))
            ScrollToBottom(scrollViewer);
    }

    private static bool IsAtBottom(ScrollViewer scrollViewer)
        => scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - 1;

    /// <summary>Like ScrollToEnd, but keeps the horizontal position.</summary>
    private static void ScrollToBottom(ScrollViewer scrollViewer)
    {
        var bottom = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, bottom);
    }
}
