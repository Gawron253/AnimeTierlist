using System.Collections.Immutable;

namespace AnimeTierlist.Infrastructure.Exports;

/// <summary>An export that produced no final output, with the affected shows when covers block it.</summary>
public sealed class ExportException : Exception
{
    public ExportException()
    {
        Affected = [];
    }

    public ExportException(string message)
        : base(message)
    {
        Affected = [];
    }

    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
        Affected = [];
    }

    public ExportException(string message, ImmutableArray<string> affected)
        : base(message)
    {
        Affected = affected;
    }

    /// <summary>Titles of in-scope shows whose covers are missing or invalid.</summary>
    public ImmutableArray<string> Affected { get; }
}
