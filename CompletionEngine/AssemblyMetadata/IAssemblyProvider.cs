using System.Collections.Generic;

namespace AXaxmlIDE.CompletionEngine.AssemblyMetadata
{
    public interface IAssemblyProvider
    {
        IEnumerable<string> GetAssemblies();
    }
}
