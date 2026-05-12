namespace DotTrace.Core.Analysis;

public enum CallTreeNodeKind
{
    Group,
    Source,
    External,
    Boundary,
    Cycle,
    Repeated,
    Truncated,
    Unresolved
}
