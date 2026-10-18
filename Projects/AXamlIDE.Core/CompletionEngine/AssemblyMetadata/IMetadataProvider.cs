using System;
using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.AssemblyMetadata;

public interface IMetadataProvider
{
    IMetadataReaderSession GetMetadata(IEnumerable<string> paths);
}

public interface IMetadataReaderSession : IDisposable
{
    string? TargetAssemblyName { get; }
    IReadOnlyCollection<IAssemblyInformation> Assemblies { get; }
}
