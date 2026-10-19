using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Debugging;

/// <summary>
/// A variable as the debug adapter gives it: raw (List&lt;T&gt; shows its _items array, objects show as {Type}).
/// <see cref="DebugValueProvider"/> turns these into what Visual Studio shows.
/// </summary>
/// <param name="VariablesReference">Non-zero when it has children (valid only while paused at this stop).</param>
/// <param name="EvaluateName">An expression that evaluates to this variable (person.Home.City), when the adapter knows one.</param>
public sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference, string? EvaluateName = null);

/// <summary>The result of evaluating an expression: a value, or the adapter's error message.</summary>
public sealed record DebugEvaluation(DebugVariable? Value, string? Error);

/// <summary>What the value views need from a paused debug session (NetCoreDbgSession; fakes in tests).</summary>
public interface IDebugEvaluator
{
    Task<IReadOnlyList<DebugVariable>> GetLocalsAsync(int frameId, CancellationToken ct = default);

    Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default);

    /// <param name="context">"hover" (DataTips), "watch" (Watch window) or "repl".</param>
    Task<DebugEvaluation> EvaluateAsync(int frameId, string expression, string context, CancellationToken ct = default);

    /// <summary>Assign <paramref name="value"/> (C# text) to <paramref name="expression"/>; returns the new value as shown.</summary>
    Task<DebugEvaluation> SetExpressionAsync(int frameId, string expression, string value, CancellationToken ct = default);

    /// <summary>Paths of the assemblies loaded in the debuggee so far (for [DebuggerDisplay] and friends).</summary>
    IReadOnlyCollection<string> LoadedModules { get; }

    /// <summary>The debuggee's process id, once it's running (for reading its memory directly: HeapTables).</summary>
    int? ProcessId { get; }
}
