using System.Collections.Immutable;

namespace DotTrace.Core.Analysis;

public sealed record AnalysisResult(CallTreeNode Root, ImmutableArray<string> Diagnostics);

