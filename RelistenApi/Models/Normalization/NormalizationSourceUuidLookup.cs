namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Exact catalog source presence and its original UUID clock from one primary snapshot.
/// Presence alone establishes no provider, artist, file or performance association.
/// A missing source needs no invented evidence revision; its dependency can fence lookup work.
/// </summary>
public sealed record NormalizationSourceUuidLookup(
    bool Exists,
    NormalizationSourceUuidDependency Dependency
);
