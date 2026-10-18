using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.AssemblyMetadata;

public interface IAssemblyProvider
{
    IEnumerable<string> GetAssemblies();
}
