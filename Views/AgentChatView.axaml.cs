using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The Claude tool panel. DataContext: <see cref="AgentChatViewModel"/>.</summary>
public partial class AgentChatView : UserControl
{
    private AgentChatViewModel? _viewModel;

    public AgentChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel: before the text box turns Enter into a new line.
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.FocusRequested -= FocusPrompt;

        _viewModel = DataContext as AgentChatViewModel;
        if (_viewModel is null)
            return;
        _viewModel.FocusRequested += FocusPrompt;
        _viewModel.EnsureStarted();   // the panel is being shown for the first time
        if (_viewModel.TakePendingFocusRequest())
            FocusPrompt();
    }

    /// <summary>VS Code's chat keys: Enter sends, Shift+Enter is a new line, Escape stops the running turn.</summary>
    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (_viewModel.SendCommand.CanExecute(null))
                    _viewModel.SendCommand.Execute(null);
                break;
            case Key.Escape when _viewModel.IsTurnRunning:
                _viewModel.StopCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void FocusPrompt() => Dispatcher.UIThread.Post(() => PromptBox.Focus(), DispatcherPriority.Loaded);
}
