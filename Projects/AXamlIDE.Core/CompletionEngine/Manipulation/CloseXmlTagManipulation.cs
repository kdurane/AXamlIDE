using System;
using System.Collections.Generic;
using AXamlIDE.CompletionEngine.Parsing;

namespace AXamlIDE.CompletionEngine.Manipulation;
    public class CloseXmlTagManipulation(XmlParser state, ReadOnlyMemory<char> text, int position)
    {
        private readonly ReadOnlyMemory<char> _text = text;

        public void TryCloseTag(ITextChange textChange, IList<TextManipulation> manipulations)
        {
            var currentTag = state.ParseCurrentTagName();
            if (textChange.NewText != "/" || string.IsNullOrEmpty(currentTag) || currentTag == "/") return;
            var text = _text.Span;
            var pos = state.ParserPos;
            var c = ' ';
            while (char.IsWhiteSpace(c) && text.Length > pos + 1)
            {
                pos++;
                c = text[pos];
            }

            var tagAlreadyClosed = c == '>';
            if (!tagAlreadyClosed)
            {
                manipulations.Add(TextManipulation.Insert(position + 1, $">"));
            }
            else
            {
                var closingTagPos = FindClosingTag(currentTag, pos + 1);
                if (closingTagPos == null) return;
                manipulations.Add(TextManipulation.Insert(position + 1, $">"));
                manipulations.Add(TextManipulation.Delete(position + 1, closingTagPos.Value - position));
            }
        }

        /// <summary>
        /// If we just changed open tag to self-closing this finds closing tag if closed tag was empty
        /// </summary>
        /// <returns>Position of closing brace of closing tag if this tag is empty and self closing</returns>
        private int? FindClosingTag(string currentTag, int startPos)
        {
            var pos = startPos;

            var text = _text.Span;
            if (pos >= text.Length)
            {
                return null;
            }

            pos = SkipWhitespace(pos, text);

            // Check next text is </closingTag>
            if (text.Length < pos + currentTag.Length + 3
                || text[pos] != '<'
                || text[pos + 1] != '/')
            {
                return null;
            }

            pos += 2;
            foreach (var t in currentTag)
            {
                if (text[pos] != t)
                {
                    return null;
                }
                pos++;
            }

            pos = SkipWhitespace(pos, text);

            if (text.Length <= pos || text[pos] != '>')
            {
                return null;
            }

            return pos;
        }

        private static int SkipWhitespace(int pos, ReadOnlySpan<char> text)
        {
            for (; text.Length > pos; pos++)
            {
                if (!char.IsWhiteSpace(text[pos]))
                {
                    return pos;
                }
            }
            return pos;
        }
    }
