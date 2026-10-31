namespace Relisten.Catalog.Compatibility;

/// <summary>
/// Temporary rollout switch. Enable only after Postgres popularity populations
/// are populated and checked; disable to restore legacy recording/Redis reads.
/// </summary>
public sealed class LegacyCatalogOptions
{
    public bool LegacyRecordingAdaptersEnabled { get; set; }
}
