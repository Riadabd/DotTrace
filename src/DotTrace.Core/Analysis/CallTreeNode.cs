namespace DotTrace.Core.Analysis;

public sealed record CallTreeNode(
    string Id,
    string DisplayText,
    CallTreeNodeKind Kind,
    SourceLocationInfo? Location,
    IReadOnlyList<CallTreeNode> Children);

