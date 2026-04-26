namespace DotTrace.Core.Analysis;

public sealed record CallGraphProject(
    string StableId,
    string Name,
    string AssemblyName,
    string FilePath);
