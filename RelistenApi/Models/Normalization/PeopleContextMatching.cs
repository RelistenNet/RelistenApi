using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Relisten.Api.Models.Normalization;

/// <summary>Captured context for one witnessed actor; group IDs identify local search groups.</summary>
public sealed record PeopleContextActor(
    string GroupId,
    long CreditId,
    string? CreditKey,
    Guid SourceUuid,
    Guid ArtistUuid,
    string ArtistName,
    string Name,
    IReadOnlyList<string> Roles,
    string? LiteralDate,
    IReadOnlyList<PeopleContextExcerpt> Evidence,
    JsonElement? ActorFacts
);

public sealed record PeopleContextExcerpt(
    string Field,
    string Quote,
    int StartCodePoint,
    int EndCodePoint
);

public sealed record PeopleContextQuestion(
    string QuestionId,
    string RetrievalBasis,
    PeopleContextActor Left,
    PeopleContextActor Right
);

/// <summary>One cacheable request. Input context is untrusted evidence, never instructions.</summary>
public sealed record PeopleContextBatchInput(
    string BatchId,
    IReadOnlyList<PeopleContextQuestion> Questions
);

public sealed record PeopleContextCitation(string Side, string Field, string Quote);

/// <summary>
/// A model's contextual hypothesis. Relation is same_person, different_people or
/// insufficient_evidence. Reason is model rationale; citations alone are source evidence.
/// No decision accepts an identity or changes source ownership.
/// </summary>
public sealed record PeopleContextDecision(
    string QuestionId,
    string Relation,
    string Reason,
    IReadOnlyList<PeopleContextCitation> Evidence
);

/// <summary>Known usage survives failed validation. Unknown cost retains its ledger reservation.</summary>
public sealed record PeopleContextMatchResult(
    string BatchId,
    string State,
    IReadOnlyList<PeopleContextDecision> Decisions,
    string? CacheKey = null,
    bool FromCache = false,
    string? Reason = null,
    string? ProviderRequestId = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? ActualUsd = null
);
