namespace DotTrace.Core.Analysis;

public sealed class AnalysisOptions
{
    public AnalysisOptions(int? maxDepth = null)
    {
        if (maxDepth is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be greater than zero.");
        }

        MaxDepth = maxDepth;
    }

    public int? MaxDepth { get; }
}

