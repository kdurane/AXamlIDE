using System.Collections.Generic;

namespace AXamlIDE.Interfaces;
    public interface INamespaceTrasformation
    {
        public IEnumerable<char> Apply(IEnumerable<char> input);
    }
