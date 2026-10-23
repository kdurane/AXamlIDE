using System.Collections.Generic;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// Reads semantic tokens (textDocument/semanticTokens) from the compact form servers send them in: five numbers per
/// token, each position relative to the token before it.
/// </summary>
public static class SemanticTokens
{
    /// <summary>
    /// The tokens in <paramref name="data"/>. Per token: line (added to the previous token's line), start character
    /// (added to the previous token's start when on the same line, else from the start of the line), length, the
    /// token type (an index into <paramref name="tokenTypes"/>, the server's legend) and the modifier bits.
    /// Tokens of a type the legend doesn't have are skipped.
    /// </summary>
    public static IReadOnlyList<LspSemanticToken> Decode(IReadOnlyList<int> data, IReadOnlyList<string> tokenTypes)
    {
        var tokens = new List<LspSemanticToken>(data.Count / 5);
        var line = 0;
        var character = 0;
        for (var i = 0; i + 4 < data.Count; i += 5)
        {
            var deltaLine = data[i];
            line += deltaLine;
            character = deltaLine == 0 ? character + data[i + 1] : data[i + 1];

            var type = data[i + 3];
            if (type >= 0 && type < tokenTypes.Count)
                tokens.Add(new LspSemanticToken(line, character, data[i + 2], tokenTypes[type], data[i + 4]));
        }
        return tokens;
    }
}
