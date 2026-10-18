using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task RequireRepertoireSourceCurrent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionReadSet read,
        CancellationToken ct
    )
    {
        // These are producer observations, including absence. Re-reading current values and
        // replacing them here would authenticate different bytes under an old extraction.
        var current = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT true
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@inputs::jsonb) AS i("ResourceUuid" uuid,"RevisionUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.input_resources p ON p.uuid=i."ResourceUuid"
                        WHERE p.uuid IS NULL OR p.current_revision_uuid IS DISTINCT FROM i."RevisionUuid" OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@catalog::jsonb) AS i("ArtistUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.catalog_artist_revisions p ON p.artist_uuid=i."ArtistUuid"
                        WHERE p.artist_uuid IS NULL OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@identities::jsonb) AS i("UpstreamIdentifier" text,"Generation" bigint)
                        LEFT JOIN normalization.catalog_source_identity_revisions p ON p.upstream_identifier=i."UpstreamIdentifier"
                        WHERE p.upstream_identifier IS NULL OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@sourceClocks::jsonb) AS i("SourceUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.catalog_source_uuid_revisions p ON p.source_uuid=i."SourceUuid"
                        WHERE p.source_uuid IS NULL OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@venues::jsonb) AS i("VenueUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.catalog_venue_revisions p ON p.venue_uuid=i."VenueUuid"
                        WHERE p.venue_uuid IS NULL OR p.generation<>i."Generation")
                    AND (@mappingGeneration::bigint IS NULL OR EXISTS(SELECT 1 FROM normalization.catalog_mapping_registry_revision
                        WHERE singleton AND generation=@mappingGeneration))
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@dependencies::jsonb) AS i("SubjectUuid" uuid,"DesiredGeneration" bigint,"EditorialRevision" bigint,"PublishedWorkUuid" uuid)
                        LEFT JOIN normalization.work_subjects s ON s.uuid=i."SubjectUuid"
                        WHERE s.uuid IS NULL OR s.desired_generation<>i."DesiredGeneration" OR s.editorial_revision<>i."EditorialRevision"
                            OR s.published_work_uuid IS DISTINCT FROM i."PublishedWorkUuid")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@observations::jsonb) AS i("SubjectUuid" uuid,"DesiredGeneration" bigint,"EditorialRevision" bigint,"PublishedWorkUuid" uuid)
                        LEFT JOIN normalization.work_subjects s ON s.uuid=i."SubjectUuid"
                        WHERE s.uuid IS NULL OR s.desired_generation<>i."DesiredGeneration" OR s.editorial_revision<>i."EditorialRevision"
                            OR s.published_work_uuid IS DISTINCT FROM i."PublishedWorkUuid")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_array_elements(@universes::jsonb) i
                        LEFT JOIN normalization.candidate_universe_revisions p ON p.domain=i->'Universe'->>'Domain'
                            AND p.scope_uuid=(i->'Universe'->>'ScopeUuid')::uuid
                        WHERE (p.domain IS NULL AND i->'Universe'->>'Domain'<>'repertoire')
                            OR COALESCE(p.generation,0)<>(i->>'Generation')::bigint)
                """,
                new
                {
                    inputs = JsonSerializer.Serialize(read.Inputs),
                    catalog = JsonSerializer.Serialize(read.CatalogInputs),
                    identities = JsonSerializer.Serialize(read.SourceIdentities),
                    sourceClocks = JsonSerializer.Serialize(read.SourceUuids),
                    venues = JsonSerializer.Serialize(read.ImportedVenues),
                    mappingGeneration = read.MappingRegistry?.Generation,
                    dependencies = JsonSerializer.Serialize(read.Dependencies),
                    observations = JsonSerializer.Serialize(read.OwnerObservations),
                    universes = JsonSerializer.Serialize(read.CandidateUniverses),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!current)
        {
            throw new NormalizationConflictException(
                "The original repertoire source observations changed."
            );
        }
    }

    private static async Task<(
        RepertoireResolutionOwnerContext Observation,
        RepertoireResolutionOwnerContext? Artist
    )> ReadRepertoireOwners(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid registration,
        Guid? artist,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        async Task<RepertoireResolutionOwnerContext> Read(NormalizationSubjectKey key)
        {
            var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
                c,
                t,
                key,
                new(1000, 100000),
                ct
            );
            var bindings = EmptyRepertoireOwnerBindings();
            if (membership.Owner is { } owner)
            {
                context.AddOwner(owner.SubjectUuid);
                bindings = await ReadRepertoireOwnerBindings(c, t, owner.SubjectUuid, context, ct);
            }
            return new(membership, bindings.ReadSet, bindings.InputReferences);
        }
        var observation = await Read(new("songs", "repertoire_observation", registration));
        var artistOwner = artist is { } uuid
            ? await Read(new("songs", "artist_repertoire", uuid))
            : null;
        if (artist is { } artistUuid)
        {
            context.AddArtist(artistUuid);
        }
        return (observation, artistOwner);
    }

    private static bool RepertoireFreshnessComplete(
        IEnumerable<CandidateFactsIssue> issues,
        RepertoireResolutionSourceSnapshot? source = null
    ) =>
        source?.Issues.Contains("provider_artist_clock_missing") != true
        && !issues.Any(issue =>
            issue.Kind
                is CandidateFactsIssueKind.MissingOwner
                    or CandidateFactsIssueKind.MissingCatalogClock
            || issue.FactKind is "owner_binding_stamp_missing" or "owner_input_head_missing"
        );

    private static RepertoireResolutionReadSet MergeRepertoireContextReadSet(
        RepertoireResolutionReadSet original,
        IEnumerable<CandidateFactsReadSet> facts,
        NormalizationSubjectObservation policy
    )
    {
        var all = facts.ToArray();
        static T[] Coherent<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
            where TKey : notnull =>
            values
                .GroupBy(key)
                .Select(group =>
                    group.Distinct().Count() == 1
                        ? group.First()
                        : throw new NormalizationConflictException(
                            "Conflicting original and current repertoire observations."
                        )
                )
                .ToArray();
        var observations = Coherent(
            original
                .OwnerObservations.Concat(all.SelectMany(read => read.OwnerObservations))
                .Append(policy),
            value => value.SubjectUuid
        );
        foreach (var dependency in original.Dependencies)
        {
            if (
                observations.Any(value =>
                    value.SubjectUuid == dependency.SubjectUuid
                    && (
                        value.DesiredGeneration != dependency.DesiredGeneration
                        || value.EditorialRevision != dependency.EditorialRevision
                        || value.PublishedWorkUuid != dependency.PublishedWorkUuid
                    )
                )
            )
            {
                throw new NormalizationConflictException(
                    "The published source owner changed during repertoire resolution."
                );
            }
        }
        return original with
        {
            CatalogInputs = Coherent(
                original.CatalogInputs.Concat(all.SelectMany(read => read.CatalogInputs)),
                value => value.ArtistUuid
            ),
            CandidateUniverses = Coherent(
                original.CandidateUniverses.Concat(all.SelectMany(read => read.CandidateUniverses)),
                value => value.Universe
            ),
            OwnerObservations = observations,
        };
    }
}
