using Microsoft.Extensions.Logging;

namespace Relisten.Import;

/// <summary>Stable diagnostic events for upstream selection and metadata eligibility.</summary>
internal static class ImportEvents
{
    internal static readonly EventId ItemCompleted = new(2102, nameof(ItemCompleted));
    internal static readonly EventId ArchiveDateRemapped = new(2103, nameof(ArchiveDateRemapped));
    internal static readonly EventId ArchiveDeletionGuard = new(2104, nameof(ArchiveDeletionGuard));
    internal static readonly EventId ArchiveItemSelected = new(2100, nameof(ArchiveItemSelected));
    internal static readonly EventId ArchiveItemSkipped = new(2101, nameof(ArchiveItemSkipped));
}
