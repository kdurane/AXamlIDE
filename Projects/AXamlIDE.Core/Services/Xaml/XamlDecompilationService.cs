using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace AXamlIDE.Services.Xaml;

public sealed record DecompiledSource(string Path, string TypeFullName, string Text);

public sealed class XamlDecompilationService
{
    public async Task<DecompiledSource> DecompileTypeAsync(string assemblyPath, string typeFullName)
    {
        var source = await Task.Run(() => new CSharpDecompiler(
                assemblyPath, new DecompilerSettings())
            .DecompileTypeAsString(new FullTypeName(typeFullName)));
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException($"Couldn't decompile '{typeFullName}'.");

        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var typeName = typeFullName.Split('.', '+').LastOrDefault() ?? "Type";
        var safeTypeName = string.Concat(typeName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeFullName)))[..12];
        var path = Path.Combine(
            Path.GetTempPath(),
            "AXamlIDE",
            "Decompiled",
            assemblyName,
            $"{safeTypeName}-{hash}.cs");
        return new DecompiledSource(path, typeFullName, source);
    }
}
