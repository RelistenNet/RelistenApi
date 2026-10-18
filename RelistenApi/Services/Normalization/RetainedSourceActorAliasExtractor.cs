using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Maps role-free alias assertions into exact retained input coordinates. Each original
/// provider field is parsed separately, including repeated array entries. The enclosing
/// producer version namespaces local IDs; the assertion Version alone identifies its format.
/// </summary>
public sealed class RetainedSourceActorAliasExtractor
{
    private const string MappingVersion = "retained-source-actor-alias-mapping-v1";
    public const string Version =
        "retained-source-actor-aliases-v1/"
        + SourceActorAliasExtractor.Version
        + "/"
        + MappingVersion;
    private readonly SourceActorAliasExtractor extractor = new();

    /// <summary>
    /// Extracts only from the exact cited original strings. Display-only text is never an
    /// extraction input. This mapper does not infer that a missing/null provider value is a clear.
    /// </summary>
    public SourceActorAliasExtraction<NormalizedEvidenceReference> Extract(
        NormalizationInputRevision input,
        IReadOnlyList<ProviderTextObservation> fields,
        CancellationToken cancellationToken = default
    )
    {
        using var document = NormalizationRetainedDocument.Open(input, cancellationToken);
        var assertions = new List<SourceActorAliasAssertion<NormalizedEvidenceReference>>();
        var unresolved = new List<ActorEvidenceUnresolved<NormalizedEvidenceReference>>();
        var scopes = new List<SourceActorAliasScopeCoverage<NormalizedEvidenceReference>>();
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = ReadOriginalField(document, field);
            var extracted = extractor.Extract(
                new Dictionary<string, string?> { [field.Field] = original }
            );
            var mapping = new FieldMapping(document, field, original);

            cancellationToken.ThrowIfCancellationRequested();
            assertions.AddRange(extracted.Assertions.Select(mapping.MapAssertion));
            unresolved.AddRange(extracted.Unresolved.Select(mapping.MapUnresolved));
            scopes.AddRange(extracted.Scopes.Select(mapping.MapScope));
        }

        return new(assertions, unresolved, scopes);
    }

    private static string ReadOriginalField(
        NormalizationRetainedDocument document,
        ProviderTextObservation field
    )
    {
        var reference = field.Text.Evidence;
        document.ValidateEvidence(reference);
        var value = document.Resolve(reference.JsonPointer);
        var original =
            reference.Quote ?? (value.ValueKind == JsonValueKind.String ? value.GetString() : null);
        if (original == null || original != field.Text.Value)
        {
            throw new ArgumentException(
                "Source alias text must retain the exact cited original string."
            );
        }

        return original;
    }

    // One field mapping supplies both evidence coordinates and local-ID scope. Sharing the
    // same mapper across fields would incorrectly coalesce repeated provider values.
    private sealed class FieldMapping(
        NormalizationRetainedDocument document,
        ProviderTextObservation field,
        string original
    )
    {
        private readonly string localPointer =
            "/" + field.Field.Replace("~", "~0").Replace("/", "~1");
        private readonly int originalLength = original.EnumerateRunes().Count();

        public SourceActorAliasAssertion<NormalizedEvidenceReference> MapAssertion(
            SourceActorAliasAssertion<NormalizationTextEvidence> assertion
        ) =>
            new(
                assertion.Version,
                Scope(assertion.AssertionUuid),
                Scope(assertion.ActorReferenceUuid),
                assertion.FieldSha256,
                assertion.Name,
                MapEvidence(assertion.NameEvidence),
                assertion
                    .Aliases.Select(alias => new ActorAliasFact<NormalizedEvidenceReference>(
                        Scope(alias.Uuid),
                        Scope(alias.ReferenceUuid),
                        alias.Wording,
                        alias.Evidence.Select(MapEvidence).ToArray()
                    ))
                    .ToArray(),
                assertion
                    .Relationships.Select(
                        relationship => new ActorCoReferenceFact<NormalizedEvidenceReference>(
                            Scope(relationship.Uuid),
                            relationship.Relationship,
                            relationship.ReferenceUuids.Select(Scope).ToArray(),
                            relationship.Roles,
                            relationship.Evidence.Select(MapEvidence).ToArray()
                        )
                    )
                    .ToArray(),
                new(
                    MapEvidence(assertion.Context.Heading),
                    assertion.Context.Qualifiers.Select(MapEvidence).ToArray(),
                    MapEvidence(assertion.Context.Statement)
                ),
                assertion.Unresolved.Select(MapUnresolved).ToArray()
            );

        public ActorEvidenceUnresolved<NormalizedEvidenceReference> MapUnresolved(
            ActorEvidenceUnresolved<NormalizationTextEvidence> issue
        ) =>
            new(
                issue.Reason,
                issue.CandidateReferenceUuids.Select(Scope).ToArray(),
                issue.Evidence.Select(MapEvidence).ToArray()
            );

        public SourceActorAliasScopeCoverage<NormalizedEvidenceReference> MapScope(
            SourceActorAliasScopeCoverage<NormalizationTextEvidence> scope
        ) =>
            new(
                scope.Field,
                scope.Complete,
                scope.Reason,
                scope.Evidence.Select(MapEvidence).ToArray()
            );

        private NormalizedEvidenceReference MapEvidence(NormalizationTextEvidence evidence)
        {
            if (
                evidence.Field != field.Field
                || evidence.JsonPointer != localPointer
                || evidence.StartCodePoint < 0
                || evidence.EndCodePoint < evidence.StartCodePoint
                || evidence.EndCodePoint > originalLength
            )
            {
                throw new ArgumentException(
                    "Source alias evidence must stay inside its original field."
                );
            }

            var reference = field.Text.Evidence;
            var mapped = new NormalizedEvidenceReference(
                reference.InputRevisionUuid,
                reference.JsonPointer,
                (reference.StartCodePoint ?? 0) + evidence.StartCodePoint,
                (reference.StartCodePoint ?? 0) + evidence.EndCodePoint,
                evidence.Quote
            );
            document.ValidateEvidence(mapped);
            return mapped;
        }

        private Guid Scope(Guid uuid) =>
            new(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{Version}/{field.Text.Evidence.InputRevisionUuid:D}/{field.Text.Evidence.JsonPointer}/{field.Text.Evidence.StartCodePoint ?? 0}/{uuid:D}"
                    )
                )[..16]
            );
    }
}
