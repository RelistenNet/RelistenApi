using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Registers trusted, independently evaluated artifacts without activating them. The command
/// host authenticates operator identity and verifies the referenced evaluation evidence before
/// calling this service; these methods are not unauthenticated HTTP request handlers.
/// Activation and revocation share the Work owner lock consumed by accepted publications.
/// </summary>
public sealed class VenueResolutionPolicyService(
    DbService db,
    NormalizationWorkService work,
    bool allowFixturePolicies = false
)
{
    public const string RegistryVersion = "venue-acceptance-registry-v1";
    public static readonly Guid PolicyKeyUuid = new("8a8bfbab-c894-4dd6-990b-a502ce7e805f");
    private static readonly Guid BootstrapWorkUuid = new("f585abbd-d562-45c1-9bbb-5d460bf6089b");
    public static NormalizationSubjectKey Owner =>
        new("venues", "acceptance_policy", PolicyKeyUuid);

    /// <summary>Hash of the exported typed artifact; collection ordering is part of its identity.</summary>
    public static string ComputeArtifactHash(VenueAcceptancePolicyArtifact artifact) =>
        Hash(JsonSerializer.Serialize(artifact with { ContentHash = "" }));

    /// <summary>Hash of the trusted attestation, excluding only its own hash field.</summary>
    public static string ComputeEvaluationHash(VenueAcceptanceEvaluationReceipt evaluation) =>
        Hash(JsonSerializer.Serialize(evaluation with { ReceiptHash = "" }));

    /// <summary>
    /// Persists an immutable artifact and evaluation. Exact replay returns its registration;
    /// changing either payload under an existing UUID conflicts. Registration leaves the active
    /// pointer unchanged. Fixture artifacts require explicit opt-in and a disposable test database.
    /// </summary>
    public Task<RegisteredVenueAcceptancePolicy> RegisterEvaluatedPolicyAsync(
        VenueAcceptancePolicyArtifact artifact,
        VenueAcceptanceEvaluationReceipt evaluation,
        string authenticatedOperator,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedOperator);
        var policy = Freeze(new RegisteredVenueAcceptancePolicy(artifact, evaluation));
        Validate(policy);
        if (policy.Evaluation.ApprovedBy != authenticatedOperator)
        {
            throw new ArgumentException(
                "The authenticated operator must attest the evaluation approval."
            );
        }
        return db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            RequireFixtureHost(c, policy, allowFixturePolicies);
            await using var t = await c.BeginTransactionAsync(cancellationToken);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                    new { key = $"normalization:venue-acceptance-artifact:{artifact.Uuid}" },
                    t,
                    cancellationToken: cancellationToken
                )
            );
            var existing = await ReadRegistrationAsync(c, t, artifact.Uuid, cancellationToken);
            if (existing != null)
            {
                if (
                    existing.Artifact.ContentHash != policy.Artifact.ContentHash
                    || existing.Evaluation.ReceiptHash != policy.Evaluation.ReceiptHash
                )
                {
                    throw new NormalizationConflictException(
                        "Policy UUID is bound to another artifact or evaluation."
                    );
                }
                await t.CommitAsync(cancellationToken);
                return existing;
            }
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.venue_acceptance_policies
                        (uuid,artifact_hash,artifact,evaluation_uuid,evaluation_hash,evaluation,registered_by)
                    VALUES(@uuid,@artifactHash,@artifact::jsonb,@evaluationUuid,@evaluationHash,@evaluation::jsonb,@authenticatedOperator)
                    """,
                    new
                    {
                        uuid = policy.Artifact.Uuid,
                        artifactHash = policy.Artifact.ContentHash,
                        artifact = JsonSerializer.Serialize(policy.Artifact),
                        evaluationUuid = policy.Evaluation.Uuid,
                        evaluationHash = policy.Evaluation.ReceiptHash,
                        evaluation = JsonSerializer.Serialize(policy.Evaluation),
                        authenticatedOperator,
                    },
                    t,
                    cancellationToken: cancellationToken
                )
            );
            await t.CommitAsync(cancellationToken);
            return policy;
        });
    }

    /// <summary>
    /// Reads the active artifact and its original owner observation together on the primary.
    /// Null means no active policy. Register Owner explicitly through Work before first review.
    /// </summary>
    public Task<ActiveVenueAcceptancePolicy?> ReadActiveAsync(
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection<ActiveVenueAcceptancePolicy?>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var active = await ReadActiveAsync(c, t, cancellationToken);
            if (active != null)
            {
                RequireFixtureHost(c, active.Policy, allowFixturePolicies);
            }
            await t.CommitAsync(cancellationToken);
            return active;
        });

    /// <summary>
    /// Reads the registry owner even when no policy is active. A fixed empty Work publication
    /// establishes that owner once; it never activates an artifact or reinstalls a changed
    /// configuration. An activation racing bootstrap can stale its empty publication. The
    /// subsequent primary snapshot still returns the actual owner and nullable active pointer.
    /// </summary>
    public async Task<VenueAcceptancePolicyState> ReadStateAsync(
        CancellationToken cancellationToken = default
    )
    {
        await work.BeginWorkAsync(
            BootstrapWorkUuid,
            Owner,
            RegistryVersion,
            [],
            [],
            cancellationToken: cancellationToken
        );
        await work.TryPublishAsync(
            BootstrapWorkUuid,
            (_, _, _) => Task.CompletedTask,
            cancellationToken
        );

        return await db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var state = await ReadStateAsync(c, t, cancellationToken);
            if (state.Active is { } active)
            {
                RequireFixtureHost(c, active.Policy, allowFixturePolicies);
            }
            await t.CommitAsync(cancellationToken);
            return state;
        });
    }

    /// <summary>
    /// Authenticate a saved positive or negative registry observation under the Work owner
    /// locks. No new bootstrap or configuration change is allowed inside publication.
    /// </summary>
    internal static async Task RequireStateAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueAcceptancePolicyState expected,
        CancellationToken ct
    )
    {
        var current = await ReadStateAsync(c, t, ct);
        if (
            current.OwnerObservation != expected.OwnerObservation
            || !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(current.Active),
                JsonSerializer.SerializeToElement(expected.Active)
            )
        )
        {
            throw new NormalizationConflictException(
                "The prepared venue acceptance policy state changed."
            );
        }
        if (current.Active is { } active)
        {
            // A negative enrollment may acknowledge an unsupported historical policy. Exact
            // state authentication does not grant its rule positive publication authority.
            Validate(active.Policy, requireCurrentVersions: false);
            RequireFixtureHost(c, active.Policy, allowFixture: true);
        }
    }

    private static async Task<VenueAcceptancePolicyState> ReadStateAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CancellationToken ct
    )
    {
        var owner = await c.QuerySingleOrDefaultAsync<PolicyOwnerRow>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",
                    editorial_revision AS "EditorialRevision",published_work_uuid AS "PublishedWorkUuid",
                    policy_version AS "PolicyVersion"
                FROM normalization.work_subjects
                WHERE domain='venues' AND subject_kind='acceptance_policy' AND subject_uuid=@PolicyKeyUuid
                """,
                new { PolicyKeyUuid },
                t,
                cancellationToken: ct
            )
        );
        if (owner == null || owner.PolicyVersion != RegistryVersion)
        {
            throw new NormalizationConflictException(
                "The venue acceptance registry requires its supported Work owner configuration."
            );
        }
        var observation = new NormalizationSubjectObservation(
            owner.SubjectUuid,
            owner.DesiredGeneration,
            owner.EditorialRevision,
            owner.PublishedWorkUuid
        );
        var active = await ReadActiveAsync(c, t, ct);
        if (active != null && active.OwnerObservation != observation)
        {
            throw new NormalizationConflictException(
                "The active policy and registry owner must share one original observation."
            );
        }
        return new(active, observation);
    }

    /// <summary>
    /// Activates, replaces or revokes a registered artifact under a reviewed policy owner.
    /// Work serializes this change with publications observing that owner; the pointer and review
    /// receipt commit together. Exact command replay returns the original receipt after revocation.
    /// </summary>
    public Task<NormalizationReviewResult> ApplyDecisionAsync(
        VenueAcceptancePolicyDecision decision,
        string authenticatedOperator,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedOperator);
        if (decision.ArtifactUuid == Guid.Empty || decision.ExpectedArtifactUuid == Guid.Empty)
        {
            throw new ArgumentException("Policy references must be stable UUIDs or null.");
        }
        return work.ApplyReviewAsync(
            new(
                decision.OperationUuid,
                decision.SubjectUuid,
                decision.ExpectedEditorialRevision,
                decision.ArtifactUuid == null ? "retract"
                    : decision.ExpectedArtifactUuid == null ? "accept"
                    : "replace",
                JsonSerializer.SerializeToElement(decision),
                decision.Reason,
                authenticatedOperator
            ),
            async (c, t, ct) =>
            {
                await NormalizedPeopleService.ValidateOwnerAsync(
                    c,
                    t,
                    decision.SubjectUuid,
                    Owner.Domain,
                    Owner.Kind,
                    Owner.Uuid,
                    ct
                );
                var current = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT artifact_uuid FROM normalization.venue_acceptance_active WHERE policy_key_uuid=@PolicyKeyUuid",
                        new { PolicyKeyUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (current != decision.ExpectedArtifactUuid)
                {
                    throw new NormalizationConflictException(
                        "The active policy changed after this decision was prepared."
                    );
                }
                if (decision.ArtifactUuid is { } uuid)
                {
                    var target =
                        await ReadRegistrationAsync(c, t, uuid, ct)
                        ?? throw new ArgumentException(
                            "Register a verified evaluation before activation."
                        );
                    Validate(target);
                    RequireFixtureHost(c, target, allowFixturePolicies);
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.venue_acceptance_active(policy_key_uuid,artifact_uuid)
                        VALUES(@PolicyKeyUuid,@ArtifactUuid)
                        ON CONFLICT(policy_key_uuid) DO UPDATE SET artifact_uuid=excluded.artifact_uuid
                        """,
                        new { PolicyKeyUuid, decision.ArtifactUuid },
                        t,
                        cancellationToken: ct
                    )
                );
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Called only inside a Work publication callback after Work has locked the prepared policy
    /// owner observation. Takes no locks of its own. A preflight enabled flag cannot replace this
    /// exact artifact, attestation and original owner-stamp check.
    /// </summary>
    internal static async Task RequireActiveAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ActiveVenueAcceptancePolicy expected,
        CancellationToken ct
    )
    {
        Validate(expected.Policy);
        var current = await ReadActiveAsync(c, t, ct);
        if (
            current == null
            || current.OwnerObservation != expected.OwnerObservation
            || current.Policy.Artifact.Uuid != expected.Policy.Artifact.Uuid
            || current.Policy.Artifact.ContentHash != expected.Policy.Artifact.ContentHash
            || current.Policy.Evaluation.Uuid != expected.Policy.Evaluation.Uuid
            || current.Policy.Evaluation.ReceiptHash != expected.Policy.Evaluation.ReceiptHash
        )
        {
            throw new NormalizationConflictException(
                "The prepared acceptance policy is no longer active."
            );
        }
        RequireFixtureHost(c, current.Policy, allowFixture: true);
    }

    internal static async Task<ActiveVenueAcceptancePolicy?> ReadActiveAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ActiveRow>(
            new CommandDefinition(
                """
                SELECT p.artifact::text AS Artifact,p.evaluation::text AS Evaluation,
                    p.artifact_hash AS ArtifactHash,p.evaluation_hash AS EvaluationHash,
                    s.uuid AS SubjectUuid,s.desired_generation AS DesiredGeneration,
                    s.editorial_revision AS EditorialRevision,s.published_work_uuid AS PublishedWorkUuid
                FROM normalization.venue_acceptance_active a
                JOIN normalization.venue_acceptance_policies p ON p.uuid=a.artifact_uuid
                JOIN normalization.work_subjects s ON s.domain='venues' AND s.subject_kind='acceptance_policy'
                    AND s.subject_uuid=a.policy_key_uuid
                WHERE a.policy_key_uuid=@PolicyKeyUuid
                """,
                new { PolicyKeyUuid },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        var policy = Decode(row);
        return new(
            policy,
            new(
                row.SubjectUuid,
                row.DesiredGeneration,
                row.EditorialRevision,
                row.PublishedWorkUuid
            )
        );
    }

    private static async Task<RegisteredVenueAcceptancePolicy?> ReadRegistrationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<PolicyRow>(
            new CommandDefinition(
                """
                SELECT artifact::text AS Artifact,evaluation::text AS Evaluation,
                    artifact_hash AS ArtifactHash,evaluation_hash AS EvaluationHash
                FROM normalization.venue_acceptance_policies WHERE uuid=@uuid
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null ? null : Decode(row);
    }

    private static RegisteredVenueAcceptancePolicy Decode(PolicyRow row)
    {
        var policy = new RegisteredVenueAcceptancePolicy(
            JsonSerializer.Deserialize<VenueAcceptancePolicyArtifact>(row.Artifact)!,
            JsonSerializer.Deserialize<VenueAcceptanceEvaluationReceipt>(row.Evaluation)!
        );
        // Old registrations remain inspectable after a feature version changes. Activation and
        // publication enforce current applicability separately; retained evidence stays readable.
        Validate(policy, requireCurrentVersions: false);
        if (
            policy.Artifact.ContentHash != row.ArtifactHash
            || policy.Evaluation.ReceiptHash != row.EvaluationHash
        )
        {
            throw new NormalizationConflictException(
                "The registered policy hashes do not match its retained payloads."
            );
        }
        return policy;
    }

    internal static void Validate(
        RegisteredVenueAcceptancePolicy policy,
        bool requireCurrentVersions = true
    )
    {
        var a = policy.Artifact;
        var e = policy.Evaluation;
        if (
            a.Uuid == Guid.Empty
            || e.Uuid == Guid.Empty
            || e.ArtifactUuid != a.Uuid
            || string.IsNullOrWhiteSpace(a.Version)
            || string.IsNullOrWhiteSpace(e.EvaluatorVersion)
            || string.IsNullOrWhiteSpace(e.IndependentEvaluator)
            || string.IsNullOrWhiteSpace(e.ApprovedBy)
            || a.ContentHash != ComputeArtifactHash(a)
            || e.ArtifactHash != a.ContentHash
            || e.ReceiptHash != ComputeEvaluationHash(e)
            || !IsHash(e.DatasetHash)
            || !IsHash(e.SplitHash)
            || !IsHash(e.AdjudicationHash)
            || e.FixtureOnly != a.FixtureOnly
            || !ValidEvidence(e.Matching)
            || (a.CreationRuleId != null && !ValidEvidence(e.Creation))
            || (a.CreationRuleId == null && e.Creation != null)
        )
        {
            throw new ArgumentException(
                "A policy needs matching immutable hashes and independently approved venue-correspondence evaluation evidence."
            );
        }
        if (requireCurrentVersions && !VenueResolutionPolicyEvaluator.Supports(a))
        {
            throw new ArgumentException(
                "The policy must name a supported, separately evaluated venue rule and exact feature versions."
            );
        }
    }

    private static bool ValidEvidence(VenueAcceptanceCapabilityEvaluation? evidence) =>
        evidence is { Cases: > 0, Errors: >= 0 } && evidence.Errors <= evidence.Cases;

    private static bool IsHash(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void RequireFixtureHost(
        NpgsqlConnection c,
        RegisteredVenueAcceptancePolicy policy,
        bool allowFixture
    )
    {
        if (
            policy.Artifact.FixtureOnly
            && (!allowFixture || !c.Database.StartsWith("relisten_test_", StringComparison.Ordinal))
        )
        {
            throw new ArgumentException(
                "Fixture policies are restricted to explicitly enabled disposable test databases."
            );
        }
    }

    private static RegisteredVenueAcceptancePolicy Freeze(RegisteredVenueAcceptancePolicy policy) =>
        JsonSerializer.Deserialize<RegisteredVenueAcceptancePolicy>(
            JsonSerializer.Serialize(policy)
        )!;

    private static string Hash(string value) =>
        Convert
            .ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private class PolicyRow
    {
        public string Artifact { get; init; } = "";
        public string Evaluation { get; init; } = "";
        public string ArtifactHash { get; init; } = "";
        public string EvaluationHash { get; init; } = "";
    }

    private sealed class ActiveRow : PolicyRow
    {
        public Guid SubjectUuid { get; init; }
        public long DesiredGeneration { get; init; }
        public long EditorialRevision { get; init; }
        public Guid? PublishedWorkUuid { get; init; }
    }

    private sealed record PolicyOwnerRow(
        Guid SubjectUuid,
        long DesiredGeneration,
        long EditorialRevision,
        Guid? PublishedWorkUuid,
        string PolicyVersion
    );
}
