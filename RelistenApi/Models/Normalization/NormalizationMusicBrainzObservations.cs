using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// An external entity, never a catalog artist, source, repertoire or canonical person UUID.
/// Name uses the provider's name/title field and preserves that field's exact citation.
/// </summary>
public sealed record MusicBrainzEntityReference
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedProviderIdentity Identity { get; init; } = null!;
    public ObservedValue<string>? Name { get; init; }
    public ObservedValue<string>? SortName { get; init; }
    public ObservedValue<string>? Type { get; init; }
    public ObservedProviderIdentity? TypeIdentity { get; init; }
    public ObservedValue<string>? Disambiguation { get; init; }
}

public sealed record MusicBrainzAliasObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedValue<string>? Name { get; init; }
    public ObservedValue<string>? SortName { get; init; }
    public ObservedValue<string>? Locale { get; init; }
    public ObservedValue<string>? Type { get; init; }
    public ObservedValue<bool>? Primary { get; init; }
}

/// <summary>
/// Original relationship kind and direction are assertions, not inferred author roles.
/// Unknown target kinds retain their relationship-object citation for exact rereading.
/// Null lists mean absent or invalid containers. Member diagnostics distinguish an explicitly
/// empty array from a supplied array whose members could not be decoded.
/// </summary>
public sealed record MusicBrainzRelationshipObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedValue<string>? Type { get; init; }
    public ObservedProviderIdentity? TypeIdentity { get; init; }
    public ObservedValue<string>? Direction { get; init; }
    public ObservedValue<string>? TargetType { get; init; }
    public MusicBrainzEntityReference? Target { get; init; }
    public ObservedValue<string>? SourceCredit { get; init; }
    public ObservedValue<string>? TargetCredit { get; init; }
    public ObservedValue<string>? Begin { get; init; }
    public ObservedValue<string>? End { get; init; }
    public ObservedValue<bool>? Ended { get; init; }
    public IReadOnlyList<ObservedValue<string>>? Attributes { get; init; }
    public IReadOnlyDictionary<string, ObservedProviderIdentity>? AttributeIdentities { get; init; }
    public IReadOnlyDictionary<string, ObservedValue<string>>? AttributeValues { get; init; }
}

/// <summary>Registry-specific work attributes retain their type namespace; no provider is guessed.</summary>
public sealed record MusicBrainzWorkAttributeObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedValue<string>? Type { get; init; }
    public ObservedProviderIdentity? TypeIdentity { get; init; }
    public ObservedValue<string>? Value { get; init; }
}

/// <summary>A validated compact comparison key and the original provider spelling at the same citation.</summary>
public sealed record MusicBrainzIswcObservation(
    ObservedValue<string> Original,
    ObservedProviderIdentity Identity
);

public sealed record MusicBrainzWorkObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public MusicBrainzEntityReference Work { get; init; } = null!;
    public IReadOnlyList<MusicBrainzIswcObservation>? Iswcs { get; init; }
    public IReadOnlyList<MusicBrainzAliasObservation>? Aliases { get; init; }
    public IReadOnlyList<MusicBrainzWorkAttributeObservation>? Attributes { get; init; }
    public IReadOnlyList<MusicBrainzRelationshipObservation>? Relationships { get; init; }

    /// <summary>The lookup profile supplies no assertion that the returned author list is exhaustive.</summary>
    public bool? CompleteAuthorship => null;
}

public sealed record MusicBrainzArtistCreditObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedValue<string>? Name { get; init; }
    public ObservedValue<string>? JoinPhrase { get; init; }
    public MusicBrainzEntityReference? Artist { get; init; }
}

/// <summary>External recording relationships do not assign a work to any Relisten recording or song.</summary>
public sealed record MusicBrainzRecordingObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public MusicBrainzEntityReference Recording { get; init; } = null!;
    public IReadOnlyList<MusicBrainzArtistCreditObservation>? ArtistCredits { get; init; }
    public IReadOnlyList<MusicBrainzRelationshipObservation>? Relationships { get; init; }
}

/// <summary>
/// MusicBrainz artist type retains Person, Group and any other provider wording independently
/// of Relisten's catalog artists and normalized people. No local identity is allocated here.
/// </summary>
public sealed record MusicBrainzArtistObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public MusicBrainzEntityReference Artist { get; init; } = null!;
    public IReadOnlyList<MusicBrainzAliasObservation>? Aliases { get; init; }
}
