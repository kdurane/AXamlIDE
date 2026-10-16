using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.Completion
{
    public class CompletionSet
    {
        public int StartPosition { get; set; }

        public List<Completion> Completions { get; set; } = new();
    }
}
