namespace DotTrace.Core.Analysis;

public enum CallTreeNodeKind
{
    Source,
    External,
    Cycle,
    Repeated,
    Truncated,
    Unresolved
}

