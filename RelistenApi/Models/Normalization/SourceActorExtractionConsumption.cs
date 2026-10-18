using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Source-actor view of an immutable recording producer. It retains complete original groups,
/// preparation and ticket, but excludes credit/title proposals that this domain does not consume.
/// This is not the full durable/public recording batch; that batch remains unchanged.
/// </summary>
public sealed record SourceActorExtractionConsumption(
    Guid AttemptUuid,
    Guid SourceUuid,
    Guid ArtistUuid,
    string ProcessingVersion,
    NormalizationRecordingPreparation Preparation,
    NormalizationWorkTicket Ticket,
    SourceActorExtractionOutput Extraction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<SourceActorExtractionInput>? AdditionalExtractions = null
);

/// <summary>Complete role-free output for one original input, including absence and decoder issues.</summary>
public sealed record SourceActorExtractionOutput(
    IReadOnlyList<ProviderDecodeIssue> DecodeIssues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationRecordingSourceActorAliasScope>? SourceActorAliases = null
);

public sealed record SourceActorExtractionInput(
    Guid InputRevisionUuid,
    SourceActorExtractionOutput Extraction
);
