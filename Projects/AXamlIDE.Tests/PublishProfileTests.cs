using System;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Publishing;

namespace AXamlIDE.Tests;

public sealed class PublishProfileTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly string _project;

    public PublishProfileTests() => _project = _workspace.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

    public void Dispose() => _workspace.Dispose();

    private string WriteProfile(string name, string properties)
        => _workspace.Write($"App/Properties/PublishProfiles/{name}.pubxml",
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project>\n  <PropertyGroup>\n{properties}\n  </PropertyGroup>\n</Project>");

    [Fact]
    public void Reads_a_Visual_Studio_folder_profile()
    {
        WriteProfile("FolderProfile", """
            <Configuration>Release</Configuration>
            <Platform>Any CPU</Platform>
            <PublishDir>bin\Release\net8.0\win-x64\publish\</PublishDir>
            <PublishProtocol>FileSystem</PublishProtocol>
            <_TargetId>Folder</_TargetId>
            <TargetFramework>net8.0</TargetFramework>
            <RuntimeIdentifier>win-x64</RuntimeIdentifier>
            <SelfContained>true</SelfContained>
            <PublishSingleFile>true</PublishSingleFile>
            <PublishReadyToRun>false</PublishReadyToRun>
            <DeleteExistingFiles>true</DeleteExistingFiles>
            """);

        var profile = PublishProfileStore.Load(_project).Single();

        Assert.Equal("FolderProfile", profile.Name);
        Assert.Equal(PublishTargets.FolderId, profile.TargetId);
        Assert.Equal(@"bin\Release\net8.0\win-x64\publish\", profile.PublishDir);
        Assert.Equal("net8.0", profile.TargetFramework);
        Assert.Equal("win-x64", profile.RuntimeIdentifier);
        Assert.True(profile.SelfContained);
        Assert.True(profile.PublishSingleFile);
        Assert.False(profile.PublishReadyToRun);
        Assert.True(profile.DeleteExistingFiles);
        Assert.True(PublishTargets.CanPublish(profile));
    }

    [Fact]
    public void Older_web_profiles_and_other_targets_are_recognised()
    {
        WriteProfile("Old", "<WebPublishMethod>FileSystem</WebPublishMethod><PublishUrl>out</PublishUrl>");
        WriteProfile("Click", "<PublishProtocol>ClickOnce</PublishProtocol><PublishDir>x</PublishDir>");
        WriteProfile("Azure", "<_TargetId>AzureWebApp</_TargetId>");

        var profiles = PublishProfileStore.Load(_project).ToDictionary(profile => profile.Name);

        Assert.Equal(PublishTargets.FolderId, profiles["Old"].TargetId);
        Assert.Equal("out", profiles["Old"].PublishDir);
        Assert.False(PublishTargets.CanPublish(profiles["Click"]));
        Assert.Equal("Azure App Service", PublishTargets.DisplayName(profiles["Azure"].TargetId));
    }

    [Fact]
    public void Saving_a_new_profile_round_trips_and_names_it_like_Visual_Studio()
    {
        var first = PublishProfileStore.NewProfile(_project, PublishTargets.Folder, "bin/Release/net10.0/publish/", "net10.0");
        PublishProfileStore.Save(first);
        var second = PublishProfileStore.NewProfile(_project, PublishTargets.Folder, "out/", null);

        Assert.Equal("FolderProfile", first.Name);
        Assert.Equal("FolderProfile1", second.Name);
        Assert.Equal(first, PublishProfileStore.Read(first.FilePath));
        Assert.Contains("<PublishProtocol>FileSystem</PublishProtocol>", File.ReadAllText(first.FilePath));
    }

    [Fact]
    public void Saving_keeps_unknown_properties_and_drops_settings_that_no_longer_apply()
    {
        var path = WriteProfile("FolderProfile", """
            <PublishDir>out</PublishDir>
            <_TargetId>Folder</_TargetId>
            <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
            <SelfContained>true</SelfContained>
            <PublishTrimmed>true</PublishTrimmed>
            <PublishSingleFile>true</PublishSingleFile>
            <MyOwnSetting>keep me</MyOwnSetting>
            """);

        var portable = PublishProfileStore.Read(path) with { RuntimeIdentifier = null };
        PublishProfileStore.Save(portable);
        var text = File.ReadAllText(path);

        Assert.Contains("<MyOwnSetting>keep me</MyOwnSetting>", text);
        Assert.DoesNotContain("RuntimeIdentifier", text);
        Assert.DoesNotContain("PublishTrimmed", text);
        Assert.DoesNotContain("PublishSingleFile", text);
        Assert.Contains("<SelfContained>false</SelfContained>", text);
    }

    [Fact]
    public void Rename_import_and_delete()
    {
        var profile = PublishProfileStore.NewProfile(_project, PublishTargets.Folder, "out/", null);
        PublishProfileStore.Save(profile);
        File.WriteAllText(profile.FilePath + ".user", "<Project />");

        var renamed = PublishProfileStore.Rename(profile, "Linux");
        Assert.True(File.Exists(renamed.FilePath));
        Assert.True(File.Exists(renamed.FilePath + ".user"));
        Assert.False(File.Exists(profile.FilePath));

        var outside = _workspace.Write("elsewhere/Linux.pubxml", File.ReadAllText(renamed.FilePath));
        var imported = PublishProfileStore.Import(_project, outside);
        Assert.Equal("Linux1", imported.Name);

        PublishProfileStore.Delete(renamed);
        Assert.Equal(["Linux1"], PublishProfileStore.Load(_project).Select(p => p.Name));
    }

    [Fact]
    public void Names_are_checked()
    {
        PublishProfileStore.Save(PublishProfileStore.NewProfile(_project, PublishTargets.Folder, "out/", null));

        Assert.Null(PublishProfileStore.ValidateName(_project, "Release Linux"));
        Assert.NotNull(PublishProfileStore.ValidateName(_project, ""));
        Assert.NotNull(PublishProfileStore.ValidateName(_project, "a;b"));
        Assert.NotNull(PublishProfileStore.ValidateName(_project, "FolderProfile"));
    }

    [Fact]
    public void Visual_Studios_last_selected_profile_is_shown_first()
    {
        WriteProfile("A", "<_TargetId>Folder</_TargetId>");
        WriteProfile("B", "<_TargetId>Folder</_TargetId>");
        File.WriteAllText(_project + ".user",
            @"<Project><PropertyGroup><_LastSelectedProfileId>C:\src\App\Properties\PublishProfiles\A.pubxml</_LastSelectedProfileId></PropertyGroup></Project>");

        Assert.Equal("A", PublishProfileStore.Initial(_project, PublishProfileStore.Load(_project))?.Name);
    }

    [Fact]
    public void Command_line_passes_the_profile_configuration_and_framework()
    {
        var profile = new PublishProfile("Linux", "x.pubxml", PublishTargets.FolderId, "out", "Debug", "net10.0");

        Assert.Equal(
            ["publish", _project, "-p:PublishProfile=Linux", "--configuration", "Debug", "--framework", "net10.0"],
            PublishCommandLine.Arguments(_project, profile));
        Assert.Throws<NotSupportedException>(() => PublishCommandLine.Arguments(_project, profile with { TargetId = "ClickOnce" }));
    }

    [Fact]
    public void Publish_folder_is_resolved_against_the_project()
    {
        var projectFolder = Path.GetDirectoryName(_project)!;
        var profile = new PublishProfile("P", "x.pubxml", PublishTargets.FolderId, @"bin\$(Configuration)\publish\", "Release");

        Assert.Equal(Path.Combine(projectFolder, "bin", "Release", "publish") + Path.DirectorySeparatorChar,
            PublishCommandLine.PublishFolder(_project, profile));
    }

    [Fact]
    public void Dangerous_folders_are_never_cleared()
    {
        var projectFolder = Path.GetDirectoryName(_project)!;

        Assert.Null(PublishCommandLine.WhyNotClear(Path.Combine(projectFolder, "bin", "publish"), _project));
        Assert.NotNull(PublishCommandLine.WhyNotClear(projectFolder, _project));
        Assert.NotNull(PublishCommandLine.WhyNotClear(_workspace.Root, _project));
        Assert.NotNull(PublishCommandLine.WhyNotClear(Path.GetPathRoot(projectFolder)!, _project));
        Assert.NotNull(PublishCommandLine.WhyNotClear(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _project));
    }

    [Fact]
    public void Clearing_empties_the_folder_but_keeps_it()
    {
        var stale = _workspace.Write("App/out/old/stale.txt", "x");
        var folder = Path.GetDirectoryName(Path.GetDirectoryName(stale))!;

        PublishCommandLine.ClearFolder(folder);

        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }
}
