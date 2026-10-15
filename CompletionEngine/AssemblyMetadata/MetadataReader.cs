namespace AXaxmlIDE.CompletionEngine.AssemblyMetadata;

public class MetadataReader(IMetadataProvider provider)
{
    public Metadata? GetForTargetAssembly(IAssemblyProvider assemblyProvider)
    {
        using var session = provider.GetMetadata(assemblyProvider.GetAssemblies());
        return MetadataConverter.ConvertMetadata(session);
    }
}
