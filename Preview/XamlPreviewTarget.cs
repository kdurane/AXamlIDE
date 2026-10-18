using System;
using System.IO;

namespace AXamlIDE.Preview;

public sealed record XamlPreviewTarget(
    string XamlAssemblyPath,
    string ExecutableAssemblyPath,
    string HostAppPath,
    DateTime XamlAssemblyLastWriteUtc,
    DateTime ExecutableAssemblyLastWriteUtc,
    DateTime HostAppLastWriteUtc)
{
    public bool IsCurrent
        => File.GetLastWriteTimeUtc(XamlAssemblyPath) == XamlAssemblyLastWriteUtc
           && File.GetLastWriteTimeUtc(ExecutableAssemblyPath) == ExecutableAssemblyLastWriteUtc
           && File.GetLastWriteTimeUtc(HostAppPath) == HostAppLastWriteUtc;
}
