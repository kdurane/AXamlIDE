namespace AXamlIDE.Services.Lsp;

/// <summary>LSP <c>DiagnosticSeverity</c> values.</summary>
public static class LspDiagnosticSeverity
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Information = 3;
    public const int Hint = 4;
}

/// <summary>LSP <c>DiagnosticTag</c> values: how the editor should draw a diagnostic's range.</summary>
public static class LspDiagnosticTag
{
    /// <summary>Code that isn't needed (an unused using, an unused private member): drawn faded.</summary>
    public const int Unnecessary = 1;
}

/// <summary>The LSP <c>SymbolKind</c> values the IDE cares about.</summary>
public static class LspSymbolKind
{
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Class = 5;
    public const int Method = 6;
    public const int Property = 7;
    public const int Field = 8;
    public const int Constructor = 9;
    public const int Enum = 10;
    public const int Interface = 11;
    public const int Function = 12;
    public const int Struct = 23;
    public const int Event = 24;
    public const int TypeParameter = 26;
}
