using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.TextMate;
using AXamlIDE.Messages;
using AXamlIDE.Shell;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using static AXamlIDE.Editor.Highlighting.TextMateController;

namespace AXamlIDE.Views;

/// <summary>
/// The main window. It only places the panels (see MainWindow.axaml) and does what needs the
/// window itself: keyboard shortcuts, the explorer layout (side and visibility), theme colours
/// for the window chrome, and the floating debug toolbar.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = null!;
    private readonly SettingsViewModel _settings = null!;
    private readonly ToolPanels _toolPanels = null!;
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
        ToolPanels toolPanels,
        KeyMap keyMap,
        IMessenger messenger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _toolPanels = toolPanels;

        // Before DataContext: the document well initialises the editor, which applies the theme.
        messenger.Register<MainWindow, EditorThemeAppliedMessage>(this,
            static (w, m) => w.ApplyThemeColorsToWindow(m.Installation));

        DataContext = viewModel;

        keyMap.AttachTo(this);
        _debugToolbar = new DebugToolbarController(this, viewModel.Run, keyMap.AttachTo);

        // Tool panels: the view model only knows the id of the panel in front.
        ToolTabs.ItemsSource = toolPanels.All;
        ToolTabs.SelectedItem = toolPanels.Find(viewModel.SelectedToolPanelId);
        ToolTabs.SelectionChanged += (_, _) =>
        {
            if (ToolTabs.SelectedItem is ToolPanel panel)
                viewModel.SelectedToolPanelId = panel.Id;
        };

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        settings.PropertyChanged += OnSettingsPropertyChanged;
        ApplyExplorerLayout();

        Closed += (_, _) =>
        {
            // View models and services are disposed by the DI container when the app exits.
            DocumentWell.Shutdown();
            messenger.UnregisterAll(this);
        };
    }

    // ============================================================ explorer layout

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsExplorerVisible):
                ApplyExplorerLayout();
                break;
            case nameof(MainWindowViewModel.SelectedToolPanelId):
                ToolTabs.SelectedItem = _toolPanels.Find(_viewModel.SelectedToolPanelId) ?? ToolTabs.SelectedItem;
                break;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SolutionExplorerOnRight))
            ApplyExplorerLayout();
    }

    /// <summary>Activity bar + explorer on the left (or mirrored on the right), explorer shown or hidden.</summary>
    private void ApplyExplorerLayout()
    {
        var onRight = _settings.SolutionExplorerOnRight;
        var visible = _viewModel.IsExplorerVisible;

        ContentGrid.ColumnDefinitions.Clear();
        if (onRight)
        {
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(visible ? 4 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(visible ? 260 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(30, GridUnitType.Pixel));

            Grid.SetColumn(EditorHost, 0);
            Grid.SetColumn(Splitter, 1);
            Grid.SetColumn(ExplorerPane, 2);
            Grid.SetColumn(ActivityBar, 3);
        }
        else
        {
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(30, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(visible ? 260 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(visible ? 4 : 0, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            Grid.SetColumn(ExplorerPane, 1);
            Grid.SetColumn(Splitter, 2);
            Grid.SetColumn(EditorHost, 3);
            Grid.SetColumn(ActivityBar, 0);
        }

        ExplorerPane.IsVisible = visible;
        Splitter.IsVisible = visible;
    }

    // ============================================================ theme

    private void ApplyThemeColorsToWindow(TextMate.Installation e)
    {
        if (!ApplyBrushAction(e, "statusBar.background", brush => StatusBar.Background = brush))
            StatusBar.Background = Brushes.Purple;

        if (!ApplyBrushAction(e, "statusBar.foreground", brush => LspStatus.Foreground = brush))
            LspStatus.Foreground = Brushes.White;

        // The window takes the editor's background so the chrome matches the theme.
        ApplyBrushAction(e, "editor.background", brush => Background = brush);
        ApplyBrushAction(e, "editor.foreground", brush => Foreground = brush);
    }
}
