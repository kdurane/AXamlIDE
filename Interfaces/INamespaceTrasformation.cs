using System.Collections.Generic;

namespace AXaxmlIDE.Interfaces;

public interface INamespaceTrasformation
{
    public IEnumerable<char> Apply(IEnumerable<char> input);
}
