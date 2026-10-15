using System.Collections.Generic;

namespace AXaxmlIDE.CompletionEngine.Completion;

public class CompletionSet
{
    public int StartPosition { get; set; }

    public List<Completion> Completions { get; set; } = new();
}
