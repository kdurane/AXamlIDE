using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.CompletionEngine.Parsing;

namespace AXamlIDE.CompletionEngine.Manipulation;
    
    /// <summary>
    /// Manipulates document as user types text
    /// Closes xml tags, renames start and end tags at same time etc.
    /// </summary>
    public class TextManipulator
    {
        private readonly ReadOnlyMemory<char> _text;
        private readonly int _position;
        private readonly XmlParser _state;

        public TextManipulator(string text, int position)
        {
            _position = position;
            _text = text.AsMemory();

            var parserStart = 0;
            var parserEnd = 0;

            // Parse up to the caret; XmlParser.Parse always starts at 0 (it needs the nesting), the start here only
            // marks the last tag
            if (text.Length > 0)
            {
                // Find last < tag
                parserStart = position;
                if (position >= text.Length)
                {
                    parserStart = text.Length - 1;
                }
                parserStart = text.LastIndexOf('<', parserStart);
                if (parserStart < 0)
                {
                    parserStart = 0;
                }


                if (text.Length > position)
                {
                    parserEnd = position;
                }
                else
                {
                    parserEnd = text.Length;
                }
            }


            _state = XmlParser.Parse(_text, parserStart, parserEnd);
        }

        public IList<TextManipulation> ManipulateText(ITextChange textChange)
        {
            var maniplations = new List<TextManipulation>();
            if (_state.State == XmlParser.ParserState.StartElement
                || _state.State == XmlParser.ParserState.None && _text.Span[_state.ParserPos] == '>'
                )
            {
                SynchronizeStartAndEndTag(textChange, maniplations);
            }


            switch (_state.State)
            {
                case XmlParser.ParserState.StartElement:
                case XmlParser.ParserState.AfterAttributeValue:
                case XmlParser.ParserState.InsideElement:
                    new CloseXmlTagManipulation(_state, _text, _position).TryCloseTag(textChange, maniplations);
                    break;
                case XmlParser.ParserState.None when string.IsNullOrEmpty(textChange.OldText) && textChange.NewText == ">":
                {
                    var pp = textChange.NewPosition - 2;
                    // if xmltag already closed ingnore '>'
                    if (pp > -1 && _text.Span[pp] == '/' && _text.Span[pp + 1] == '>')
                    {
                        maniplations.Add(TextManipulation.Delete(textChange.NewPosition, 1));
                    }

                    break;
                }
            }

            return [.. maniplations.OrderByDescending(n => n.Start)];
        }

        private readonly char[] _xmlNameSpecialCharacters = ['-', '_', '.'];

        private void SynchronizeStartAndEndTag(ITextChange textChange, List<TextManipulation> maniplations)
        {
            if (!textChange.NewText.All(n => char.IsLetterOrDigit(n) || _xmlNameSpecialCharacters.Contains(n)))
            {
                return;
            }

            var startTag = _state.ParseCurrentTagName();
            var maybeTagStart = _state.CurrentValueStart;
            if (maybeTagStart == null || startTag is null)
            {
                return;
            }

            var startPos = maybeTagStart.Value; // add 1 to take opening < into account
            if (startTag.EndsWith('/'))
            {
                return; // start tag is self-closing
            }
            if (textChange.NewPosition < startPos || textChange.NewPosition > startPos + startTag.Length)
            {
                return; //we are not editing tag name
            }

            var searchEndTag = _state.Clone();
            if (!searchEndTag.SeekClosingTag()) return;
            var endTag = searchEndTag.ParseCurrentTagName();
            if (endTag is null || string.IsNullOrEmpty(endTag) || endTag[0] != '/')
            {
                return;
            }

            maybeTagStart = searchEndTag.CurrentValueStart;
            if (maybeTagStart == null)
            {
                return;
            }

            var endPos = maybeTagStart.Value; // add 1 to take opening < into account

            // reverse change to start tag
            startTag = textChange.ReverseOn(startTag, startPos);

            var isTheSameTag = endTag.Length > 0 && endTag[1..] == startTag;
            if (isTheSameTag)
            {
                maniplations.AddRange(textChange.AsManipulations(endPos - startPos));
            }
        }
    }
