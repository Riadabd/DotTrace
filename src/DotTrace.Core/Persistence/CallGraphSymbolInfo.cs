using DotTrace.Core.Analysis;

namespace DotTrace.Core.Persistence;

public sealed record CallGraphSymbolInfo(
    long Id,
    long SnapshotId,
    string QualifiedName,
    string SignatureText,
    SymbolOriginKind OriginKind,
    string? ProjectName,
    string? ProjectAssemblyName,
    string? ProjectFilePath,
    SourceLocationInfo? Location,
    long DirectCallerCount,
    long DirectCalleeCount);
