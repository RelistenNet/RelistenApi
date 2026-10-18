using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Provider assertions retained by an accepted structure decision. Traversal order and source
/// indexes do not establish musical order. Each group belongs to one immutable structure revision.
/// </summary>
public sealed record NormalizedSetlistAnnotationGroup
{
    public long index { get; init; }
    public Guid edition_uuid { get; init; }
    public Guid structure_revision_uuid { get; init; }
    public long revision { get; init; }
    public string basis { get; init; } = "";
    public Guid? decision_uuid { get; init; }
    public int source_index { get; init; }
    public Guid proposal_attempt_uuid { get; init; }
    public Guid input_revision_uuid { get; init; }
    public int account_index { get; init; }
    public string scope { get; init; } = "";
    public int? source_set_index { get; init; }
    public Guid? source_passage_uuid { get; init; }
    public string correspondence { get; init; } = "unmapped";
    public IReadOnlyList<Guid>? set_uuids { get; init; }
    public IReadOnlyList<Guid>? occurrence_uuids { get; init; }
    public long target_count { get; init; }
    public string detail_status { get; init; } = "available";
    public NormalizedAnnotationOrigin? origin { get; init; }
    public NormalizedAnnotationText? passage_title { get; init; }
    public IReadOnlyList<NormalizedAnnotationText>? notes { get; init; }
    public IReadOnlyList<NormalizedFootnoteDefinition>? footnotes { get; init; }
    public IReadOnlyList<NormalizedFootnoteReference>? footnote_references { get; init; }
    public IReadOnlyList<NormalizedPerformerClaim>? performers { get; init; }
    public NormalizedAnnotationBoolean? prerecorded_playback { get; init; }
    public NormalizedAnnotationBoolean? segue { get; init; }
    public NormalizedAnnotationText? transition_marker { get; init; }
}

public sealed record NormalizedAnnotationOrigin(
    NormalizationResourceKey identity,
    string observation_scope,
    NormalizationPublicEvidence evidence
);

/// <summary>
/// Text is display prose only when detail_status is available. Evidence always retains original
/// support, including markup; derived display text does not pretend to be an exact source quote.
/// </summary>
public sealed record NormalizedAnnotationText(
    string kind,
    string detail_status,
    string? text,
    NormalizationPublicEvidence evidence
);

public sealed record NormalizedAnnotationBoolean(bool value, NormalizationPublicEvidence evidence);

public sealed record NormalizedFootnoteDefinition(
    int index,
    int? number,
    NormalizationPublicEvidence? number_evidence,
    NormalizedAnnotationText note
);

/// <summary>Definition indexes refer only to this source account's definitions, never another provider's notes.</summary>
public sealed record NormalizedFootnoteReference(
    int number,
    string resolution,
    IReadOnlyList<int> definition_indexes,
    NormalizationPublicEvidence evidence
);

public sealed record NormalizedPerformerClaim(
    string relationship,
    NormalizedAnnotationText? name,
    IReadOnlyList<NormalizedAnnotationIdentity> identities
);

public sealed record NormalizedAnnotationIdentity(
    NormalizationResourceKey identity,
    NormalizationPublicEvidence evidence
);
