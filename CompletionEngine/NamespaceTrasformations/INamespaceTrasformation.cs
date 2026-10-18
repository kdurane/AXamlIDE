using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.NamespaceTrasformations;

public interface INamespaceTrasformation
{
    public IEnumerable<char> Apply(IEnumerable<char> input);
}
