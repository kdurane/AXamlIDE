using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.Parsing;

    public class MarkupExtensionParser
    {
        private struct ParserState
        {
            public ParserStateType State { get; set; }
            public int ElementNameStart;
            public int? ElementNameEnd;
            public int? AttributeNameStart;
            public int? AttributeNameEnd;
            public int? AttributeValueStart;

            public void Reset(ParserStateType state)
            {
                State = state;
                ElementNameStart = 0;
                ElementNameEnd = AttributeNameStart = AttributeNameEnd = AttributeValueStart = null;
            }
        }

        public enum ParserStateType
        {
            None,
            StartElement,
            InsideElement,
            StartAttribute,
            AfterAttribute,
            BeforeAttributeValue,
            AttributeValue
        }

        public int CurrentValueStart
        {
            get
            {
                return State switch
                {
                    ParserStateType.StartElement => _state.ElementNameStart,
                    ParserStateType.StartAttribute => _state.AttributeNameStart ?? 0,
                    ParserStateType.BeforeAttributeValue => _state.AttributeValueStart ?? 0,
                    ParserStateType.AttributeValue => _state.AttributeValueStart ?? 0,
                    _ => 0
                };
            }
        }

        public ParserStateType State => _state.State;

        public string? ElementName
        {
            get
            {
                if (_data.Length == 0)
                    return null;
                if (_state.State < ParserStateType.StartElement)
                    return null;
                var endElement = _state.ElementNameEnd ?? _data.Length - 1;
                if (endElement < _state.ElementNameStart)
                    endElement = _state.ElementNameStart;
                return endElement >= _data.Length ? "" : _data.Substring(_state.ElementNameStart, endElement - _state.ElementNameStart + 1);
            }
        }

        public string? AttributeName
        {
            get
            {
                if (_state.State < ParserStateType.StartAttribute || _data.Length == 0)
                    return null;
                if (_state.AttributeNameStart == null || _state.AttributeNameEnd == null)
                    return null;
                if (_state.AttributeNameEnd.Value < _state.AttributeNameStart.Value)
                    return null;
                return _data.Substring(_state.AttributeNameStart.Value,
                    _state.AttributeNameEnd.Value - _state.AttributeNameStart.Value + 1);
            }
        }

        public string? AttributeValue
        {
            get
            {
                if (State < ParserStateType.AttributeValue || _data.Length == 0 || _state.AttributeValueStart == null)
                    return null;
                return _data[_state.AttributeValueStart.Value..];
            }
        }

        public int AttributesCount { get; private set; }

        private ParserState _state;
        private readonly Stack<ParserState> _stack = new();

        private readonly string _data;

        private MarkupExtensionParser(string data)
        {
            _data = data;
        }

        private void Parse()
        {
            AttributesCount = 0;
            for (var c = 0; c < _data.Length; c++)
            {
                var st = _state.State;
                var ch = _data[c];

                switch (ch)
                {
                    //Special symbols that we can handle ignoring current state (assuming that expression syntax is correct)
                    case ',':
                        _state.State = ParserStateType.InsideElement;
                        AttributesCount++;
                        break;
                    case '{':
                        {
                            if (st != ParserStateType.None)
                                _stack.Push(_state);
                            _state.Reset(ParserStateType.StartElement);
                            _state.ElementNameStart = c + 1;
                            AttributesCount = 0;
                            break;
                        }
                    case '}':
                        {
                            if (_stack.Count != 0)
                            {
                                _state = _stack.Pop();
                                _state.State = ParserStateType.InsideElement;
                            }

                            break;
                        }
                    //Regular state handling
                    default:
                        {
                            switch (st)
                            {
                                case ParserStateType.StartElement when _state.ElementNameStart == c && char.IsWhiteSpace(ch):
                                    _state.ElementNameStart++;
                                    break;
                                case ParserStateType.StartElement:
                                    {
                                        if (char.IsWhiteSpace(ch))
                                        {
                                            _state.ElementNameEnd = c;
                                            _state.State = ParserStateType.InsideElement;
                                        }

                                        break;
                                    }
                                case ParserStateType.InsideElement:
                                    {
                                        if (!char.IsWhiteSpace(ch))
                                        {
                                            _state.State = ParserStateType.StartAttribute;
                                            _state.AttributeNameStart = c;
                                        }

                                        break;
                                    }
                                case ParserStateType.StartAttribute when ch == '=':
                                    _state.AttributeNameEnd = c - 1;
                                    _state.State = ParserStateType.BeforeAttributeValue;
                                    _state.AttributeValueStart = c + 1;
                                    break;
                                case ParserStateType.StartAttribute when char.IsWhiteSpace(ch):
                                    _state.AttributeNameEnd = c - 1;
                                    _state.State = ParserStateType.AfterAttribute;
                                    break;
                                case ParserStateType.StartAttribute:
                                    _state.AttributeNameEnd = c;
                                    break;
                                case ParserStateType.AfterAttribute:
                                    {
                                        if (ch == '=')
                                            _state.State = ParserStateType.BeforeAttributeValue;
                                        break;
                                    }
                                case ParserStateType.BeforeAttributeValue:
                                    {
                                        if (!char.IsWhiteSpace(ch))
                                        {
                                            _state.AttributeValueStart = c;
                                            _state.State = ParserStateType.AttributeValue;
                                        }

                                        break;
                                    }
                            }

                            break;
                        }
                }
            }
        }

        public static MarkupExtensionParser Parse(string data)
        {
            var rv = new MarkupExtensionParser(data);
            rv.Parse();
            return rv;
        }

        public override string ToString()
        {
            return $"State: {State}, ElementName: {ElementName}, AttributeName: {AttributeName}, AttributeValue: {AttributeValue}";
        }
    }
