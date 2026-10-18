using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>A provider assertion and its exact retained support; acceptance is a separate decision.</summary>
public sealed record ObservedValue<T>(T Value, NormalizedEvidenceReference Evidence);

public sealed record ObservedProviderIdentity(
    NormalizationResourceKey Identity,
    NormalizedEvidenceReference Evidence
);

/// <summary>
/// A page member identifies where an assertion was observed. It does not establish the
/// latest state of an item that may occur on other pages. Legacy snapshots retain stored
/// catalog fields and cannot recover discarded provider data.
/// </summary>
public enum ProviderObservationScope
{
    ResourceItem,
    CatalogMember,
    PageMember,
    LegacySnapshot,
}

public sealed record ProviderObservationOrigin(
    NormalizationResourceKey Item,
    NormalizedEvidenceReference Evidence,
    ProviderObservationScope Scope
);

public sealed record ProviderDecodeIssue(string Code, NormalizedEvidenceReference Evidence);

public sealed record ProviderPerformerObservation
{
    public ObservedValue<string>? Name { get; init; }
    public IReadOnlyList<ObservedProviderIdentity> Identities { get; init; } = [];
}

public enum ProviderTextFormat
{
    PlainText,
    SourceMarkup,
}

/// <summary>
/// Original prose with its semantic field. Source markup retains exact raw support; PlainText
/// is a separate display derivation and stays absent when that derivation is unavailable.
/// </summary>
public sealed record ProviderTextObservation(
    string Field,
    ObservedValue<string> Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PlainText = null,
    ProviderTextFormat Format = ProviderTextFormat.PlainText
);

/// <summary>An explicit footnote definition. Missing numbers remain unknown, including unnumbered HTML notes.</summary>
public sealed record ProviderFootnoteDefinition(
    ObservedValue<int>? Number,
    ProviderTextObservation Text
);

/// <summary>A performance attribution, such as a cover performer or guest; not a work author.</summary>
public sealed record ProviderPerformanceAnnotation(
    string Kind,
    ObservedValue<string>? Name,
    IReadOnlyList<ObservedProviderIdentity> Identities
);

/// <summary>
/// City coordinates must not become a venue pin. Unknown is used unless the provider
/// representation explicitly distinguishes the coordinate's subject.
/// </summary>
public enum ObservedCoordinateKind
{
    Unknown,
    Venue,
    City,
}

public sealed record ProviderVenueObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public IReadOnlyList<ObservedValue<string>> Names { get; init; } = [];
    public IReadOnlyList<ObservedProviderIdentity> Identities { get; init; } = [];
    public ObservedValue<string>? OriginalLocality { get; init; }
    public ObservedValue<string>? Country { get; init; }
    public ObservedValue<string>? Region { get; init; }
    public ObservedValue<string>? City { get; init; }
    public ObservedValue<string>? Address { get; init; }
    public ObservedValue<double>? Latitude { get; init; }
    public ObservedValue<double>? Longitude { get; init; }
    public ObservedCoordinateKind CoordinateKind { get; init; }
    public IReadOnlyList<ProviderTextObservation> Text { get; init; } = [];
}

/// <summary>Explicit work-credit wording. Cover performers and guests are separate annotations.</summary>
public sealed record ProviderWorkCreditObservation
{
    public string Role { get; init; } = "writer";
    public ObservedValue<string> Name { get; init; } = null!;
    public IReadOnlyList<ObservedProviderIdentity> Identities { get; init; } = [];
}

public sealed record ProviderRepertoireObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;

    /// <summary>A stored identity can exist before its title is known; slugs are not substitute titles.</summary>
    public ObservedValue<string>? Title { get; init; }
    public IReadOnlyList<ObservedProviderIdentity> Identities { get; init; } = [];
    public IReadOnlyList<ObservedValue<string>> Aliases { get; init; } = [];
    public ObservedProviderIdentity? AliasOf { get; init; }
    public IReadOnlyList<ObservedProviderIdentity> WorkIdentities { get; init; } = [];
    public IReadOnlyList<ProviderWorkCreditObservation> WorkCredits { get; init; } = [];
    public bool CompleteAuthorship { get; init; }

    /// <summary>Original catalog prose that does not establish aliases or provider identities.</summary>
    public IReadOnlyList<ProviderTextObservation> Text { get; init; } = [];
}

/// <summary>
/// Provider file identity and raw playback locator, without a catalog track assignment.
/// Song IDs describe supplied associations; their array order does not assert passage order.
/// </summary>
public sealed record ProviderFileObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public IReadOnlyList<ObservedProviderIdentity> Identities { get; init; } = [];

    /// <summary>An explicit provider recording owner, retained separately from the enclosing response.</summary>
    public ObservedProviderIdentity? RecordingIdentity { get; init; }
    public ObservedValue<string>? Filename { get; init; }
    public ObservedValue<string>? OriginalFilename { get; init; }
    public ObservedValue<string>? Title { get; init; }
    public ObservedValue<string>? PlaybackLocator { get; init; }
    public ObservedValue<int>? Position { get; init; }
    public ObservedValue<string>? SetLabel { get; init; }
    public IReadOnlyList<ObservedProviderIdentity> SongIdentities { get; init; } = [];
    public IReadOnlyList<ProviderWorkCreditObservation> WorkCredits { get; init; } = [];
    public IReadOnlyList<ProviderTextObservation> Text { get; init; } = [];
}

public sealed record ProviderRecordingObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public IReadOnlyList<ProviderPerformerObservation> Performers { get; init; } = [];
    public ObservedValue<string>? LiteralDate { get; init; }
    public ProviderVenueObservation? Venue { get; init; }
    public IReadOnlyList<ProviderTextObservation> Text { get; init; } = [];
    public IReadOnlyList<ProviderFileObservation> Files { get; init; } = [];
}

/// <summary>
/// An entry in the provider's account. Ordinal and title never become a canonical occurrence
/// UUID. A song identity can recur; a provider occurrence key is populated only when supplied.
/// </summary>
public sealed record ProviderPassageObservation
{
    public int? Ordinal { get; init; }
    public int? SetOrdinal { get; init; }
    public ObservedProviderIdentity? OccurrenceIdentity { get; init; }
    public ObservedValue<string> Title { get; init; } = null!;
    public IReadOnlyList<ObservedProviderIdentity> SongIdentities { get; init; } = [];
    public ObservedValue<string>? SetLabel { get; init; }
    public ObservedValue<string>? TransitionMarker { get; init; }
    public ObservedValue<bool>? Segue { get; init; }

    /// <summary>Provider-reported prerecorded playback, distinct from a live song performance.</summary>
    public ObservedValue<bool>? Tape { get; init; }
    public IReadOnlyList<ObservedValue<int>> FootnoteNumbers { get; init; } = [];
    public IReadOnlyList<ProviderTextObservation> Annotations { get; init; } = [];
    public IReadOnlyList<ProviderPerformanceAnnotation> PerformerAnnotations { get; init; } = [];
}

/// <summary>A supplied set boundary, including an empty or unnamed set.</summary>
public sealed record ProviderSetObservation
{
    public int Ordinal { get; init; }
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedValue<string>? Label { get; init; }
    public ObservedValue<int>? EncoreNumber { get; init; }
    public ObservedProviderIdentity? Identity { get; init; }

    /// <summary>Null means absent or uncaptured; an empty list requires an explicitly empty notes field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProviderTextObservation>? Annotations { get; init; }
}

public sealed record ProviderSetlistObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public IReadOnlyList<ProviderPerformerObservation> Performers { get; init; } = [];
    public ObservedValue<string>? LiteralDate { get; init; }
    public ProviderVenueObservation? Venue { get; init; }
    public bool Ordered { get; init; }
    public IReadOnlyList<ProviderSetObservation> Sets { get; init; } = [];
    public IReadOnlyList<ProviderPassageObservation> Passages { get; init; } = [];
    public IReadOnlyList<ProviderTextObservation> Text { get; init; } = [];
    public IReadOnlyList<ProviderFootnoteDefinition> Footnotes { get; init; } = [];
}

/// <summary>
/// Pure decoding result. It contains assertions, not canonical identities, accepted matches,
/// catalog bindings or an assurance that page members are current entity facts.
/// </summary>
public sealed record ProviderDecodeResult
{
    public Guid InputRevisionUuid { get; init; }
    public NormalizationResourceKey Resource { get; init; } = null!;
    public string DecoderVersion { get; init; } = "";
    public IReadOnlyList<ProviderRecordingObservation> Recordings { get; init; } = [];
    public IReadOnlyList<ProviderVenueObservation> Venues { get; init; } = [];
    public IReadOnlyList<ProviderRepertoireObservation> Repertoire { get; init; } = [];
    public IReadOnlyList<ProviderSetlistObservation> Setlists { get; init; } = [];
    public IReadOnlyList<ProviderDecodeIssue> Issues { get; init; } = [];
}
