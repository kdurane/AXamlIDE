using System;
using System.Collections.Generic;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// Our answers to Roslyn's workspace/configuration questions. Roslyn asks for each of its options by a section name
/// such as "csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters" (or "navigation.dotnet_navigate_to_decompiled_sources"
/// for options shared by all languages); an option we don't answer (null) keeps Roslyn's default.
///
/// The fixed answers are what Rider and Visual Studio do out of the box; the rest follow the user's
/// <see cref="EditorFeatureSettings"/> (Settings window). The names are the ones Roslyn's VS Code extension uses for
/// its settings (dotnet.inlayHints.enableInlayHintsForParameters is "csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters").
/// </summary>
public static class RoslynSettings
{
    private const string CSharpPrefix = "csharp|";

    private static readonly Dictionary<string, object> Defaults = new(StringComparer.Ordinal)
    {
        // Which parameter names and types are hinted, when those hints are on (EditorFeatureSettings): before literal
        // arguments (Process(true) reads as Process(force: true)), and the types behind var, lambda parameters and new().
        ["inlay_hints.dotnet_enable_inlay_hints_for_literal_parameters"] = true,
        ["inlay_hints.dotnet_enable_inlay_hints_for_object_creation_parameters"] = true,
        ["inlay_hints.dotnet_enable_inlay_hints_for_indexer_parameters"] = false,
        ["inlay_hints.dotnet_enable_inlay_hints_for_other_parameters"] = false,
        ["inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_differ_only_by_suffix"] = true,
        ["inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_match_method_intent"] = true,
        ["inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_match_argument_name"] = true,
        ["inlay_hints.csharp_enable_inlay_hints_for_implicit_variable_types"] = true,
        ["inlay_hints.csharp_enable_inlay_hints_for_lambda_parameter_types"] = true,
        ["inlay_hints.csharp_enable_inlay_hints_for_implicit_object_creation"] = true,
        ["inlay_hints.csharp_enable_inlay_hints_for_collection_expressions"] = false,
    };

    /// <summary>The answer for one section, or null to leave Roslyn's default (and for other languages: Visual Basic, Razor).</summary>
    /// <param name="codeAnalysisRunning">Analyze > Run Code Analysis on Solution is collecting its one pass: analyse
    /// every file with everything, whatever the settings say.</param>
    public static object? Get(string section, EditorFeatureSettings features, bool codeAnalysisRunning = false)
    {
        if (section.StartsWith(CSharpPrefix, StringComparison.Ordinal))
            section = section[CSharpPrefix.Length..];
        else if (section.Contains('|'))
            return null;   // "visual_basic|..."

        return section switch
        {
            "completion.dotnet_show_completion_items_from_unimported_namespaces" => features.CompletionFromUnimportedNamespaces,
            "inlay_hints.dotnet_enable_inlay_hints_for_parameters" => features.InlayParameterHints,
            "inlay_hints.csharp_enable_inlay_hints_for_types" => features.InlayTypeHints,
            "code_lens.dotnet_enable_references_code_lens" => features.CodeLens,   // no counting references nobody sees
            // Which files Roslyn analyses in the background: its analyzers (IDE0005, CA1822...) and the compiler (CS0103...).
            "background_analysis.dotnet_analyzer_diagnostics_scope" => Scope(codeAnalysisRunning),
            "background_analysis.dotnet_compiler_diagnostics_scope" => Scope(codeAnalysisRunning || features.SolutionWideAnalysis),
            _ => Defaults.GetValueOrDefault(section)
        };
    }

    /// <summary>
    /// "fullSolution" reports every file through workspace/diagnostic; "openFiles" (Roslyn's default) only the open ones.
    /// Solution-wide analysis only widens the compiler's scope, as Rider does (errors everywhere, inspections in the
    /// open files) and as Visual Studio's "Show compiler errors and warnings for: Entire solution" does. Every analyzer
    /// on every file after each keystroke is Visual Studio's Run Code Analysis, far too slow to keep running; that's
    /// the one-off Analyze menu command instead.
    /// </summary>
    private static string Scope(bool fullSolution) => fullSolution ? "fullSolution" : "openFiles";
}
