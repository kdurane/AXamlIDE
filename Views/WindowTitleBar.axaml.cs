using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Settings;

namespace AXamlIDE.Views;

/// <summary>
/// A customChrome window's title bar (see WindowTitleBar.axaml). Moves, maximizes, minimizes and closes its window,
/// and tells the XAML what to show through classes: :maximized, :canMinimize, :canMaximize (from the window's
/// CanMinimize / CanMaximize / CanResize), :hasContent, :noCaptionButtons and :macos.
/// </summary>
public partial class WindowTitleBar : UserControl
{
    public static readonly StyledProperty<object?> BarContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, object?>(nameof(BarContent));

    public static readonly StyledProperty<bool> ShowCaptionButtonsProperty =
        AvaloniaProperty.Register<WindowTitleBar, bool>(nameof(ShowCaptionButtons), true);

    private Window? _window;

    public WindowTitleBar()
    {
        InitializeComponent();
        PseudoClasses.Set(":macos", OperatingSystem.IsMacOS());
        DragArea.PointerPressed += OnDragAreaPressed;
        MinimizeButton.Click += (_, _) => { if (_window != null) _window.WindowState = WindowState.Minimized; };
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => _window?.Close();
    }

    /// <summary>What fills the bar's left side (the main window's menu and toolbar); the window's title if none.</summary>
    public object? BarContent
    {
        get => GetValue(BarContentProperty);
        set => SetValue(BarContentProperty, value);
    }

    /// <summary>False for message boxes: their own buttons close them, so no minimize / maximize / close.</summary>
    public bool ShowCaptionButtons
    {
        get => GetValue(ShowCaptionButtonsProperty);
        set => SetValue(ShowCaptionButtonsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BarContentProperty)
            PseudoClasses.Set(":hasContent", BarContent != null);
        else if (change.Property == ShowCaptionButtonsProperty)
            PseudoClasses.Set(":noCaptionButtons", !ShowCaptionButtons);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.FindAncestorOfType<Window>();
        if (_window == null) return;
        _window.PropertyChanged += OnWindowPropertyChanged;
        UpdateFromWindow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window != null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.CanMinimizeProperty
            || e.Property == Window.CanMaximizeProperty || e.Property == Window.CanResizeProperty)
            UpdateFromWindow();
    }

    private void UpdateFromWindow()
    {
        if (_window == null) return;
        PseudoClasses.Set(":maximized", _window.WindowState == WindowState.Maximized);
        PseudoClasses.Set(":canMinimize", _window.CanMinimize);
        PseudoClasses.Set(":canMaximize", CanMaximize);
    }

    private bool CanMaximize => _window is { CanMaximize: true, CanResize: true };

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_window == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2)
            ToggleMaximized();
        // A floating Dock window moves through Dock, so dropping it on the main window docks it again.
        else if (_window is not HostWindow host || !host.TryBeginMoveDrag(e, WindowDragDockScope.FullWindow))
            _window.BeginMoveDrag(e);
    }

    private void ToggleMaximized()
    {
        if (_window == null || !CanMaximize) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>
    /// Gives a window built in code (DialogService's dialogs) the custom chrome: the class, and a title bar above
    /// its content. Message boxes pass captionButtons false. Call once its Content is set, before showing it.
    /// </summary>
    public static void Apply(Window window, bool captionButtons)
    {
        window.Classes.Add("customChrome");
        window.CanMinimize = false;
        var body = window.Content as Control;
        window.Content = null;
        var bar = new WindowTitleBar { ShowCaptionButtons = captionButtons };
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        var panel = new DockPanel();
        panel.Children.Add(bar);
        if (body != null) panel.Children.Add(body);
        window.Content = panel;
    }
}
