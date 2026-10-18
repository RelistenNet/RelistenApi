using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class RetainedRecordingCreditExtractor
{
    private const string ActorMappingVersion = "retained-actor-facts-v1";

    /// <summary>
    /// Local grammar uses coordinates in one original named field. Retained facts cite that
    /// field's actual JSON/HTML location, and namespace every local ID so identical array
    /// entries or Local root accounts cannot accidentally become the same source actor.
    /// </summary>
    private static CreditedActorFacts<NormalizedEvidenceReference> MapActorFacts(
        NormalizedEvidenceReference field,
        CreditedActorFacts<NormalizationTextEvidence> facts,
        NormalizationRetainedDocument document
    )
    {
        Guid Scope(Guid uuid) =>
            new(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{ActorMappingVersion}/{field.InputRevisionUuid:D}/{field.JsonPointer}/{field.StartCodePoint ?? 0}/{uuid:D}"
                    )
                )[..16]
            );

        NormalizedEvidenceReference Map(NormalizationTextEvidence citation)
        {
            if (
                citation.Field != facts.Anchor.Field
                || citation.JsonPointer != facts.Anchor.JsonPointer
            )
            {
                throw new ArgumentException("Actor facts must cite their original credited field.");
            }
            var mapped = Remap(field, citation);
            document.ValidateEvidence(mapped);
            return mapped;
        }

        return new(
            facts.Version,
            Scope(facts.ActorUuid),
            Scope(facts.ReferenceUuid),
            Map(facts.Anchor),
            facts
                .Aliases.Select(alias => new ActorAliasFact<NormalizedEvidenceReference>(
                    Scope(alias.Uuid),
                    Scope(alias.ReferenceUuid),
                    alias.Wording,
                    alias.Evidence.Select(Map).ToArray()
                ))
                .ToArray(),
            facts
                .Profiles.Select(profile => new ActorProfileFact<NormalizedEvidenceReference>(
                    Scope(profile.Uuid),
                    profile.Literal,
                    profile.Provider,
                    profile.Namespace,
                    profile.LookupKey,
                    profile.Relationship,
                    profile.Evidence.Select(Map).ToArray()
                ))
                .ToArray(),
            facts
                .Contacts.Select(contact => new ActorContactFact<NormalizedEvidenceReference>(
                    Scope(contact.Uuid),
                    contact.Literal,
                    contact.ContactType,
                    contact.LookupKey,
                    contact.Evidence.Select(Map).ToArray()
                ))
                .ToArray(),
            facts
                .Context.Select(context => new ActorContextFact<NormalizedEvidenceReference>(
                    Scope(context.Uuid),
                    context.ContextKind,
                    context.Evidence.Select(Map).ToArray()
                ))
                .ToArray(),
            facts.Kind == null
                ? null
                : new(facts.Kind.Kind, facts.Kind.Evidence.Select(Map).ToArray()),
            facts
                .Relationships.Select(
                    relation => new ActorCoReferenceFact<NormalizedEvidenceReference>(
                        Scope(relation.Uuid),
                        relation.Relationship,
                        relation.ReferenceUuids.Select(Scope).ToArray(),
                        relation.Roles,
                        relation.Evidence.Select(Map).ToArray()
                    )
                )
                .ToArray(),
            facts
                .Coverage.Select(coverage => new ActorEvidenceCoverage<NormalizedEvidenceReference>(
                    coverage.Kind,
                    coverage.Complete,
                    coverage.Reason,
                    coverage.Fields.Select(Map).ToArray()
                ))
                .ToArray(),
            facts
                .Unresolved.Select(
                    issue => new ActorEvidenceUnresolved<NormalizedEvidenceReference>(
                        issue.Reason,
                        issue.CandidateReferenceUuids.Select(Scope).ToArray(),
                        issue.Evidence.Select(Map).ToArray()
                    )
                )
                .ToArray()
        );
    }
}
