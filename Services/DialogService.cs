using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using AXaxmlIDE.Enums;
using AXaxmlIDE.Lsp;
using AXaxmlIDE.Models;

namespace AXaxmlIDE.Services;

/// <summary>
/// The "build a Window, ShowDialog(this)" helpers that used to live in MainWindow.
/// Bodies are copy-pasted; the only change is <c>this</c> -> <c>_owner</c>.
/// Later: extract an IDialogService interface from the public members so the
/// view models can use it instead of being handed the Window.
/// </summary>
public sealed class DialogService
{
          private readonly Window _owner;

          public DialogService(Window owner) => _owner = owner;

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
                new TextBlock { Text = $"Save changes to {doc.Title}?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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
                    await dialog.ShowDialog(_owner);
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
                    await window.ShowDialog(_owner);
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
                    LspLocation? selected = null;
                    var list = new ListBox
                    {
                              MinWidth = 560,
                              MaxHeight = 420,
                              ItemsSource = locations.Select(display).ToArray()
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
                    new TextBlock { Text = "Select a result to navigate." },
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
                    await window.ShowDialog(_owner);
                    return selected;

                    void Accept()
                    {
                              if (list.SelectedIndex < 0 || list.SelectedIndex >= locations.Count)
                                        return;
                              selected = locations[list.SelectedIndex];
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
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
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
                    await dialog.ShowDialog(_owner);
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

          // ----------------------------------------------------------- text prompt

          public async Task<string?> PromptForTextAsync(string title, string placeholder)
          {
                    string? value = null;
                    var textBox = new TextBox { PlaceholderText = placeholder, MinWidth = 360 };
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
                    var search = new Button { Content = "Search", MinWidth = 80 };
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
                    dialog.Opened += (_, _) => textBox.Focus();
                    await dialog.ShowDialog(_owner);
                    return value;

                    void Accept()
                    {
                              value = textBox.Text;
                              dialog.Close();
                    }
          }
}
