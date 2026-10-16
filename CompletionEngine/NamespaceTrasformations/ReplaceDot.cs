using System.Collections.Generic;
using AXamlIDE.Interfaces;

namespace AXamlIDE.CompletionEngine.NamespaceTrasformations
{
    internal class ReplaceDot(char sobstituion) : INamespaceTrasformation
    {
        public IEnumerable<char> Apply(IEnumerable<char> input)
        {
            foreach (var c in input)
            {
                if (c == '.')
                    yield return sobstituion;
                else
                {
                    yield return c;
                }
            }
        }
    }
}
