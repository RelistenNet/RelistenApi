using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// A complete accepted image for one edition. UUIDs are allocated before submission and reused
/// on retry. Every formerly active set/passage is retained or explicitly supplied as retracted.
/// The source accounts remain distinct assertions, including contradictory or unresolved passages.
/// </summary>
public sealed record SetlistStructureChange : MusicChange
{
    public SetlistEditionChange Edition { get; init; } = new();
    public IReadOnlyList<SetlistStructureSet> Sets { get; init; } = [];
    public IReadOnlyList<SongOccurrenceChange> Occurrences { get; init; } = [];
    public IReadOnlyList<SetlistStructureSource> Sources { get; init; } = [];
    public IReadOnlyList<SetlistSetCorrespondence> SetCorrespondence { get; init; } = [];
    public IReadOnlyList<SetlistPassageCorrespondence> PassageCorrespondence { get; init; } = [];
    public IReadOnlyList<SetlistUnresolvedPassage> UnresolvedPassages { get; init; } = [];
}

/// <summary>A set has identity independently of label and position, including empty or repeated sets.</summary>
public sealed record SetlistStructureSet
{
    public Guid Uuid { get; init; }
    public int? Position { get; init; }
    public string? Label { get; init; }
    public int? EncoreNumber { get; init; }
    public NormalizationResourceKey? ProviderIdentity { get; init; }
    public bool Retracted { get; init; }
    public IReadOnlyList<NormalizedEvidenceReference> Evidence { get; init; } = [];
}

/// <summary>
/// Pins one exact published proposal account. Account is checked against that immutable batch;
/// its complete typed sets, tape flags, footnotes, annotations and performer claims are preserved.
/// AccountIndex addresses the input's account observations, not a canonical performance order.
/// </summary>
public sealed record SetlistStructureSource(
    Guid ProposalAttemptUuid,
    Guid InputRevisionUuid,
    int AccountIndex,
    NormalizationSetlistAccountProposal Account
);

public sealed record SetlistSetCorrespondence(int SourceIndex, int SetIndex, Guid SetUuid);

/// <summary>Explicit component/split mapping may name several occurrences; no title/order identity inference occurs.</summary>
public sealed record SetlistPassageCorrespondence(
    int SourceIndex,
    Guid ProposalUuid,
    IReadOnlyList<Guid> OccurrenceUuids
);

/// <summary>A retained source passage may remain unmatched without creating a canonical occurrence.</summary>
public sealed record SetlistUnresolvedPassage(int SourceIndex, Guid ProposalUuid, string Reason);

/// <summary>
/// Original proposal read sets for work admission. These are saved publication stamps, not
/// current owner clocks. Admission must use InputRevisions; publication also verifies the
/// original resource generations and proposal publication receipts without refreshing them.
/// </summary>
public sealed record SetlistStructureReadSet(
    IReadOnlyList<NormalizationBindingInputReference> InputRevisions,
    IReadOnlyList<NormalizationInputDependency> Inputs,
    IReadOnlyList<NormalizationSubjectDependency> ProposalPublications,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
    NormalizationMappingRegistryDependency? MappingRegistry
);
