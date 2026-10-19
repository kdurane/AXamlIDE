using System.Collections.Generic;

// The messages between AXamlIDE and its MSBuild host: one JSON line each way per request.
// This file is compiled into both the host and AXamlIDE.Core (linked in its .csproj), so the two can't drift.
namespace AXamlIDE.MsBuildHost;

internal static class HostCommands
{
    /// <summary>Evaluate a project: its files, references and a few properties. The default.</summary>
    public const string Evaluate = "evaluate";

    /// <summary>One file's build action (item type) and metadata in a project.</summary>
    public const string GetItem = "getItem";

    /// <summary>Change one file's build action and/or one metadata value, write the project, and return the result.</summary>
    public const string SetItem = "setItem";
}

/// <param name="Command">One of <see cref="HostCommands"/>; null means Evaluate.</param>
/// <param name="ItemType">SetItem: the new build action, or null to keep it.</param>
/// <param name="MetadataName">SetItem: the metadata to change, or null; an empty <paramref name="MetadataValue"/> clears it.</param>
/// <param name="Configuration">Evaluate: the build configuration ("Debug", "Release"), so OutputPath matches what
/// <c>dotnet build -c</c> writes; null means the project's default.</param>
internal sealed record HostRequest(
    string ProjectPath,
    string? Command = null,
    string? FilePath = null,
    string? ItemType = null,
    string? MetadataName = null,
    string? MetadataValue = null,
    string? Configuration = null);

internal sealed record HostResponse(
    bool Success,
    string[] Files,
    string[] ProjectReferences,
    string? Error,
    string? AssemblyName,
    string? OutputType,
    string? OutputPath,
    string? ProjectAssetsFile,
    HostItem? Item = null);

/// <param name="ItemType">The file's build action, or null if the project doesn't include it.</param>
/// <param name="AvailableItemTypes">The build actions to offer (the common ones and the project's AvailableItemName).</param>
/// <param name="Metadata">The evaluated values of the metadata the Properties panel shows ("" when not set).</param>
internal sealed record HostItem(
    string? ItemType,
    string[] AvailableItemTypes,
    Dictionary<string, string> Metadata);
