using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Publishing;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Services.Dialogs;

/// <summary>
/// Everything that opens a window or a picker on the user's behalf. View models and editor
/// controllers depend on this instead of being handed the Window, so they have no UI types in
/// them and can be tested with a fake.
/// </summary>
public interface IDialogService
{
    // Prompts
    Task<SaveChoice> AskSaveAsync(OpenDocument doc);
    /// <summary>A one-line text box; null if cancelled.</summary>
    Task<string?> PromptForTextAsync(string title, string placeholder, string acceptText = "OK", string initialText = "");

    /// <summary>A yes/no question; true if the user chose <paramref name="confirmText"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<bool> ConfirmRenameAsync(IReadOnlyDictionary<string, List<(LspRange Range, string NewText)>> changes);
    Task<bool> ConfirmWorkspaceReplaceAsync(WorkspaceReplacePlan plan);

    // Pickers
    Task<string?> ShowQuickOpenDialogAsync(IReadOnlyList<string> files);
    Task<LspLocation?> PickLocationAsync(string title, IReadOnlyList<LspLocation> locations, Func<LspLocation, string> display);
    /// <summary>Pick one of <paramref name="items"/>; null if cancelled.</summary>
    Task<string?> PickAsync(string title, string prompt, IReadOnlyList<string> items);
    Task<string?> PickFolderAsync();
    Task<string?> PickSolutionAsync();

    // Windows
    Task ShowSettingsAsync();

    /// <summary>
    /// Visual Studio's Add New Item for <paramref name="folder"/>, starting on <paramref name="templateName"/>
    /// (else Class): the files to write (checked: valid name, nothing there yet), or null if cancelled.
    /// </summary>
    Task<IReadOnlyList<NewItemFile>?> AddNewItemAsync(string folder, string? workspaceRoot, string? templateName = null);

    /// <summary>Visual Studio's Publish wizard for a new profile: its target and location, or a file to import; null if cancelled.</summary>
    Task<PublishWizardResult?> ShowPublishWizardAsync(string projectPath, string? targetFramework);

    /// <summary>Visual Studio's Profile settings ("Show all settings"): the edited profile (not yet saved), or null if cancelled.</summary>
    Task<PublishProfile?> EditPublishProfileAsync(string projectPath, PublishProfile profile, IReadOnlyList<string> targetFrameworks);
}
