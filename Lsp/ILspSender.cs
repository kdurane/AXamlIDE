using System.Collections.Generic;

namespace AXamlIDE.Lsp;

public interface ILspSender
{
    bool SupportsIncrementalSync { get; }
    void DidChange(string uri, int version, IReadOnlyList<ContentChange> changes);
    void DidChangeFull(string uri, int version, string text);
}
