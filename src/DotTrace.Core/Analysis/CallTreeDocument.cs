namespace DotTrace.Core.Analysis;

public sealed record CallTreeDocument(
    CallTreeNode SelectedRoot,
    CallTreeNode CallersTree,
    CallTreeNode CalleesTree);
