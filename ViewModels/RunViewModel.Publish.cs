using System;
using System.IO;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Publishing;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Publish: run one publish profile (the Publish page's button, PublishDockable), with its output in the Output
/// panel like a build's. Busy with a build or another publish = nothing happens.
/// </summary>
public partial class RunViewModel
{
    /// <summary>Publish <paramref name="projectPath"/> with <paramref name="profile"/>; true if it succeeded.</summary>
    public async Task<bool> PublishAsync(string projectPath, PublishProfile profile)
    {
        if (IsBusy || !await _saver.SaveAllAsync())   // like a build: save everything first
            return false;

        var folder = PublishCommandLine.PublishFolder(projectPath, profile);
        BeginBuildOutput($"Publishing {Path.GetFileNameWithoutExtension(projectPath)} with {profile.Name} ({profile.Configuration}) to {folder}");
        IsBusy = true;
        try
        {
            if (profile.DeleteExistingFiles && Directory.Exists(folder))
            {
                if (PublishCommandLine.WhyNotClear(folder, projectPath) is { } reason)
                {
                    AppendBuildOutput($"Publish cancelled: \"Delete all existing files\" is on, and {reason} Choose another target location.");
                    return false;
                }
                AppendBuildOutput("Deleting the existing files...");
                await Task.Run(() => PublishCommandLine.ClearFolder(folder));
            }

            var exitCode = await _projectRunner.PublishAsync(projectPath, PublishCommandLine.Arguments(projectPath, profile));
            AppendBuildOutput(exitCode == 0
                ? $"Publish succeeded: {folder}"
                : $"Publish failed with exit code {exitCode}.");
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            AppendBuildOutput($"Publish failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
