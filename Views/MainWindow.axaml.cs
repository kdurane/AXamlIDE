using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;
using AXamlIDE.Services.Documents;
using AXamlIDE.Shell;
using AXamlIDE.ViewModels;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace AXamlIDE.Views;

/// <summary>
/// The main window. It only places the panels (see MainWindow.axaml) and does what needs a window:
/// keyboard shortcuts (here and in every floating tab window), and the
/// floating debug toolbar. Its colours come from the theme (Styles/ThemeResources.axaml).
/// </summary>
public partial class MainWindow : Window
{
    private readonly KeyMap _keyMap = null!;
    private SearchEverywhereWindow? _searchEverywhere;   // made on first use, then hidden and shown again
    private bool _exitConfirmed;    // the exit questions were answered: the next close goes through
    private bool _confirmingExit;   // they're being asked now

    /// <summary>
    /// For the XAML designer/previewer only: shows the layout with no services behind it.
    /// The app always uses the DI constructor below.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Created by the DI container (see AppServices).</summary>
    public MainWindow(MainWindowViewModel viewModel, KeyMap keyMap, FileChangeWatcher fileWatcher)
    {
        InitializeComponent();
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

        // Floating windows open relative to this one.
        PositionChanged += (_, _) => UpdateBoundsForDocking(docking);
        SizeChanged += (_, _) => UpdateBoundsForDocking(docking);

        Closing += async (_, e) =>
        {
            // The questions (stop debugging? save?) need awaiting, which a Closing handler can't do: cancel this
            // close, ask, and close again once everything is answered.
            if (!_exitConfirmed)
            {
                e.Cancel = true;
                if (_confirmingExit)
                    return;   // the X clicked again while the questions are open
                _confirmingExit = true;
                try
                {
                    _exitConfirmed = await viewModel.ConfirmExitAsync();
                }
                finally
                {
                    _confirmingExit = false;
                }
                // Posted: with nothing to ask, this runs before the cancelled close has finished.
                if (_exitConfirmed)
                    Dispatcher.UIThread.Post(Close);
                return;
            }

            viewModel.Session.Save();   // before the floating windows close (the Search window's place)
            docking.CloseFloatingWindows();
        };
        keyMap.AttachTo(this);
        viewModel.SearchEverywhere.Actions = keyMap.Actions;   // its Actions tab: the commands, with their keys
        viewModel.SearchEverywhere.ShowRequested += () =>
        {
            _searchEverywhere ??= new SearchEverywhereWindow(this, viewModel.SearchEverywhere);
            _searchEverywhere.ShowOver();
        };
        Activated += (_, _) => fileWatcher.CheckAll();
    }

    private void UpdateBoundsForDocking(DockLayoutViewModel docking)
        => docking.MainWindowBounds = (Position.X, Position.Y, Bounds.Width, Bounds.Height);

    private HostWindow CreateFloatingWindow()
    {
        // Dock's style lets a lone tool's header stand in for the window's title bar. That hides the system
        // close button, and dragging the header becomes a plain window move, which can't drop into a dock.
        // So it keeps a title bar: ours, as every window has (customChrome, Styles/WindowStyles.axaml), with
        // its close button (its tabs go back, see DockLayoutViewModel). The tool's header stays a Dock drag
        // handle, so the tool can be dragged back into a dock.
        var window = new HostWindow { ToolChromeControlsWholeWindow = false };
        window.Classes.Add("customChrome");
        window.Bind(BackgroundProperty, window.GetResourceObservable("AppWindowBackgroundBrush"));
        _keyMap.AttachTo(window);
        return window;
    }
}
