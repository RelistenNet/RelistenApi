using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationPeopleResolutionApplier
{
    /// <summary>
    /// Reads only current managed support. Exact current field evidence can authorize clearing;
    /// missing data or a parser returning zero from arbitrary prose cannot establish absence.
    /// The original context epoch/owner fences this additional projection through publication.
    /// </summary>
    private async Task<IReadOnlyList<PeopleAcceptanceScopeCoverage>> ReadRetirementCoverageAsync(
        Guid sourceUuid,
        NormalizationPeopleResolutionPreparation facts,
        IReadOnlyDictionary<Guid, PeopleAcceptanceClaimDecision> decisions,
        CancellationToken ct
    )
    {
        var prior = await ReadPriorSupportAsync(sourceUuid, facts.CreditContext, ct);
        var retained = (
            await evidence.GetInputsAsync(
                facts
                    .Inputs.SelectMany(input => input.OriginalInput.Binding.Inputs)
                    .Distinct()
                    .ToArray(),
                cancellationToken: ct
            )
        ).ToDictionary(input => input.Uuid);
        var coverage = new List<PeopleAcceptanceScopeCoverage>();
        var documents = new Dictionary<Guid, NormalizationRetainedDocument>();
        try
        {
            foreach (
                var group in prior.GroupBy(row => new
                {
                    row.Provider,
                    row.ResourceType,
                    row.ProviderKey,
                    row.Role,
                    row.TrackUuid,
                })
            )
            {
                var contributor = new NormalizationResourceKey(
                    group.Key.Provider,
                    group.Key.ResourceType,
                    group.Key.ProviderKey
                );
                var scope = new RecordingCreditCompleteScope(group.Key.Role, group.Key.TrackUuid);
                var input = facts.Inputs.SingleOrDefault(input =>
                    input.OriginalInput.Contributor == contributor
                );
                var fields = new List<NormalizedEvidenceReference>();
                var reason =
                    input == null
                        ? "missing_contributor_input"
                        : CoverageIssue(input, scope, decisions);
                if (reason == null)
                {
                    foreach (var support in group)
                    {
                        var resource = new NormalizationResourceKey(
                            support.InputProvider,
                            support.InputType,
                            support.InputKey
                        );
                        var reference = input!.OriginalInput.Binding.Inputs.SingleOrDefault(value =>
                            value.Resource == resource
                        );
                        NormalizedEvidenceReference? field = null;
                        if (reference != null)
                        {
                            if (
                                !documents.TryGetValue(
                                    reference.InputRevisionUuid,
                                    out var document
                                )
                            )
                            {
                                document = NormalizationRetainedDocument.Open(
                                    retained[reference.InputRevisionUuid],
                                    ct
                                );
                                documents.Add(reference.InputRevisionUuid, document);
                            }
                            field = KnownCurrentField(
                                document,
                                reference.InputRevisionUuid,
                                support.Pointer,
                                scope.Role,
                                input
                                    .Claims.Where(claim =>
                                        claim.ScopeTrackUuid == scope.ScopeTrackUuid
                                        && Role(claim.Proposal.Mention.Role) == scope.Role
                                        && decisions[claim.Proposal.ProposalUuid].Action
                                            == PeopleResolutionAction.MatchExisting
                                    )
                                    .Select(claim => claim.Proposal.Mention.Evidence)
                                    .ToArray()
                            );
                        }
                        if (field == null)
                        {
                            reason = "missing_or_unproven_current_field";
                            break;
                        }
                        fields.Add(field);
                    }
                }
                coverage.Add(
                    new(
                        contributor,
                        scope,
                        reason == null,
                        reason ?? "complete_current_role_fields",
                        fields.Distinct().ToArray()
                    )
                );
            }
            return coverage;
        }
        finally
        {
            foreach (var document in documents.Values)
            {
                document.Dispose();
            }
        }
    }

    private Task<PriorSupport[]> ReadPriorSupportAsync(
        Guid sourceUuid,
        RecordingCreditContext context,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var epoch = await c.QuerySingleAsync<long>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var owner = await c.QuerySingleOrDefaultAsync<NormalizationSubjectObservation>(
                new CommandDefinition(
                    "SELECT uuid AS \"SubjectUuid\",desired_generation AS \"DesiredGeneration\",editorial_revision AS \"EditorialRevision\",published_work_uuid AS \"PublishedWorkUuid\" FROM normalization.work_subjects WHERE domain='people' AND subject_kind='recording' AND subject_uuid=@sourceUuid",
                    new { sourceUuid },
                    t,
                    cancellationToken: ct
                )
            );
            if (
                epoch != context.PeopleUniverse.Generation
                || owner != context.RecordingOwnerObservation
            )
            {
                throw new NormalizationConflictException(
                    "Contributor support changed after the accepted-credit context snapshot."
                );
            }
            var rows = (
                await c.QueryAsync<PriorSupport>(
                    new CommandDefinition(
                        """
                        SELECT DISTINCT s.contributor_provider AS "Provider",s.contributor_type AS "ResourceType",s.contributor_key AS "ProviderKey",
                            c.role AS "Role",c.scope_track_uuid AS "TrackUuid",p.provider AS "InputProvider",p.resource_type AS "InputType",p.provider_key AS "InputKey",s.json_pointer AS "Pointer"
                        FROM normalization.recording_credit_contributor_support s
                        JOIN normalization.recording_credits c ON c.uuid=s.credit_uuid
                        JOIN normalization.input_revisions r ON r.uuid=s.input_revision_uuid
                        JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                        WHERE c.source_uuid=@sourceUuid AND s.retired_work_uuid IS NULL AND c.state IN ('accepted','pending')
                        ORDER BY s.contributor_provider,s.contributor_type,s.contributor_key,c.role,c.scope_track_uuid,p.provider,p.resource_type,p.provider_key,s.json_pointer
                        LIMIT 10001
                        """,
                        new { sourceUuid },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (rows.Length > 10000)
            {
                throw new ArgumentException(
                    "Contributor retirement coverage exceeds its support limit."
                );
            }
            await t.CommitAsync(ct);
            return rows;
        });

    private static string? CoverageIssue(
        NormalizationPeopleResolutionInput input,
        RecordingCreditCompleteScope scope,
        IReadOnlyDictionary<Guid, PeopleAcceptanceClaimDecision> decisions
    )
    {
        if (input.DecodeIssues.Any(RecordingCreditDecodeIssues.AffectsCredits))
        {
            return "incomplete_decode";
        }
        if (
            scope.ScopeTrackUuid.HasValue
            && !input.OriginalInput.Binding.Files.Any(file =>
                file.Binding.Value?.TrackUuid == scope.ScopeTrackUuid
            )
        )
        {
            return "missing_checked_file";
        }
        if (
            input.Claims.Any(claim =>
                claim.ScopeIssue != null && Role(claim.Proposal.Mention.Role) == scope.Role
            )
            || input.UnresolvedCredits.Any(issue =>
                NormalizedPeopleService.RetainedCreditFieldRole(issue.Evidence.JsonPointer)
                    is not { } role
                || role == scope.Role
            )
        )
        {
            return "unresolved_extraction_coverage";
        }
        if (
            input
                .Claims.Where(claim =>
                    claim.ScopeTrackUuid == scope.ScopeTrackUuid
                    && Role(claim.Proposal.Mention.Role) == scope.Role
                )
                .Any(claim =>
                    decisions[claim.Proposal.ProposalUuid].Action
                    != PeopleResolutionAction.MatchExisting
                )
        )
        {
            return "unresolved_identity_coverage";
        }
        return null;
    }

    private static NormalizedEvidenceReference? KnownCurrentField(
        NormalizationRetainedDocument document,
        Guid inputRevisionUuid,
        string pointer,
        string role,
        IReadOnlyList<NormalizedEvidenceReference> resolvedClaims
    )
    {
        JsonElement value;
        try
        {
            value = document.Resolve(pointer);
        }
        catch (ArgumentException)
        {
            // A present array is a complete field even after its former entry was removed.
            var slash = pointer.LastIndexOf('/');
            if (slash < 0 || !int.TryParse(pointer[(slash + 1)..], out _))
            {
                return null;
            }
            pointer = pointer[..slash];
            try
            {
                value = document.Resolve(pointer);
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (
                value.ValueKind
                is not (JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Null)
            )
            {
                return null;
            }
        }
        var typedRole =
            NormalizedPeopleService.RetainedCreditFieldRole(pointer) == role
            && resolvedClaims.Any(claim =>
                claim.InputRevisionUuid == inputRevisionUuid
                && (
                    claim.JsonPointer == pointer
                    || claim.JsonPointer.StartsWith(pointer + "/", StringComparison.Ordinal)
                )
            );
        if (
            value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.String
                && string.IsNullOrWhiteSpace(value.GetString())
            || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0
            || typedRole
                && (
                    value.ValueKind == JsonValueKind.String
                    || value.ValueKind == JsonValueKind.Array
                        && value
                            .EnumerateArray()
                            .All(item => item.ValueKind == JsonValueKind.String)
                )
        )
        {
            var reference = new NormalizedEvidenceReference(inputRevisionUuid, pointer);
            document.ValidateEvidence(reference);
            return reference;
        }
        return null;
    }

    private sealed record PriorSupport(
        string Provider,
        string ResourceType,
        string ProviderKey,
        string Role,
        Guid? TrackUuid,
        string InputProvider,
        string InputType,
        string InputKey,
        string Pointer
    );
}
