namespace DotTrace.Core.Analysis;

public sealed record CallGraphRootSymbol(
    string ProjectStableId,
    string SymbolStableId,
    RootSymbolKind Kind,
    string? MetadataJson);
