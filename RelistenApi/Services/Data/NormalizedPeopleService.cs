using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Global identities and evidenced recording credits; equal names only retrieve candidates.</summary>
public sealed partial class NormalizedPeopleService(DbService db, NormalizationWorkService work)
{
    // Reserve both scopes before owner locks: person identity edits also change resolved
    // composition authors. Automatic credit-only changes advance only people; reviewed
    // credits also update the linked person's identity revision.
    private static readonly NormalizationCandidateUniverseKey[] IdentityCandidateWriteScopes =
    [
        NormalizationCandidateUniverseKey.Compositions,
        NormalizationCandidateUniverseKey.People,
    ];

    public Task<NormalizationPublicationResult> PublishAsync(
        Guid workUuid,
        PeoplePublication publication,
        CancellationToken cancellationToken = default
    ) =>
        work.TryPublishAsync(
            workUuid,
            async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:people:identity',0))",
                        transaction: transaction,
                        cancellationToken: ct
                    )
                );
                await ValidatePeoplePublicationOwner(
                    connection,
                    transaction,
                    workUuid,
                    publication,
                    ct
                );
                var references = publication
                    .Aliases.Select(a => a.Evidence)
                    .Concat(publication.ExternalIdentities.Select(a => a.Evidence))
                    .Concat(publication.Credits.SelectMany(a => a.Evidence))
                    .ToArray();
                await ValidatePublicationEvidenceAsync(
                    connection,
                    transaction,
                    workUuid,
                    "people",
                    references,
                    ct
                );
                var created = new HashSet<Guid>();
                foreach (var person in publication.People)
                {
                    if (
                        !await connection.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@Uuid)",
                                person,
                                transaction,
                                cancellationToken: ct
                            )
                        )
                        && !publication.Aliases.Any(a =>
                            a.PersonUuid == person.Uuid
                            && NameKey(a.Wording) == NameKey(person.DisplayName)
                        )
                    )
                    {
                        throw new ArgumentException(
                            "Automatic person creation requires an evidenced display-name assertion."
                        );
                    }
                    if (await InsertPerson(connection, transaction, person, ct))
                    {
                        created.Add(person.Uuid);
                    }
                }
                var assertionChanges = await WriteAliases(
                    connection,
                    transaction,
                    workUuid,
                    publication.Aliases,
                    created,
                    ct
                );
                foreach (var identity in publication.ExternalIdentities)
                {
                    if (
                        !created.Contains(identity.PersonUuid)
                        && !await connection.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT EXISTS(SELECT 1 FROM normalization.person_external_identities WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey)",
                                identity,
                                transaction,
                                cancellationToken: ct
                            )
                        )
                    )
                    {
                        await ValidateWorkOwnerAsync(
                            connection,
                            transaction,
                            workUuid,
                            "people",
                            "person",
                            identity.PersonUuid,
                            ct
                        );
                    }
                    assertionChanges += await WriteExternalIdentity(
                        connection,
                        transaction,
                        identity,
                        ct
                    );
                }
                if (assertionChanges > 0 && publication.Credits.Count == 0)
                {
                    var owner = await ReadWorkContext(connection, transaction, workUuid, ct);
                    if (!created.Contains(owner.EntityUuid))
                    {
                        await connection.ExecuteAsync(
                            new CommandDefinition(
                                "UPDATE normalization.people SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@uuid",
                                new { uuid = owner.EntityUuid },
                                transaction,
                                cancellationToken: ct
                            )
                        );
                    }
                }
                foreach (var credit in publication.Credits)
                {
                    if (credit.Basis == "reviewed")
                    {
                        throw new ArgumentException(
                            "Automatic publication cannot claim reviewed authority."
                        );
                    }
                    await WriteCredit(connection, transaction, credit, workUuid, false, 0, ct);
                }
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );

    private static async Task<int> WriteAliases(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workUuid,
        IReadOnlyList<PersonAlias> aliases,
        HashSet<Guid> created,
        CancellationToken ct
    )
    {
        var inserted = 0;
        foreach (var originalAlias in aliases)
        {
            var alias = originalAlias;
            ValidateAutomaticMetadataBasis(alias.Basis);
            await ValidateNameEvidenceAsync(
                connection,
                transaction,
                alias.Wording,
                alias.Evidence,
                ct
            );
            if (
                !created.Contains(alias.PersonUuid)
                && !await connection.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM normalization.person_aliases WHERE uuid=@Uuid)",
                        alias,
                        transaction,
                        cancellationToken: ct
                    )
                )
            )
            {
                await ValidateWorkOwnerAsync(
                    connection,
                    transaction,
                    workUuid,
                    "people",
                    "person",
                    alias.PersonUuid,
                    ct
                );
            }
            var state = await AutomaticMetadataState(
                connection,
                transaction,
                "person_aliases",
                "person_uuid",
                alias.PersonUuid,
                "name_key=@key OR uuid=@Uuid",
                new { key = NameKey(alias.Wording), alias.Uuid },
                alias.Evidence,
                ct
            );
            if (state == null)
            {
                continue;
            }
            if (state == "pending")
            {
                alias = alias with { Uuid = Guid.NewGuid() };
            }
            inserted += await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.person_aliases(uuid,person_uuid,wording,name_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                    VALUES(@Uuid,@PersonUuid,@Wording,@nameKey,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,@Basis)
                    ON CONFLICT(uuid) DO NOTHING
                    """,
                    new
                    {
                        state,
                        alias.Basis,
                        alias.Uuid,
                        alias.PersonUuid,
                        alias.Wording,
                        nameKey = NameKey(alias.Wording),
                        alias.Evidence.InputRevisionUuid,
                        alias.Evidence.JsonPointer,
                        alias.Evidence.StartCodePoint,
                        alias.Evidence.EndCodePoint,
                        alias.Evidence.Quote,
                    },
                    transaction,
                    cancellationToken: ct
                )
            );
            if (
                !await connection.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT person_uuid=@PersonUuid AND wording=@Wording AND input_revision_uuid=@InputRevisionUuid AND json_pointer=@JsonPointer AND start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer AND end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer AND quote IS NOT DISTINCT FROM @Quote::text FROM normalization.person_aliases WHERE uuid=@Uuid",
                        new
                        {
                            alias.Uuid,
                            alias.PersonUuid,
                            alias.Wording,
                            alias.Evidence.InputRevisionUuid,
                            alias.Evidence.JsonPointer,
                            alias.Evidence.StartCodePoint,
                            alias.Evidence.EndCodePoint,
                            alias.Evidence.Quote,
                        },
                        transaction,
                        cancellationToken: ct
                    )
                )
            )
            {
                throw new NormalizationConflictException(
                    "Alias UUID is already bound to different wording, identity or evidence."
                );
            }
        }
        return inserted;
    }

    public Task<NormalizationReviewResult> ApplyDecisionAsync(
        RecordingCreditDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    ) =>
        work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                command.Action,
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator
            ),
            async (connection, transaction, ct) =>
            {
                await ValidateOwnerAsync(
                    connection,
                    transaction,
                    command.SubjectUuid,
                    "people",
                    "recording",
                    command.Credit.SourceUuid,
                    ct
                );
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:people:identity',0))",
                        transaction: transaction,
                        cancellationToken: ct
                    )
                );
                await ValidateEvidenceAsync(connection, transaction, command.Credit.Evidence, ct);
                var state = command.Action switch
                {
                    "accept" or "replace" => "accepted",
                    "reject" => "rejected",
                    "retract" => "superseded",
                    _ => throw new ArgumentException("Unsupported credit action."),
                };
                await WriteCredit(
                    connection,
                    transaction,
                    command.Credit with
                    {
                        State = state,
                        Basis = "reviewed",
                    },
                    command.OperationUuid,
                    true,
                    command.ExpectedCreditRevision,
                    ct
                );
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );

    public Task<IReadOnlyList<CanonicalPerson>> FindCandidatesAsync(
        string name,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection<IReadOnlyList<CanonicalPerson>>(async c =>
            (
                await c.QueryAsync<CanonicalPerson>(
                    new CommandDefinition(
                        """
                        SELECT p.uuid AS "Uuid",p.display_name AS "DisplayName",p.kind AS "Kind" FROM normalization.people p
                        WHERE NOT EXISTS(SELECT 1 FROM normalization.person_redirects r WHERE r.retired_uuid=p.uuid)
                          AND (EXISTS(SELECT 1 FROM normalization.person_identity_resolution r JOIN normalization.people original ON original.uuid=r.original_uuid WHERE r.resolved_uuid=p.uuid AND original.name_key=@key) OR EXISTS(SELECT 1 FROM normalization.resolved_person_aliases a WHERE a.resolved_person_uuid=p.uuid AND a.name_key=@key))
                        ORDER BY p.uuid LIMIT 200
                        """,
                        new { key = NameKey(name) },
                        cancellationToken: cancellationToken
                    )
                )
            ).ToArray()
        );

    public static string NameKey(string name) =>
        string.Join(
            ' ',
            name.Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );

    public static async Task<Guid> ResolvePersonUuidAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct = default
    )
    {
        var resolved = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                WITH RECURSIVE chain(uuid) AS (SELECT uuid FROM normalization.people WHERE uuid=@uuid
                    UNION ALL SELECT r.survivor_uuid FROM chain c JOIN normalization.person_redirects r ON r.retired_uuid=c.uuid)
                SELECT uuid FROM chain c WHERE NOT EXISTS(SELECT 1 FROM normalization.person_redirects r WHERE r.retired_uuid=c.uuid)
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return resolved ?? throw new ArgumentException("Person identity does not exist.");
    }

    private static async Task<bool> InsertPerson(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CanonicalPerson person,
        CancellationToken ct
    )
    {
        if (
            await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@Uuid AND metadata_reviewed)",
                    person,
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            // A provider refresh may repeat the identity shape that the operator corrected.
            return false;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(person.DisplayName);
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@Uuid,@DisplayName,@key,@Kind) ON CONFLICT(uuid) DO NOTHING",
                new
                {
                    person.Uuid,
                    person.DisplayName,
                    key = NameKey(person.DisplayName),
                    person.Kind,
                },
                t,
                cancellationToken: ct
            )
        );
        var same = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT metadata_reviewed OR (display_name=@DisplayName AND kind=@Kind) FROM normalization.people WHERE uuid=@Uuid",
                person,
                t,
                cancellationToken: ct
            )
        );
        if (!same)
        {
            throw new NormalizationConflictException(
                "Person UUID already has a different meaning."
            );
        }
        return inserted == 1;
    }

    private static async Task<int> WriteExternalIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PersonExternalIdentity identity,
        CancellationToken ct
    )
    {
        ValidateAutomaticMetadataBasis(identity.Basis);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ResourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProviderKey);
        var state = await AutomaticMetadataState(
            c,
            t,
            "person_external_identities",
            "person_uuid",
            identity.PersonUuid,
            "provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey",
            identity,
            identity.Evidence,
            ct
        );
        if (state == null)
        {
            return 0;
        }
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.person_external_identities(uuid,person_uuid,provider,resource_type,provider_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                VALUES(@uuid,@PersonUuid,@Provider,@ResourceType,@ProviderKey,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,@Basis) ON CONFLICT DO NOTHING
                """,
                new
                {
                    uuid = state == "pending" ? Guid.NewGuid() : identity.Uuid ?? Guid.NewGuid(),
                    identity.Basis,
                    identity.PersonUuid,
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey,
                    identity.Evidence.InputRevisionUuid,
                    identity.Evidence.JsonPointer,
                    identity.Evidence.StartCodePoint,
                    identity.Evidence.EndCodePoint,
                    identity.Evidence.Quote,
                    state,
                },
                t,
                cancellationToken: ct
            )
        );
        var owner = await c.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                "SELECT person_uuid FROM normalization.person_external_identities WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey AND state='accepted'",
                identity,
                t,
                cancellationToken: ct
            )
        );
        if (
            state == "accepted"
            && owner.HasValue
            && await ResolvePersonUuidAsync(c, t, owner.Value, ct)
                != await ResolvePersonUuidAsync(c, t, identity.PersonUuid, ct)
        )
        {
            throw new NormalizationConflictException(
                "Provider identity already belongs to a different person."
            );
        }
        return inserted;
    }

    private static async Task WriteCredit(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingCredit credit,
        Guid decisionUuid,
        bool reviewed,
        long expectedRevision,
        CancellationToken ct
    )
    {
        if (credit.Evidence.Count == 0)
        {
            throw new ArgumentException("Credits require retained evidence.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(credit.OriginalText);
        if (credit.PersonUuid.HasValue)
        {
            credit = credit with
            {
                PersonUuid = await ResolvePersonUuidAsync(c, t, credit.PersonUuid.Value, ct),
            };
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new { key = $"normalization:credits:{credit.SourceUuid}" },
                t,
                cancellationToken: ct
            )
        );
        var current = await c.QuerySingleOrDefaultAsync<CreditRow>(
            new CommandDefinition(
                "SELECT uuid AS \"Uuid\",revision AS \"Revision\",basis AS \"Basis\",state AS \"State\",source_uuid AS \"SourceUuid\",scope_track_uuid AS \"ScopeTrackUuid\" FROM normalization.recording_credits WHERE uuid=@Uuid FOR UPDATE",
                credit,
                t,
                cancellationToken: ct
            )
        );
        if (!reviewed)
        {
            var resolved = await ResolveAutomaticCredit(c, t, credit, current, ct);
            if (resolved == null)
            {
                return;
            }
            credit = resolved.Credit;
            current = resolved.Current;
        }
        if (reviewed && (current?.Revision ?? 0) != expectedRevision)
        {
            throw new NormalizationConflictException("Credit changed after review was prepared.");
        }
        await ValidateCreditScope(c, t, credit, current, ct);
        await StoreCreditRevision(c, t, credit, current, decisionUuid, reviewed, ct);
    }

    private static async Task StoreCreditRevision(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingCredit credit,
        CreditRow? current,
        Guid decisionUuid,
        bool reviewed,
        CancellationToken ct
    )
    {
        if (current != null)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO normalization.recording_credit_history SELECT *,@decisionUuid FROM normalization.recording_credits WHERE uuid=@Uuid",
                    new { credit.Uuid, decisionUuid },
                    t,
                    cancellationToken: ct
                )
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.recording_credits(uuid,source_uuid,role,original_text,person_uuid,scope_track_uuid,state,basis,reason_code,decision_uuid)
                VALUES(@Uuid,@SourceUuid,@Role,@OriginalText,@PersonUuid,@ScopeTrackUuid,@State,@Basis,@ReasonCode,@decisionUuid)
                ON CONFLICT(uuid) DO UPDATE SET role=EXCLUDED.role,original_text=EXCLUDED.original_text,person_uuid=EXCLUDED.person_uuid,state=EXCLUDED.state,
                    basis=EXCLUDED.basis,reason_code=EXCLUDED.reason_code,decision_uuid=EXCLUDED.decision_uuid,revision=recording_credits.revision+1,updated_at=clock_timestamp()
                """,
                new
                {
                    credit.Uuid,
                    credit.SourceUuid,
                    credit.Role,
                    credit.OriginalText,
                    credit.PersonUuid,
                    credit.ScopeTrackUuid,
                    credit.State,
                    credit.Basis,
                    credit.ReasonCode,
                    decisionUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (reviewed)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE normalization.people SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@PersonUuid",
                    credit,
                    t,
                    cancellationToken: ct
                )
            );
        }
        await AppendCreditEvidence(c, t, credit.Uuid, credit.Evidence, ct);
    }

    private static async Task ValidateCreditScope(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingCredit credit,
        CreditRow? current,
        CancellationToken ct
    )
    {
        if (current == null)
        {
            var valid = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM sources s WHERE s.uuid=@SourceUuid AND
                        (@ScopeTrackUuid::uuid IS NULL OR EXISTS(SELECT 1 FROM source_tracks st JOIN source_sets ss ON ss.id=st.source_set_id AND ss.source_id=s.id
                            WHERE st.uuid=@ScopeTrackUuid AND st.source_id=s.id AND NOT st.is_orphaned)))
                    """,
                    credit,
                    t,
                    cancellationToken: ct
                )
            );
            if (!valid)
            {
                throw new ArgumentException(
                    "Credit source or track scope is not in the current catalog."
                );
            }
        }
        else if (
            current.SourceUuid != credit.SourceUuid
            || current.ScopeTrackUuid != credit.ScopeTrackUuid
        )
        {
            throw new NormalizationConflictException(
                "A credit UUID cannot be rebound to a different recording scope."
            );
        }
    }

    private static async Task<CreditWrite?> ResolveAutomaticCredit(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingCredit credit,
        CreditRow? current,
        CancellationToken ct
    )
    {
        var previousCorrection = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.recording_credit_history h
                    JOIN normalization.review_operations r ON r.uuid=h.superseding_decision_uuid
                    WHERE h.source_uuid=@SourceUuid AND h.role=@Role AND h.original_text=@OriginalText
                      AND h.scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid)
                """,
                credit,
                t,
                cancellationToken: ct
            )
        );
        if (previousCorrection)
        {
            var sameEvidence = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.recording_credit_history h
                        JOIN normalization.review_operations r ON r.uuid=h.superseding_decision_uuid
                        JOIN normalization.recording_credit_evidence e ON e.credit_uuid=h.uuid AND e.credit_revision=h.revision
                        WHERE h.source_uuid=@SourceUuid AND h.role=@Role AND h.original_text=@OriginalText
                          AND h.scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid AND e.input_revision_uuid=ANY(@revisions))
                    """,
                    new
                    {
                        credit.SourceUuid,
                        credit.Role,
                        credit.OriginalText,
                        credit.ScopeTrackUuid,
                        revisions = credit.Evidence.Select(e => e.InputRevisionUuid).ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (sameEvidence)
            {
                return null;
            }
            credit = credit with
            {
                Uuid = Guid.NewGuid(),
                State = "pending",
                ReasonCode = "changed_evidence_reviewed_claim",
            };
            current = null;
        }
        var claim = await c.QuerySingleOrDefaultAsync<CreditRow>(
            new CommandDefinition(
                """
                SELECT uuid AS "Uuid",revision AS "Revision",basis AS "Basis",state AS "State",source_uuid AS "SourceUuid",scope_track_uuid AS "ScopeTrackUuid",person_uuid AS "PersonUuid"
                FROM normalization.recording_credits WHERE source_uuid=@SourceUuid AND role=@Role
                  AND scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid AND original_text=@OriginalText
                  AND state<>'superseded' ORDER BY (basis='reviewed') DESC,(state='accepted') DESC,uuid LIMIT 1 FOR UPDATE
                """,
                credit,
                t,
                cancellationToken: ct
            )
        );
        if (claim?.Basis == "reviewed")
        {
            var sameEvidence = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.recording_credit_evidence WHERE credit_uuid=@Uuid AND input_revision_uuid=ANY(@revisions))",
                    new
                    {
                        claim.Uuid,
                        revisions = credit.Evidence.Select(e => e.InputRevisionUuid).ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (sameEvidence)
            {
                return null;
            }
            // New evidence deserves fresh review, while the accepted correction/exclusion stays in force.
            credit = credit with
            {
                Uuid = Guid.NewGuid(),
                State = "pending",
                ReasonCode = "changed_evidence_reviewed_claim",
            };
            current = null;
        }
        else if (claim != null)
        {
            credit = credit with { Uuid = claim.Uuid };
            current = claim;
        }
        else if (current?.Basis == "reviewed")
        {
            credit = credit with
            {
                Uuid = Guid.NewGuid(),
                State = "pending",
                ReasonCode = "conflicting_evidence",
            };
            current = null;
        }
        if (
            credit.State == "pending"
            && await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.recording_credits a JOIN normalization.recording_credit_evidence e ON e.credit_uuid=a.uuid AND e.credit_revision=a.revision
                        WHERE a.source_uuid=@SourceUuid AND a.role=@Role AND a.original_text=@OriginalText AND a.person_uuid IS NOT DISTINCT FROM @PersonUuid::uuid
                          AND a.scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid AND a.state='pending' AND e.input_revision_uuid=ANY(@revisions))
                    """,
                    new
                    {
                        credit.SourceUuid,
                        credit.Role,
                        credit.OriginalText,
                        credit.PersonUuid,
                        credit.ScopeTrackUuid,
                        revisions = credit.Evidence.Select(e => e.InputRevisionUuid).ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            return null;
        }
        if (credit.State == "accepted" && credit.PersonUuid.HasValue)
        {
            var duplicate = await c.QuerySingleOrDefaultAsync<CreditRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid",revision AS "Revision",basis AS "Basis",state AS "State",source_uuid AS "SourceUuid",scope_track_uuid AS "ScopeTrackUuid",person_uuid AS "PersonUuid"
                    FROM normalization.recording_credits WHERE source_uuid=@SourceUuid AND role=@Role AND person_uuid=@PersonUuid
                    AND scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid AND state='accepted' AND uuid<>@Uuid FOR UPDATE
                    """,
                    credit,
                    t,
                    cancellationToken: ct
                )
            );
            if (duplicate != null)
            {
                await AppendCreditEvidence(c, t, duplicate.Uuid, credit.Evidence, ct);
                return null;
            }
        }
        return new CreditWrite(credit, current);
    }

    private sealed record CreditWrite(RecordingCredit Credit, CreditRow? Current);

    private static async Task AppendCreditEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        CancellationToken ct
    )
    {
        foreach (var e in evidence)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_credit_evidence(credit_uuid,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,credit_revision)
                    SELECT @uuid,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,revision FROM normalization.recording_credits WHERE uuid=@uuid
                    """,
                    new
                    {
                        uuid,
                        e.InputRevisionUuid,
                        e.JsonPointer,
                        e.StartCodePoint,
                        e.EndCodePoint,
                        e.Quote,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private sealed class CreditRow
    {
        public Guid Uuid { get; init; }
        public Guid? PersonUuid { get; init; }
        public long Revision { get; init; }
        public string Basis { get; init; } = null!;
        public string State { get; init; } = null!;
        public Guid SourceUuid { get; init; }
        public Guid? ScopeTrackUuid { get; init; }
    }
}
