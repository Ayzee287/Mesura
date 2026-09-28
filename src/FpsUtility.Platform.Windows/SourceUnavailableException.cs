using FpsUtility.Core.Raw;

namespace FpsUtility.Platform.Windows;

/// <summary>
/// A collector's way of saying its whole source is unavailable, and why. The snapshot collector turns
/// it into an <c>unavailable</c> source entry; it never escapes a scan (architecture §10).
/// </summary>
internal sealed class SourceUnavailableException : Exception
{
    public SourceUnavailableException(MissingReason reason, string detail)
        : base(detail)
    {
        Reason = reason;
    }

    public MissingReason Reason { get; }
}
