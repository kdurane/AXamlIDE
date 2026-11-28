using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AXamlIDE.Commands;
using AXamlIDE.Models;
using AXamlIDE.Services.Agents;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Workspace;
using AXamlIDE.Services.Xaml;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.SampleData;

/// <summary>Document tabs (editor, preview, diff, image), the Claude panel, the terminal, the dialogs and the main window.</summary>
public static partial class DesignData
{
    private const string SampleXaml = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:Contoso.App.ViewModels"
                     x:Class="Contoso.App.Views.OrdersView"
                     x:DataType="vm:OrdersViewModel">
            <DockPanel>
                <TextBox DockPanel.Dock="Top" Text="{Binding Filter}" Watermark="Filter orders" />
                <ListBox ItemsSource="{Binding Orders}" />
            </DockPanel>
        </UserControl>
        """;

    // ------------------------------------------------------------ document tabs

    /// <summary>An open AXAML file that changed on disk under unsaved edits (the tab's banner shows).</summary>
    private static readonly Lazy<OpenDocument> LazyXamlDocument = new(() =>
    {
        var document = new OpenDocument(SamplePath("Contoso.App/Views/OrdersView.axaml"), SampleXaml, Encoding.UTF8, LanguageIds.Axaml);
        document.Document.Insert(document.Document.TextLength, "\n");   // unsaved
        document.MarkChangedOnDisk();
        return document;
    });

    public static DocumentDockable Document => LazyDocument.Value;

    private static readonly Lazy<DocumentDockable> LazyDocument = new(() => new DocumentDockable(LazyXamlDocument.Value,
        new TabActions(Get<DocumentCommands>(), Get<IClipboardService>(), Get<IDialogService>()), PreviewMode.Split, PreviewSplit.SideBySide, previewFirst: false));

    public static PreviewPaneViewModel PreviewPane => LazyPreviewPane.Value;

    private static readonly Lazy<PreviewPaneViewModel> LazyPreviewPane = new(() =>
        new PreviewPaneViewModel(LazyXamlDocument.Value, Get<XamlLanguageService>(), Get<IMessenger>()));

    public static DiffDockable Diff => LazyDiff.Value;

    private static readonly Lazy<DiffDockable> LazyDiff = new(() =>
    {
        _ = Git;   // the sample repository's status
        var change = new GitChangeItem(new GitChange("Contoso.App/Models/Order.cs", SamplePath("Contoso.App/Models/Order.cs"),
            GitFileState.None, GitFileState.Modified), IsStagedSide: false);
        var diff = new DiffDockable(change, SampleRoot, Get<GitService>(), Get<GitStatusTracker>(), Get<IDocumentHost>());
        diff.Diff = SideBySideDiff.Compare(
            "public sealed class Order\n{\n    public int Id { get; set; }\n    public Customer Customer { get; set; }\n    public decimal Total { get; set; }\n}\n",
            "public sealed class Order\n{\n    public int Id { get; set; }\n    public Customer? Customer { get; set; }\n    public decimal Total { get; set; }\n    public bool IsOpen { get; set; } = true;\n}\n");
        return diff;
    });

    public static ImageDockable Image => LazyImage.Value;

    private static readonly Lazy<ImageDockable> LazyImage = new(() =>
    {
        // The viewer opens files: a gradient drawn here and saved as one.
        var path = Path.Combine(Path.GetTempPath(), "AXamlIDE-design-sample.png");
        if (!File.Exists(path))
        {
            using var bitmap = new WriteableBitmap(new PixelSize(320, 200), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var pixels = bitmap.Lock())
            {
                var row = new byte[pixels.RowBytes];
                for (var y = 0; y < 200; y++)
                {
                    for (var x = 0; x < 320; x++)
                    {
                        row[x * 4] = (byte)(255 - y);           // blue
                        row[x * 4 + 1] = (byte)(x * 255 / 320); // green
                        row[x * 4 + 2] = 120;                   // red
                        row[x * 4 + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
                }
            }
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }
        return new ImageDockable(path);
    });

    // ------------------------------------------------------------ Claude panel and terminal

    public static AgentChatViewModel AgentChat => LazyAgentChat.Value;

    private static readonly Lazy<AgentChatViewModel> LazyAgentChat = new(() =>
    {
        _ = Solution;   // Claude works in the workspace folder
        var chat = Get<AgentChatViewModel>();
        chat.State = AgentChatState.Ready;   // not Idle: nothing is started
        chat.Title = "Orders without a customer";
        chat.UsageText = "34k / 200k tokens";
        chat.Items.Add(new UserMessageItem("Why does the orders list crash when an order has no customer?", []));
        chat.Items.Add(new AgentTextItem(isThought: true, null) { Text = "The list binds Customer.Name; a null Customer would throw in the converter." });
        chat.Items.Add(new ToolCallItem(chat, new AcpToolCall("1", true, "Read OrdersViewModel.cs", "read", "completed", null,
            [new AcpLocation(SamplePath("Contoso.App/ViewModels/OrdersViewModel.cs"), 44)])));
        chat.Items.Add(new ToolCallItem(chat, new AcpToolCall("2", true, "Edit Order.cs", "edit", "completed",
            [new AcpDiffContent(SamplePath("Contoso.App/Models/Order.cs"), "    public Customer Customer { get; set; }", "    public Customer? Customer { get; set; }"),
             new AcpDiffContent(SamplePath("Contoso.App/Converters/NoneConverter.cs"), null, "public sealed class NoneConverter : IValueConverter")], null)));
        const string script = "python3 - <<'EOF'\nimport json, sys\nd = json.load(open('orders.json'))\nprint(sum(1 for o in d if o.get('customer') is None))\nEOF";
        chat.Items.Add(new ToolCallItem(chat, new AcpToolCall("3", true, script, "execute", "completed",
            [new AcpTextContent("```console\n12\n```")], null, script, "Count orders with no customer")));
        var plan = new PlanItem();
        plan.Show([new AcpPlanEntry("Find where Customer is read", "completed"), new AcpPlanEntry("Make Customer nullable", "in_progress"),
            new AcpPlanEntry("Show \"(none)\" in the list", "pending")]);
        chat.Items.Add(plan);
        chat.Items.Add(new UserMessageItem("Keep the old converter, don't add a new one", []) { Delivery = "Sent while Claude was working" });
        chat.Items.Add(new AgentTextItem(isThought: false, null)
        {
            Text = "`Order.Customer` can be null for orders imported from the old system, and the list reads `Customer.Name`.\n\n" +
                   "I made `Customer` nullable and the list now shows **(none)** instead:\n\n```csharp\nvar name = order.Customer?.Name ?? \"(none)\";\n```"
        });
        chat.PromptText = "Add a test for it";
        return chat;
    });

    public static TerminalViewModel Terminal => Get<TerminalViewModel>();

    /// <summary>The Run panel before anything has run (a terminal shows nothing in the previewer).</summary>
    public static RunTerminalViewModel RunTerminal => Get<RunTerminalViewModel>();

    // ------------------------------------------------------------ dialogs and windows

    public static NewProjectViewModel NewProject
    {
        get
        {
            var newProject = new NewProjectViewModel(SampleRoot, Get<IDialogService>());
            foreach (var template in new ProjectTemplate[]
                     {
                         new("Avalonia .NET App", "avalonia.app", ["C#", "F#"], ["Desktop", "Xaml", "Avalonia"]),
                         new("Avalonia .NET MVVM App", "avalonia.mvvm", ["C#", "F#"], ["Desktop", "Xaml", "Avalonia"]),
                         new("Avalonia Cross Platform Application", "avalonia.xplat", ["C#", "F#"], ["Desktop", "Mobile", "Web", "Avalonia"]),
                         new("Class Library", "classlib", ["C#", "F#", "VB"], ["Common", "Library"]),
                         new("Console App", "console", ["C#", "F#", "VB"], ["Common", "Console"]),
                         new("xUnit Test Project", "xunit", ["C#", "F#", "VB"], ["Test", "xUnit"])
                     })
                newProject.Templates.Add(template);
            newProject.SelectedTemplate = newProject.Templates[1];
            return newProject;
        }
    }

    public static AddNewItemViewModel AddNewItem => new(SamplePath("Contoso.App/Views"), SampleRoot);

    public static SaveChangesViewModel SaveChanges => new(
    [
        SamplePath("Contoso.App/Models/Order.cs"),
        SamplePath("Contoso.App/Views/MainWindow.axaml"),
        SamplePath("Contoso.App/ViewModels/MainWindowViewModel.cs"),
    ]);

    public static WarningViewModel Warning => new(
        "Are you sure you want to undo the changes in these 2 files? This can't be undone.",
        [SamplePath("Contoso.App/Models/Order.cs"), SamplePath("Contoso.App/Views/MainWindow.axaml")],
        ["Undo Changes"]);

    public static CloneRepositoryViewModel CloneRepository => new(SampleRoot, Get<IDialogService>());

    public static PublishWizardViewModel PublishWizard => new(SamplePath("Contoso.App/Contoso.App.csproj"), "net10.0");

    public static PublishSettingsViewModel PublishSettings => new(SamplePath("Contoso.App/Contoso.App.csproj"),
        new PublishProfile("FolderProfile", SamplePath("Contoso.App/Properties/PublishProfiles/FolderProfile.pubxml"), "Folder",
            "bin/Release/net10.0/publish/", RuntimeIdentifier: "linux-x64", SelfContained: true, PublishSingleFile: true),
        ["net10.0"]);

    public static StartViewModel Start => Get<StartViewModel>();

    public static SettingsViewModel Settings => Get<SettingsViewModel>();

    public static WelcomeViewModel Welcome => new(Get<WorkspaceViewModel>());

    public static MainWindowViewModel MainWindow => LazyMainWindow.Value;

    private static readonly Lazy<MainWindowViewModel> LazyMainWindow = new(() =>
    {
        _ = Solution;
        _ = Problems;
        return Get<MainWindowViewModel>();
    });
}
