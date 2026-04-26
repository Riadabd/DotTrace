namespace DotTrace.Core.Analysis;

public sealed record CallGraphCall(
    string CallerStableId,
    string? CalleeStableId,
    string CallText,
    SourceLocationInfo? Location,
    int Ordinal);
