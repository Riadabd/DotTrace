namespace DotTrace.Core.Persistence;

public sealed record CallGraphSnapshotInfo(
    long Id,
    string InputPath,
    string WorkspaceFingerprint,
    string ToolVersion,
    DateTimeOffset CreatedUtc,
    bool IsActive);
