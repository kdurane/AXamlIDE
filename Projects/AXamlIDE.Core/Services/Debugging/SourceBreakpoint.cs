namespace AXamlIDE.Services.Debugging;

/// <summary>
/// A breakpoint as the debug adapter is told it (DAP's SourceBreakpoint): its 1-based line, and optionally Visual
/// Studio's Conditions: an expression that must be true (<see cref="Condition"/>), and a hit count
/// (<see cref="HitCondition"/>: "5" stops on the 5th hit; netcoredbg also takes "&gt;= 5" style comparisons).
/// </summary>
public sealed record SourceBreakpoint(int Line, string? Condition = null, string? HitCondition = null);

/// <summary>The exception filters netcoredbg understands (DAP's setExceptionBreakpoints).</summary>
public static class ExceptionFilters
{
    /// <summary>Break when any exception is thrown (VS: every box ticked in Exception Settings).</summary>
    public const string All = "all";

    /// <summary>Break when an exception isn't handled by user code (Visual Studio's default).</summary>
    public const string UserUnhandled = "user-unhandled";
}
