using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AXamlIDE.Services.Workspace;

/// <summary>One file an item template writes.</summary>
public sealed record NewItemFile(string Path, string Text);

/// <summary>
/// An entry in Add New Item (Visual Studio's list): what it's called, where it's listed, and the files it writes
/// for a name and namespace. <see cref="Extension"/> is the main file's ("MainView" + ".axaml").
/// </summary>
public sealed record ItemTemplate(
    string Name, string Category, string Description, string DefaultName, string Extension, bool NeedsIdentifier,
    Func<string, string, IReadOnlyList<(string Extension, string Text)>> Files);

/// <summary>
/// The Add New Item templates (C#, Avalonia, General) and how a new item is planned: its namespace from the project
/// and folder (as Visual Studio does: RootNamespace + the folders below the project), its files, and the checks
/// (a valid name, nothing there already). The Avalonia ones are what `dotnet new avalonia.usercontrol` and friends
/// write. Projects/AXamlIDE.Tests covers them.
/// </summary>
public static partial class ItemTemplates
{
    public const string CSharp = "C#";
    public const string Avalonia = "Avalonia";
    public const string General = "General";

    public static IReadOnlyList<ItemTemplate> All { get; } =
    [
        Code("Class", "An empty class.", "Class1", (ns, name) => $$"""
            namespace {{ns}};

            public class {{name}}
            {
            }
            """),
        Code("Interface", "An empty interface.", "IInterface1", (ns, name) => $$"""
            namespace {{ns}};

            public interface {{name}}
            {
            }
            """),
        Code("Record", "An immutable record class.", "Record1", (ns, name) => $$"""
            namespace {{ns}};

            public sealed record {{name}}();
            """),
        Code("Enum", "An enumeration.", "Enum1", (ns, name) => $$"""
            namespace {{ns}};

            public enum {{name}}
            {
            }
            """),
        Code("Struct", "A value type.", "Struct1", (ns, name) => $$"""
            namespace {{ns}};

            public struct {{name}}
            {
            }
            """),

        new("User Control", Avalonia, "A control made of other controls: MyView.axaml with its code-behind.", "UserControl1", ".axaml", true,
            (ns, name) =>
            [
                (".axaml", $$"""
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                                 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                                 mc:Ignorable="d" d:DesignWidth="800" d:DesignHeight="450"
                                 x:Class="{{ns}}.{{name}}">
                        Welcome to Avalonia!
                    </UserControl>
                    """),
                (".axaml.cs", CodeBehind(ns, name, "UserControl"))
            ]),
        new("Window", Avalonia, "A top-level window with its code-behind.", "Window1", ".axaml", true,
            (ns, name) =>
            [
                (".axaml", $$"""
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                            xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                            mc:Ignorable="d" d:DesignWidth="800" d:DesignHeight="450"
                            x:Class="{{ns}}.{{name}}"
                            Title="{{name}}">
                        Welcome to Avalonia!
                    </Window>
                    """),
                (".axaml.cs", CodeBehind(ns, name, "Window"))
            ]),
        new("Templated Control", Avalonia, "A lookless control: the class and a ControlTheme with its template (add the .axaml to App.axaml's resources).", "TemplatedControl1", ".cs", true,
            (ns, name) =>
            [
                (".cs", $$"""
                    using Avalonia.Controls.Primitives;

                    namespace {{ns}};

                    public class {{name}} : TemplatedControl
                    {
                    }
                    """),
                (".axaml", $$"""
                    <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                        xmlns:controls="using:{{ns}}">
                        <Design.PreviewWith>
                            <controls:{{name}} />
                        </Design.PreviewWith>

                        <ControlTheme x:Key="{x:Type controls:{{name}}}" TargetType="controls:{{name}}">
                            <Setter Property="Template">
                                <ControlTemplate>
                                    <TextBlock Text="Templated Control" />
                                </ControlTemplate>
                            </Setter>
                        </ControlTheme>
                    </ResourceDictionary>
                    """)
            ]),
        new("Styles", Avalonia, "A Styles file (include it with <StyleInclude Source=\"...\" />).", "Styles1", ".axaml", false,
            (_, _) =>
            [
                (".axaml", """
                    <Styles xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                        <Design.PreviewWith>
                            <Border Padding="20">
                                <!-- Add controls for the previewer here -->
                            </Border>
                        </Design.PreviewWith>

                        <!-- Add styles here -->
                    </Styles>
                    """)
            ]),
        new("Resource Dictionary", Avalonia, "Brushes, themes and other resources (include it with <ResourceInclude Source=\"...\" />).", "Resources1", ".axaml", false,
            (_, _) =>
            [
                (".axaml", """
                    <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                        <!-- Add resources here -->
                    </ResourceDictionary>
                    """)
            ]),
        new("View Model", Avalonia, "A view model for CommunityToolkit.Mvvm: [ObservableProperty] and [RelayCommand] generate the rest.", "MainViewModel", ".cs", true,
            (ns, name) =>
            [
                (".cs", $$"""
                    using CommunityToolkit.Mvvm.ComponentModel;
                    using CommunityToolkit.Mvvm.Input;

                    namespace {{ns}};

                    public partial class {{name}} : ObservableObject
                    {
                        [ObservableProperty]
                        public partial string Title { get; set; } = "";
                    }
                    """)
            ]),

        Plain("Text File", "An empty text file.", "TextFile1", ".txt", ""),
        Plain("JSON File", "A JSON file.", "JsonFile1", ".json", "{\n}\n"),
        Plain("XML File", "An XML file.", "XmlFile1", ".xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"),
        Plain("Markdown File", "A Markdown file (README, notes).", "README", ".md", ""),
    ];

    public static IReadOnlyList<string> Categories { get; } = [CSharp, Avalonia, General];

    /// <summary>
    /// The files to write for <paramref name="name"/> (with or without the extension), or why not: an invalid name
    /// (a C# identifier where there's a class), or a file that's already there.
    /// </summary>
    public static (IReadOnlyList<NewItemFile> Files, string? Error) Plan(ItemTemplate template, string folder, string name)
    {
        name = name.Trim();
        if (name.EndsWith(template.Extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^template.Extension.Length];
        if (name.Length == 0)
            return ([], "Enter a name.");
        if (FileOperations.InvalidNameReason(name + template.Extension) is { } reason)
            return ([], reason);
        if (template.NeedsIdentifier && !Identifier().IsMatch(name))
            return ([], $"'{name}' isn't a valid C# name: use letters, digits and _, not starting with a digit.");

        var files = template.Files(NamespaceFor(folder), name)
            .Select(file => new NewItemFile(Path.Combine(folder, name + file.Extension), Normalize(file.Text)))
            .ToList();
        if (files.FirstOrDefault(file => File.Exists(file.Path) || Directory.Exists(file.Path)) is { } existing)
            return ([], $"'{Path.GetFileName(existing.Path)}' already exists here.");
        return (files, null);
    }

    /// <summary>Writes the planned files (never over an existing one). The first is the one to open.</summary>
    public static void Write(IReadOnlyList<NewItemFile> files)
    {
        foreach (var file in files)
        {
            using var stream = new FileStream(file.Path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(file.Text);
        }
    }

    /// <summary>
    /// The namespace for code in <paramref name="folder"/>: the nearest project's RootNamespace (else its file name)
    /// plus the folders below it, as Visual Studio names them; just the folder's name outside a project.
    /// </summary>
    public static string NamespaceFor(string folder)
    {
        folder = Path.GetFullPath(folder);
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var project = dir.EnumerateFiles("*.csproj").FirstOrDefault();
            if (project is null)
                continue;
            var root = RootNamespace().Match(File.ReadAllText(project.FullName)) is { Success: true } match
                ? match.Groups[1].Value.Trim()
                : Path.GetFileNameWithoutExtension(project.Name);
            var below = Path.GetRelativePath(dir.FullName, folder);
            var parts = below == "." ? [] : below.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Join(".", new[] { root }.Concat(parts).Select(SafeIdentifier));
        }
        return SafeIdentifier(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private static string SafeIdentifier(string part)
    {
        var safe = string.Join(".", part.Split('.').Select(piece =>
        {
            var cleaned = string.Concat(piece.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            return cleaned.Length == 0 || char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
        }));
        return safe;
    }

    /// <summary>Raw strings use \n; files get the system's line ending and end with one.</summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n");
        if (lines.Length > 0 && !lines.EndsWith('\n'))
            lines += "\n";
        return lines.Replace("\n", Environment.NewLine);
    }

    private static ItemTemplate Code(string name, string description, string defaultName, Func<string, string, string> text)
        => new(name, CSharp, description, defaultName, ".cs", true, (ns, n) => [(".cs", text(ns, n))]);

    private static ItemTemplate Plain(string name, string description, string defaultName, string extension, string text)
        => new(name, General, description, defaultName, extension, false, (_, _) => [(extension, text)]);

    private static string CodeBehind(string ns, string name, string baseType) => $$"""
        using Avalonia.Controls;

        namespace {{ns}};

        public partial class {{name}} : {{baseType}}
        {
            public {{name}}()
            {
                InitializeComponent();
            }
        }
        """;

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"<RootNamespace>\s*([^<]+?)\s*</RootNamespace>")]
    private static partial Regex RootNamespace();
}
