using System.Collections.Generic;

namespace AXamlIDE.Messages;

// App-wide messages (sent through IMessenger). The shell (MainWindowViewModel) handles both.

/// <summary>Show this text in the status bar.</summary>
public sealed record StatusMessage(string Text);

/// <summary>Bring a tool panel to the front (a <see cref="ToolTab"/> id).</summary>
public sealed record ShowToolTabMessage(string PanelId);
/// <summary>Files were created outside the explorer (e.g. by a code action); the explorer shows them.</summary>
public sealed record FilesCreatedMessage(IReadOnlyList<string> Paths);

/// <summary>A build (or rebuild/clean) is starting; previews pause until it finishes.</summary>
public sealed record BuildStartedMessage;
/// <summary>A build finished; <paramref name="Succeeded"/> is false if it failed or threw.</summary>
public sealed record BuildFinishedMessage(bool Succeeded);

/// <summary>
/// The errors and warnings of the last build (empty after a clean build, a Clean, or closing the workspace).
/// Sent by RunViewModel; the Problems list shows them until the next build.
/// </summary>
public sealed record BuildProblemsMessage(IReadOnlyList<Services.Running.BuildDiagnostic> Diagnostics);

/// <summary>
/// Open this solution (.sln/.slnx) or folder, closing what's open first (asking about unsaved changes): from the start
/// window, the recent list, a clone or a new project. Handled by MainWindowViewModel.
/// </summary>
public sealed record OpenWorkspaceMessage(string Path);
