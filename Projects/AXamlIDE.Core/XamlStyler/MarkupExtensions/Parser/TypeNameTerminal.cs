// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using Irony.Parsing;

namespace AXamlIDE.XamlStyler.MarkupExtensions.Parser
{
    internal class TypeNameTerminal : Terminal
    {
        public TypeNameTerminal(string name) : base(name)
        {
        }

        public override Token TryMatch(ParsingContext context, ISourceStream source)
        {
            while (!source.EOF())
            {
                switch (source.PreviewChar)
                {
                    case '\n':
                    case '\r':
                    case ' ':
                    case '}':
                        if (source.PreviewPosition > source.Position)
                        {
                            return source.CreateToken(this.OutputTerminal);
                        }

                        return context.CreateErrorToken($"{this.Name} was expected");
                }

                source.PreviewPosition++;
            }

            return context.CreateErrorToken($"Unbalanced {this.Name}");
        }
    }
}