namespace AXamlIDE.Services.Lsp;

/// <summary>
/// Whether Analyze > Run Code Analysis on Solution is collecting its pass right now. Shared by the language server's
/// option answers (RoslynSettings, read on the server's reader thread) and SolutionAnalysisService (which sets it on
/// the UI thread); it's a separate object so the server's settings don't need the service that needs the server.
/// </summary>
public sealed class CodeAnalysisRun
{
    private volatile bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set => _isRunning = value;
    }
}
