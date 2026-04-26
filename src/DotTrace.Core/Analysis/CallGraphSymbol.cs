namespace DotTrace.Core.Analysis;

public sealed record CallGraphSymbol(
    string StableId,
    string? ProjectStableId,
    string QualifiedName,
    string SignatureText,
    string NormalizedQualifiedName,
    string NormalizedSignatureText,
    SymbolOriginKind OriginKind,
    SourceLocationInfo? Location);
