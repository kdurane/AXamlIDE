using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using AXamlIDE.Shell;
using AXamlIDE.ViewModels;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace AXamlIDE.Views;

/// <summary>
/// The main window. It only places the panels (see MainWindow.axaml) and does what needs a window:
/// keyboard shortcuts (here and in every floating tab window), the activity bar's side, and the
/// floating debug toolbar. Its colours come from the theme (Styles/ThemeResources.axaml).
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsViewModel _settings = null!;
    private readonly KeyMap _keyMap = null!;
    private readonly DebugToolbarController? _debugToolbar;

    /// <summary>
    /// For the XAML designer/previewer only: shows the layout with no services behind it.
    /// The app always uses the DI constructor below.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Created by the DI container (see AppServices).</summary>
    public MainWindow(
        MainWindowViewModel viewModel,
        SettingsViewModel settings,
        KeyMap keyMap)
    {
        InitializeComponent();
        _settings = settings;
        _keyMap = keyMap;

        // Tabs dragged out of the window get a window of ours, so the shortcuts work there too.
        var docking = viewModel.Docking;
        docking.Factory.DefaultHostWindowLocator = CreateFloatingWindow;
        docking.Factory.HostWindowLocator = new Dictionary<string, System.Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = CreateFloatingWindow
        };

        DataContext = viewModel;
        viewModel.ExitRequested += Close;

        keyMap.AttachTo(this);
        _debugToolbar = new DebugToolbarController(this, viewModel.Run, keyMap.AttachTo);

        settings.PropertyChanged += OnSettingsPropertyChanged;
        ApplyActivityBarSide();

        // Floating windows open relative to this one.
        PositionChanged += (_, _) => UpdateBoundsForDocking(docking);
        SizeChanged += (_, _) => UpdateBoundsForDocking(docking);

        Closing += (_, _) =>
        {
            viewModel.Session.Save();   // before the floating windows close (the Search window's place)
            docking.CloseFloatingWindows();
        };
    }

    private void UpdateBoundsForDocking(DockLayoutViewModel docking)
        => docking.MainWindowBounds = (Position.X, Position.Y, Bounds.Width, Bounds.Height);

    private HostWindow CreateFloatingWindow()
    {
        // Dock's style lets a lone tool's header stand in for the window's title bar. That hides the system
        // close button, and dragging the header becomes a plain window move, which can't drop into a dock.
        // Keeping the system title bar gives a close button (its tabs go back, see DockLayoutViewModel), and
        // the tool's header stays a Dock drag handle, so the tool can be dragged back into a dock.
        var window = new HostWindow { ToolChromeControlsWholeWindow = false };
        window.Bind(BackgroundProperty, window.GetResourceObservable("AppWindowBackgroundBrush"));
        _keyMap.AttachTo(window);
        return window;
    }

    // ============================================================ activity bar

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SolutionExplorerOnRight))
            ApplyActivityBarSide();
    }

    /// <summary>The activity bar sits on the explorer's side (the explorer itself is moved by the dock layout).</summary>
    private void ApplyActivityBarSide()
    {
        var onRight = _settings.SolutionExplorerOnRight;

        ContentGrid.ColumnDefinitions.Clear();
        ContentGrid.ColumnDefinitions.Add(onRight ? new ColumnDefinition(GridLength.Star) : new ColumnDefinition(30, GridUnitType.Pixel));
        ContentGrid.ColumnDefinitions.Add(onRight ? new ColumnDefinition(30, GridUnitType.Pixel) : new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(ActivityBar, onRight ? 1 : 0);
        Grid.SetColumn(DockArea, onRight ? 0 : 1);
    }
}
