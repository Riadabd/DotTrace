namespace DotTrace.Core.Analysis;

public sealed class DotTraceException : Exception
{
    public DotTraceException(string message)
        : base(message)
    {
    }
}

