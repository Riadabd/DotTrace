using System.Collections.Immutable;

namespace DotTrace.Core.Analysis;

public sealed record CallGraphBuildResult(
    string InputPath,
    string WorkspaceFingerprint,
    string ToolVersion,
    IReadOnlyList<CallGraphProject> Projects,
    IReadOnlyList<CallGraphSymbol> Symbols,
    IReadOnlyList<CallGraphCall> Calls,
    ImmutableArray<string> Diagnostics);
