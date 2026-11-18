using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.NuGet;
using AXamlIDE.Services.Testing;
using AXamlIDE.Services.Workspace;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.SampleData;

/// <summary>
/// The workspace's panels with the made-up Contoso solution open: the explorer tree, its Properties, the outline,
/// search results, Git changes, tests, NuGet packages and a publish profile. Nothing is read from disk: the tree is
/// built by hand and each panel is given what its loader would have found.
/// </summary>
public static partial class DesignData
{
    // ------------------------------------------------------------ the solution (Explorer)

    private static readonly Lazy<ProjectTreeNode> LazySolution = new(() =>
    {
        ProjectTreeNode File(string relative) => new(Path.GetFileName(relative), SamplePath(relative), NodeKind.File, null);

        ProjectTreeNode Folder(string relative, params ProjectTreeNode[] children)
            => Open(new ProjectTreeNode(Path.GetFileName(relative), SamplePath(relative), NodeKind.Folder, () => children));

        var app = Open(new ProjectTreeNode("Contoso.App", SamplePath("Contoso.App/Contoso.App.csproj"), NodeKind.Project, () =>
        [
            Folder("Contoso.App/Models", File("Contoso.App/Models/Customer.cs"), File("Contoso.App/Models/Order.cs")),
            Folder("Contoso.App/ViewModels", File("Contoso.App/ViewModels/MainViewModel.cs"), File("Contoso.App/ViewModels/OrdersViewModel.cs")),
            Folder("Contoso.App/Views", File("Contoso.App/Views/MainWindow.axaml"), File("Contoso.App/Views/OrdersView.axaml")),
            File("Contoso.App/App.axaml"),
            File("Contoso.App/Program.cs")
        ]));
        var core = new ProjectTreeNode("Contoso.Core", SamplePath("Contoso.Core/Contoso.Core.csproj"), NodeKind.Project,
            () => [File("Contoso.Core/OrderService.cs")]);
        var tests = new ProjectTreeNode("Contoso.Tests", SamplePath("Contoso.Tests/Contoso.Tests.csproj"), NodeKind.Project,
            () => [File("Contoso.Tests/OrderServiceTests.cs")]);
        var solution = Open(new ProjectTreeNode("Contoso", SamplePath("Contoso.sln"), NodeKind.Solution, () => [app, core, tests]));

        var workspace = Get<WorkspaceModel>();
        workspace.SolutionProjects = [app.FullPath, core.FullPath, tests.FullPath];
        workspace.WorkspaceRoots.Add(solution);
        return solution;
    });

    /// <summary>Filled and expanded, as if the user had opened it.</summary>
    private static ProjectTreeNode Open(ProjectTreeNode node)
    {
        node.Populate();
        node.IsExpanded = true;
        return node;
    }

    /// <summary>The Contoso solution is open (the explorer's tree, and what other panels read the workspace from).</summary>
    private static ProjectTreeNode Solution => LazySolution.Value;

    public static ExplorerViewModel Explorer => LazyExplorer.Value;

    private static readonly Lazy<ExplorerViewModel> LazyExplorer = new(() =>
    {
        var explorer = Get<ExplorerViewModel>();
        var app = Solution.Children[0];
        explorer.Workspace.SelectedNode = app.Children[1].Children[1];   // OrdersViewModel.cs
        return explorer;
    });

    // ------------------------------------------------------------ Properties

    public static PropertiesViewModel Properties => LazyProperties.Value;

    private static readonly Lazy<PropertiesViewModel> LazyProperties = new(() =>
    {
        var properties = Get<PropertiesViewModel>();
        properties.Heading = "OrdersView.axaml  File Properties";
        properties.Groups.Add(new PropertyGroup("Advanced",
        [
            new PropertyRow("Build Action", "AvaloniaXaml", "How the file is used by the build.", ["None", "Compile", "Content", "AvaloniaXaml", "AvaloniaResource", "EmbeddedResource"], _ => System.Threading.Tasks.Task.CompletedTask),
            new PropertyRow("Copy to Output Directory", "Do not copy", "Whether the file is copied to the output folder.", ["Do not copy", "Copy always", "Copy if newer"], _ => System.Threading.Tasks.Task.CompletedTask),
            new PropertyRow("Custom Tool", "", "A tool that generates code from the file.", null, _ => System.Threading.Tasks.Task.CompletedTask)
        ]));
        properties.Groups.Add(new PropertyGroup("Misc",
        [
            new PropertyRow("File Name", "OrdersView.axaml", "The file's name."),
            new PropertyRow("Full Path", SamplePath("Contoso.App/Views/OrdersView.axaml"), "Where the file is.")
        ]));
        return properties;
    });

    // ------------------------------------------------------------ Outline

    public static OutlineViewModel Outline => LazyOutline.Value;

    private static readonly Lazy<OutlineViewModel> LazyOutline = new(() =>
    {
        var outline = Get<OutlineViewModel>();
        foreach (var (name, kind) in new[]
                 {
                     ("OrdersViewModel", LspSymbolKind.Class), ("  Filter", LspSymbolKind.Property), ("  Orders", LspSymbolKind.Property),
                     ("  OrdersViewModel(OrderService orders)", LspSymbolKind.Constructor), ("  Refresh()", LspSymbolKind.Method)
                 })
            outline.Items.Add(new DocumentOutlineItem(name, kind, 0, 0, 0));
        outline.SelectedItem = outline.Items[4];
        return outline;
    });

    // ------------------------------------------------------------ Search (find in files) and Search Everywhere

    public static SearchViewModel Search => LazySearch.Value;

    private static readonly Lazy<SearchViewModel> LazySearch = new(() =>
    {
        _ = Solution;
        var search = Get<SearchViewModel>();
        search.SearchQuery = "Customer";
        // The search trims each line it reports (the column is still the untrimmed line's).
        WorkspaceSearchResult Line(string file, int line, int column, string text) => new(SamplePath(file), line, column, text);
        foreach (var group in SearchResultGroup.Build(SampleRoot,
                 [
                     Line("Contoso.App/Models/Order.cs", 7, 12, "public Customer Customer { get; set; }"),
                     Line("Contoso.App/Models/Customer.cs", 3, 21, "public sealed class Customer"),
                     Line("Contoso.App/ViewModels/OrdersViewModel.cs", 44, 31, "var name = order.Customer?.Name ?? \"(none)\";"),
                     Line("Contoso.App/ViewModels/OrdersViewModel.cs", 61, 17, "if (order.Customer is null)")
                 ], "Customer"))
            search.SearchResults.Add(group);
        search.SearchStatus = "4 results in 3 files.";
        return search;
    });

    public static SearchEverywhereViewModel SearchEverywhere => LazySearchEverywhere.Value;

    private static readonly Lazy<SearchEverywhereViewModel> LazySearchEverywhere = new(() =>
    {
        var everywhere = Get<SearchEverywhereViewModel>();
        SearchEverywhereItem Item(string title, string detail, string icon, string section, string? key = null)
            => new() { Title = title, Detail = detail, Icon = icon, Section = section, Shortcut = key, Run = () => { } };
        everywhere.Results =
        [
            Item("OrdersViewModel", "Contoso.App.ViewModels", "Icon.Class", "Types"),
            Item("OrderService", "Contoso.Core", "Icon.Class", "Types"),
            Item("OrdersView.axaml", "Contoso.App/Views", "Icon.File", "Files"),
            Item("Order.cs", "Contoso.App/Models", "Icon.File", "Files"),
            Item("Open Orders...", "Edit", "Icon.Search", "Actions", "Ctrl+O")
        ];
        everywhere.Results[0].ShowsSection = true;
        everywhere.Results[2].ShowsSection = true;
        everywhere.Results[4].ShowsSection = true;
        everywhere.SelectedIndex = 0;
        return everywhere;
    });

    // ------------------------------------------------------------ Git

    public static GitViewModel Git => LazyGit.Value;

    private static readonly Lazy<GitViewModel> LazyGit = new(() =>
    {
        _ = Solution;   // open first: opening a workspace makes the tracker look for its repository again
        var git = Get<GitViewModel>();
        GitChange Change(string relative, GitFileState index, GitFileState workTree)
            => new(relative, SamplePath(relative), index, workTree);
        Get<GitStatusTracker>().ShowForDesign(new GitStatus(SampleRoot, "main", "4f2a9c1", "origin/main", 2, 0,
        [
            Change("Contoso.App/ViewModels/OrdersViewModel.cs", GitFileState.Modified, GitFileState.None),
            Change("Contoso.App/Models/Order.cs", GitFileState.None, GitFileState.Modified),
            Change("Contoso.App/Views/OrdersView.axaml", GitFileState.None, GitFileState.Modified),
            Change("Contoso.App/Models/Customer.cs", GitFileState.None, GitFileState.Untracked)
        ], []));
        git.CommitMessage = "Show orders without a customer";
        return git;
    });

    // ------------------------------------------------------------ Tests

    public static TestExplorerViewModel TestExplorer => LazyTests.Value;

    private static readonly Lazy<TestExplorerViewModel> LazyTests = new(() =>
    {
        var tests = Get<TestExplorerViewModel>();
        var project = SamplePath("Contoso.Tests/Contoso.Tests.csproj");
        TestNode Test(string method, TestOutcome outcome, int milliseconds, string? error = null)
        {
            var name = TestCaseName.Parse("Contoso.Tests.OrderServiceTests." + method);
            return new TestNode(TestNodeKind.Method, method, project, name.FullName)
            {
                Test = name,
                Outcome = outcome,
                TestCount = 1,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                Result = new TestResult(name, outcome, TimeSpan.FromMilliseconds(milliseconds), error,
                    error is null ? null : "   at Contoso.Tests.OrderServiceTests.Totals_include_tax() in " + SamplePath("Contoso.Tests/OrderServiceTests.cs") + ":line 31", null)
            };
        }

        var cls = new TestNode(TestNodeKind.Class, "OrderServiceTests", project, "Contoso.Tests.OrderServiceTests") { Outcome = TestOutcome.Failed, TestCount = 4, IsExpanded = true };
        foreach (var test in new[]
                 {
                     Test("Loads_orders", TestOutcome.Passed, 12), Test("Skips_closed_orders", TestOutcome.Passed, 3),
                     Test("Totals_include_tax", TestOutcome.Failed, 41, "Assert.Equal() Failure: Values differ\nExpected: 144.6\nActual:   120.5"),
                     Test("Exports_to_csv", TestOutcome.Skipped, 0)
                 })
            cls.Children.Add(test);
        var ns = new TestNode(TestNodeKind.Namespace, "Contoso.Tests", project, "Contoso.Tests") { Outcome = TestOutcome.Failed, TestCount = 4, IsExpanded = true };
        ns.Children.Add(cls);
        var root = new TestNode(TestNodeKind.Project, "Contoso.Tests", project, project) { Outcome = TestOutcome.Failed, TestCount = 4, IsExpanded = true };
        root.Children.Add(ns);
        tests.Roots.Add(root);
        tests.PassedCount = 2;
        tests.FailedCount = 1;
        tests.SkippedCount = 1;
        tests.SelectedNode = cls.Children[2];
        return tests;
    });

    // ------------------------------------------------------------ NuGet and Publish (their tabs)

    public static NuGetDockable NuGet => LazyNuGet.Value;

    private static readonly Lazy<NuGetDockable> LazyNuGet = new(() =>
    {
        var nuget = new NuGetDockable(SamplePath("Contoso.App/Contoso.App.csproj"), Get<NuGetService>(), Get<IMessenger>());
        NuGetPackageRow Package(string id, string installed, string latest, string description, long downloads) => new(id)
        {
            InstalledVersion = installed, LatestVersion = latest, Description = description, Authors = "Avalonia Team",
            TotalDownloads = downloads, IsVerified = true
        };
        nuget.Packages.Add(Package("Avalonia", "12.1.6", "12.1.6", "A cross-platform UI framework for .NET.", 41_000_000));
        nuget.Packages.Add(Package("Avalonia.Desktop", "12.1.4", "12.1.6", "Desktop platform support for Avalonia.", 30_000_000));
        nuget.Packages.Add(Package("CommunityToolkit.Mvvm", "8.4.0", "8.4.0", "Fast and modular MVVM library.", 25_000_000));
        nuget.SelectedPackage = nuget.Packages[1];
        return nuget;
    });

    public static PublishDockable Publish => LazyPublish.Value;

    private static readonly Lazy<PublishDockable> LazyPublish = new(() =>
    {
        var project = SamplePath("Contoso.App/Contoso.App.csproj");
        var publish = new PublishDockable(project, Get<RunViewModel>(), Get<WorkspaceModel>(),
            Get<Services.Dialogs.IDialogService>(), Get<Services.Documents.IDocumentHost>(), Get<IMessenger>());
        var profile = new PublishProfile("FolderProfile", SamplePath("Contoso.App/Properties/PublishProfiles/FolderProfile.pubxml"),
            "Folder", "bin/Release/net10.0/publish/", RuntimeIdentifier: "linux-x64", SelfContained: true, PublishSingleFile: true);
        publish.Profiles.Add(profile);
        publish.SelectedProfile = profile;
        publish.StatusText = "Ready to publish.";
        return publish;
    });
}
