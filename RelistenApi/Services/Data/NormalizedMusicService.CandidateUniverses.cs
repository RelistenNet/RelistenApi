using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private Task<
        IReadOnlyList<NormalizationCandidateUniverseKey>
    > ReadWorkCandidateWriteScopesAsync(Guid workUuid, CancellationToken cancellationToken) =>
        db.WithWriteConnection<IReadOnlyList<NormalizationCandidateUniverseKey>>(async connection =>
        {
            var owner =
                await connection.QuerySingleOrDefaultAsync<MusicOwner>(
                    new CommandDefinition(
                        """
                        SELECT s.domain AS "Domain",s.subject_kind AS "Kind",s.subject_uuid AS "EntityUuid"
                        FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                        WHERE r.uuid=@workUuid
                        """,
                        new { workUuid },
                        cancellationToken: cancellationToken
                    )
                )
                ?? throw new ArgumentException(
                    "The music work owner does not exist.",
                    nameof(workUuid)
                );
            return CandidateWriteScopes(owner);
        });

    private Task<
        IReadOnlyList<NormalizationCandidateUniverseKey>
    > ReadSubjectCandidateWriteScopesAsync(Guid subjectUuid, CancellationToken cancellationToken) =>
        db.WithWriteConnection<IReadOnlyList<NormalizationCandidateUniverseKey>>(async connection =>
        {
            var owner =
                await connection.QuerySingleOrDefaultAsync<MusicOwner>(
                    new CommandDefinition(
                        """
                        SELECT domain AS "Domain",subject_kind AS "Kind",subject_uuid AS "EntityUuid"
                        FROM normalization.work_subjects WHERE uuid=@subjectUuid
                        """,
                        new { subjectUuid },
                        cancellationToken: cancellationToken
                    )
                )
                ?? throw new ArgumentException(
                    "The music review owner does not exist.",
                    nameof(subjectUuid)
                );
            return CandidateWriteScopes(owner);
        });

    /// <summary>
    /// The owner tuple is immutable, so it can declare the complete clock lock set before the
    /// work store locks resources and owners. Payload scope is still checked in that transaction.
    /// Repertoire relationships also change composition matching facts; setlist/file usage does not.
    /// </summary>
    private static IReadOnlyList<NormalizationCandidateUniverseKey> CandidateWriteScopes(
        MusicOwner owner
    ) =>
        owner switch
        {
            { Domain: "songs", Kind: "artist_repertoire" } =>
            [
                NormalizationCandidateUniverseKey.Compositions,
                NormalizationCandidateUniverseKey.ForRepertoire(owner.EntityUuid),
            ],
            { Domain: "compositions", Kind: "composition" } =>
            [
                NormalizationCandidateUniverseKey.Compositions,
            ],
            _ => [],
        };
}
