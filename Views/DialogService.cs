using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Publishing;
using AXamlIDE.Services.Workspace;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The "build a Window, ShowDialog(Owner)" helpers. Implements <see cref="IDialogService"/> so view
/// models and editor controllers depend on the interface, not on the Window.
/// Dialogs are owned by the app's main window, looked up when each dialog opens.
/// </summary>
/// <param name="settings">The live settings the Settings window edits.</param>
public sealed class DialogService(SettingsViewModel settings) : IDialogService
{
    private static Window Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("Dialogs need the main window, which isn't open yet.");

    // ------------------------------------------------------------ save prompt

    public async Task<SaveChoice> AskSaveAsync(OpenDocument doc)
    {
        var result = SaveChoice.Cancel; // closing the window with the X = cancel
        var dialog = new Window
        {
            Title = "Unsaved changes",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"Save changes to {doc.Title}?", TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        Make("Save", SaveChoice.Save),
                        Make("Don't save", SaveChoice.Discard),
                        Make("Cancel", SaveChoice.Cancel)
                    }
                }
            }
        };
        await dialog.ShowDialog(Owner);
        return result;

        Button Make(string text, SaveChoice choice)
        {
            var b = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) =>
            {
                result = choice;
                dialog.Close();
            };
            return b;
        }
    }

    // ------------------------------------------------------------- quick open

    public async Task<string?> ShowQuickOpenDialogAsync(IReadOnlyList<string> files)
    {
        string? selectedPath = null;
        var results = new ListBox { MaxHeight = 420, MinWidth = 560 };
        var query = new TextBox { PlaceholderText = "Type a file name..." };
        var window = new Window
        {
            Title = "Quick Open",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        query.TextChanged += (_, _) => UpdateResults();
        query.KeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case Key.Enter:
                    args.Handled = true;
                    AcceptSelection();
                    break;
                case Key.Down when results.ItemCount > 0:
                    results.Focus();
                    results.SelectedIndex = Math.Min(results.SelectedIndex + 1, results.ItemCount - 1);
                    args.Handled = true;
                    break;
            }
        };
        results.DoubleTapped += (_, _) => AcceptSelection();
        results.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            AcceptSelection();
        };

        UpdateResults();
        window.Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children = { query, results }
        };
        window.Opened += (_, _) => query.Focus();
        await window.ShowDialog(Owner);
        return selectedPath;

        void UpdateResults()
        {
            var text = query.Text?.Trim() ?? "";
            var filtered = files
                .Where(path => text.Length == 0
                               || Path.GetFileName(path).Contains(text, StringComparison.OrdinalIgnoreCase)
                               || path.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path).Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToArray();
            results.ItemsSource = filtered;
            results.SelectedIndex = filtered.Length > 0 ? 0 : -1;
        }

        void AcceptSelection()
        {
            if (results.SelectedItem is not string path) return;
            selectedPath = path;
            window.Close();
        }
    }

    // ---------------------------------------------------------- location list

    public async Task<LspLocation?> PickLocationAsync(
        string title,
        IReadOnlyList<LspLocation> locations,
        Func<LspLocation, string> display)
    {
        var index = await PickIndexAsync(title, "Select a result to navigate.", [.. locations.Select(display)]);
        return index is { } i ? locations[i] : null;
    }

    public async Task<string?> PickAsync(string title, string prompt, IReadOnlyList<string> items)
    {
        var index = await PickIndexAsync(title, prompt, items);
        return index is { } i ? items[i] : null;
    }

    /// <summary>A list to pick from (double-click or Enter); the chosen index, or null if closed.</summary>
    private static async Task<int?> PickIndexAsync(string title, string prompt, IReadOnlyList<string> items)
    {
        int? selected = null;
        var list = new ListBox
        {
            MinWidth = 560,
            MaxHeight = 420,
            ItemsSource = items
        };
        var window = new Window
        {
            Title = title,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = prompt },
                    list
                }
            }
        };

        list.SelectedIndex = 0;
        list.DoubleTapped += (_, _) => Accept();
        list.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            Accept();
        };
        await window.ShowDialog(Owner);
        return selected;

        void Accept()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= items.Count)
                return;
            selected = list.SelectedIndex;
            window.Close();
        }
    }

    // ---------------------------------------------------------------- rename

    public async Task<bool> ConfirmRenameAsync(
        IReadOnlyDictionary<string, List<(LspRange Range, string NewText)>> changes)
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = "Confirm Rename",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"Apply language-server rename edits in {changes.Count} file(s)? Changed files will be opened and left unsaved for review.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        Make("Apply Rename", true),
                        Make("Cancel", false)
                    }
                }
            }
        };
        await dialog.ShowDialog(Owner);
        return confirmed;

        Button Make(string text, bool accept)
        {
            var button = new Button { Content = text, MinWidth = 90 };
            button.Click += (_, _) =>
            {
                confirmed = accept;
                dialog.Close();
            };
            return button;
        }
    }

    // ------------------------------------------------------- workspace replace

    public async Task<bool> ConfirmWorkspaceReplaceAsync(WorkspaceReplacePlan plan)
    {
        var confirmed = false;
        Window? dialog = null;
        dialog = new Window
        {
            Title = "Confirm Replace All",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Replace {plan.MatchCount} occurrence(s) in {plan.Files.Count} file(s)?",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Closed files will be saved immediately. Open documents will be changed in memory and remain unsaved.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            MakeButton("Replace All", accept: true),
                            MakeButton("Cancel", accept: false)
                        }
                    }
                }
            }
        };

        await dialog.ShowDialog(Owner);
        return confirmed;

        Button MakeButton(string text, bool accept)
        {
            var button = new Button { Content = text, MinWidth = 90 };
            button.Click += (_, _) =>
            {
                confirmed = accept;
                dialog?.Close();
            };
            return button;
        }
    }

    // ---------------------------------------------------------------- confirm

    public async Task<MoveChoice> ConfirmMoveAsync(string message, IReadOnlyList<string> namespaceChanges)
    {
        var choice = MoveChoice.Cancel;
        var updateNamespaces = new CheckBox
        {
            Content = "Update namespaces and references",
            IsChecked = true
        };
        var changes = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6),
            Child = new ScrollViewer
            {
                MaxHeight = 240,
                Content = new SelectableTextBlock
                {
                    Text = string.Join(Environment.NewLine, namespaceChanges),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        changes.Bind(Border.BorderBrushProperty, changes.GetResourceObservable("DockBorderSubtleBrush"));
        updateNamespaces.IsCheckedChanged += (_, _) => changes.IsVisible = updateNamespaces.IsChecked == true;

        Window? dialog = null;
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (namespaceChanges.Count > 0)
        {
            content.Children.Add(updateNamespaces);
            content.Children.Add(changes);
            content.Children.Add(new TextBlock
            {
                Text = "Changed files are saved, except open ones with unsaved edits of yours: those are changed and left for you to save.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12
            });
        }
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { MakeButton("Move", accept: true), MakeButton("Cancel", accept: false) }
        });

        dialog = new Window
        {
            Title = "Move",
            Width = namespaceChanges.Count > 0 ? 620 : 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        await dialog.ShowDialog(Owner);
        return choice;

        Button MakeButton(string text, bool accept)
        {
            var button = new Button { Content = text, MinWidth = 90 };
            button.Click += (_, _) =>
            {
                choice = !accept ? MoveChoice.Cancel
                    : namespaceChanges.Count > 0 && updateNamespaces.IsChecked == true ? MoveChoice.MoveAndUpdateNamespaces
                    : MoveChoice.Move;
                dialog?.Close();
            };
            return button;
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var confirmed = false;
        Window? dialog = null;
        dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { MakeButton(confirmText, accept: true), MakeButton("Cancel", accept: false) }
                    }
                }
            }
        };

        await dialog.ShowDialog(Owner);
        return confirmed;

        Button MakeButton(string text, bool accept)
        {
            var button = new Button { Content = text, MinWidth = 90 };
            button.Click += (_, _) =>
            {
                confirmed = accept;
                dialog?.Close();
            };
            return button;
        }
    }

    // ----------------------------------------------------------- text prompt

    public async Task<string?> PromptForTextAsync(string title, string placeholder, string acceptText = "OK", string initialText = "")
    {
        string? value = null;
        var textBox = new TextBox { PlaceholderText = placeholder, MinWidth = 360, Text = initialText };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        textBox.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            Accept();
        };
        var search = new Button { Content = acceptText, MinWidth = 80 };
        search.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                textBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { search, cancel }
                }
            }
        };
        dialog.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };
        await dialog.ShowDialog(Owner);
        return value;

        void Accept()
        {
            value = textBox.Text;
            dialog.Close();
        }
    }

    // --------------------------------------------------------------- pickers

    public async Task<string?> PickFolderAsync()
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Project Folder",
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSolutionAsync()
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Solution",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Solution files") { Patterns = ["*.sln", "*.slnx"] }]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    // --------------------------------------------------------------- windows

    public Task ShowSettingsAsync()
        => new SettingsWindow(settings).ShowDialog(Owner);

    public async Task<IReadOnlyList<NewItemFile>?> AddNewItemAsync(string folder, string? workspaceRoot, string? templateName = null)
    {
        var viewModel = new AddNewItemViewModel(folder, workspaceRoot, templateName);
        var added = await new AddNewItemWindow { DataContext = viewModel }.ShowDialog<bool>(Owner);
        return added ? viewModel.Files : null;
    }

    public async Task ShowStartWindowAsync(StartViewModel start)
    {
        var window = new StartWindow { DataContext = start, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        void Close() => window.Close();
        start.Finished += Close;
        try
        {
            await window.ShowDialog(Owner);
        }
        finally
        {
            start.Finished -= Close;
        }
    }

    public async Task<string?> CloneRepositoryAsync(string defaultLocation)
    {
        var viewModel = new CloneRepositoryViewModel(defaultLocation, this);
        return await new CloneRepositoryWindow { DataContext = viewModel }.ShowDialog<string?>(Owner);
    }

    public async Task<string?> NewProjectAsync(string defaultLocation)
    {
        var viewModel = new NewProjectViewModel(defaultLocation, this);
        return await new NewProjectWindow { DataContext = viewModel }.ShowDialog<string?>(Owner);
    }

    public async Task<PublishWizardResult?> ShowPublishWizardAsync(string projectPath, string? targetFramework)
    {
        var viewModel = new PublishWizardViewModel(projectPath, targetFramework);
        var finished = await new PublishWizardWindow { DataContext = viewModel }.ShowDialog<bool>(Owner);
        return finished ? viewModel.Result : null;
    }

    public async Task<PublishProfile?> EditPublishProfileAsync(string projectPath, PublishProfile profile, IReadOnlyList<string> targetFrameworks)
    {
        var viewModel = new PublishSettingsViewModel(projectPath, profile, targetFrameworks);
        var saved = await new PublishSettingsWindow { DataContext = viewModel }.ShowDialog<bool>(Owner);
        return saved ? viewModel.Accept() : null;
    }
}
