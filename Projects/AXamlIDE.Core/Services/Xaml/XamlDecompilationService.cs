using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;

namespace AXamlIDE.Services.Xaml;

public sealed record DecompiledSource(string Path, string TypeFullName, string Text);

public sealed class XamlDecompilationService
{
    public async Task<DecompiledSource> DecompileTypeAsync(string assemblyPath, string typeFullName, string? documentationPath)
    {

        var (body, assemblyName) = await Task.Run(() =>
        {
            var decompiler = new CSharpDecompiler(assemblyPath, new DecompilerSettings { FileScopedNamespaces = false });
            if (documentationPath is not null)
                decompiler.DocumentationProvider = new XmlDocumentationProvider(documentationPath);
            return (decompiler.DecompileTypeAsString(new FullTypeName(typeFullName)),
                    decompiler.TypeSystem.MainModule.FullAssemblyName);
        });
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException($"Couldn't decompile '{typeFullName}'.");

        var typeName = typeFullName.Split('.', '+').LastOrDefault() ?? "Type";
        var safeTypeName = string.Concat(typeName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeFullName)))[..12];
        var fullAssemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var path = Path.Combine(
            Path.GetTempPath(),
            "AXamlIDE",
            "Decompiled",
            fullAssemblyName,
            $"{typeFullName}.cs");
        var source = $"#region Assembly {fullAssemblyName}\n// {assemblyPath}\n#endregion\n\n{body}";
        return new DecompiledSource(path, typeFullName, source);
    }
}
