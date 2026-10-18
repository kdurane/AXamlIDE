using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AXamlIDE.Documents;
using AXamlIDE.Lsp;
using AXamlIDE.Search;
using AXamlIDE.Settings;

namespace AXamlIDE.Shared;

/// <summary>
/// Everything that opens a window or a picker on the user's behalf. View models and editor
/// controllers depend on this instead of being handed the Window, so they have no UI types in
/// them and can be tested with a fake.
/// </summary>
public interface IDialogService
{
    // Prompts
    Task<SaveChoice> AskSaveAsync(OpenDocument doc);
    Task<string?> PromptForTextAsync(string title, string placeholder);
    Task<bool> ConfirmRenameAsync(IReadOnlyDictionary<string, List<(LspRange Range, string NewText)>> changes);
    Task<bool> ConfirmWorkspaceReplaceAsync(WorkspaceReplacePlan plan);

    // Pickers
    Task<string?> ShowQuickOpenDialogAsync(IReadOnlyList<string> files);
    Task<LspLocation?> PickLocationAsync(string title, IReadOnlyList<LspLocation> locations, Func<LspLocation, string> display);
    Task<string?> PickFolderAsync();
    Task<string?> PickSolutionAsync();

    // Windows
    Task ShowSettingsAsync(SettingsViewModel settings);
}
