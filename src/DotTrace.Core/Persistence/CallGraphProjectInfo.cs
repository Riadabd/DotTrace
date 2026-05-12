namespace DotTrace.Core.Persistence;

public sealed record CallGraphProjectInfo(
    long Id,
    long SnapshotId,
    string Name,
    string AssemblyName,
    string FilePath,
    long SourceSymbolCount,
    long RootSymbolCount,
    long DirectCallCount);
