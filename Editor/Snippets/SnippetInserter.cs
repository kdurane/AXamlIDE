using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Snippets;
using AXamlIDE.Services.Snippets;

namespace AXamlIDE.Editor.Snippets;

/// <summary>
/// Inserts a <see cref="SnippetDefinition"/> with AvaloniaEdit's snippet engine: the fields are highlighted and Tab /
/// Shift+Tab move between them, a field used twice is typed in both places, Enter or Escape finishes (Enter leaves the
/// caret at $0). Multi-line bodies are indented like the line they go on, and \t becomes the file's indent unit.
/// </summary>
public static class SnippetInserter
{
    /// <summary>
    /// Replaces <paramref name="start"/>..caret (the typed prefix, if any) with the snippet. A selection becomes
    /// $TM_SELECTED_TEXT (Insert Snippet with text selected surrounds it).
    /// </summary>
    public static void Insert(TextArea textArea, SnippetDefinition definition, int start, string? filePath)
    {
        var caret = textArea.Caret.Offset;
        if (start < caret)
        {
            textArea.Document.Remove(start, caret - start);
            textArea.Caret.Offset = start;
        }
        Build(definition, filePath, IndentUnit.Detect(textArea.Document.Text)).Insert(textArea);
    }

    private static Snippet Build(SnippetDefinition definition, string? filePath, string indent)
    {
        var parts = SnippetBody.Parse(definition.Body, name => Variable(name, filePath));
        var fields = new Dictionary<int, SnippetFieldElement>();
        var snippet = new Snippet();
        var lastText = "";
        foreach (var part in parts)
        {
            switch (part)
            {
                case SnippetText text:
                    snippet.Elements.Add(new SnippetTextElement { Text = text.Text.Replace("\t", indent) });
                    lastText = text.Text;
                    continue;
                case SnippetField field when fields.TryGetValue(field.Index, out var first):
                    snippet.Elements.Add(new SnippetBoundElement { TargetElement = first });
                    break;
                case SnippetField field:
                    // The default may come from a later occurrence ($1 ... ${1:name}), as in VS Code.
                    var text1 = parts.OfType<SnippetField>().FirstOrDefault(f => f.Index == field.Index && f.Default.Length > 0)?.Default ?? "";
                    fields[field.Index] = new SnippetFieldElement { Text = text1 };
                    snippet.Elements.Add(fields[field.Index]);
                    break;
                case SnippetEnd:
                    snippet.Elements.Add(new SnippetCaretElement());
                    break;
                case SnippetVariable { Name: "TM_SELECTED_TEXT" or "SELECTION" }:
                    // Indented by the tabs before it on its line, so a selected block moves into the body.
                    var line = lastText[(lastText.LastIndexOf('\n') + 1)..];
                    snippet.Elements.Add(new SelectionElement(line.All(c => c == '\t') ? string.Concat(Enumerable.Repeat(indent, line.Length)) : ""));
                    break;
                case SnippetVariable variable:
                    snippet.Elements.Add(new SnippetTextElement { Text = Variable(variable.Name, filePath) ?? variable.Default });
                    break;
            }
            lastText = "";
        }
        return snippet;
    }

    /// <summary>
    /// $TM_SELECTED_TEXT: the selection, its later lines indented by <paramref name="indent"/> more (AvaloniaEdit's
    /// SnippetSelectionElement does the same with the editor's tab setting, not the file's).
    /// </summary>
    private sealed class SelectionElement(string indent) : SnippetElement
    {
        public override void Insert(InsertionContext context)
        {
            var text = context.SelectedText.TrimStart(' ', '\t').Replace(context.LineTerminator, context.LineTerminator + indent);
            context.Document.Insert(context.InsertionPosition, text);
            context.InsertionPosition += text.Length;
        }
    }

    /// <summary>VS Code's snippet variables that make sense here; null for the rest (the default is used).</summary>
    private static string? Variable(string name, string? filePath)
    {
        var fileName = filePath is null ? null : Path.GetFileName(filePath);
        return name switch
        {
            "TM_FILENAME" => fileName,
            // MainView.axaml.cs -> MainView: what a class in the file is called.
            "TM_FILENAME_BASE" => fileName?.Split('.')[0],
            "TM_FILEPATH" => filePath,
            "TM_DIRECTORY" => filePath is null ? null : Path.GetDirectoryName(filePath),
            "CURRENT_YEAR" => DateTime.Now.ToString("yyyy"),
            "CURRENT_MONTH" => DateTime.Now.ToString("MM"),
            "CURRENT_DATE" => DateTime.Now.ToString("dd"),
            "UUID" => Guid.NewGuid().ToString(),
            _ => null
        };
    }
}
