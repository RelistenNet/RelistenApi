using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>
    /// Collects only the owners and catalog contexts actually consumed by a facts projection.
    /// The final read runs in that projection's transaction: missing stamps are not initialized
    /// or silently replaced, and citations do not imply that input resource heads are current.
    /// </summary>
    private sealed class CandidateFactsContext
    {
        private readonly HashSet<CandidateOwnerRequest> _owners = [];
        private readonly HashSet<Guid> _artists = [];
        private readonly List<CandidateFactsIssue> _issues = [];

        public IReadOnlyList<CandidateFactsIssue> Issues => _issues.ToArray();

        public void AddOwner(Guid? subjectUuid, Guid? candidateUuid = null)
        {
            if (subjectUuid is null)
            {
                AddIssue(CandidateFactsIssueKind.MissingOwner, candidateUuid);
                return;
            }

            _owners.Add(new(subjectUuid.Value, candidateUuid));
        }

        public void AddArtist(Guid artistUuid)
        {
            _artists.Add(artistUuid);
        }

        public void AddIssue(
            CandidateFactsIssueKind kind,
            Guid? candidateUuid = null,
            string? factKind = null
        )
        {
            var issue = new CandidateFactsIssue(kind, candidateUuid, factKind);
            if (!_issues.Contains(issue))
            {
                _issues.Add(issue);
            }
        }

        // Capture body requirements before final owner/catalog reads. Those later reads
        // add query-union diagnostics and must never become a cached body's issues.
        public CandidateFactsRequirements CaptureRequirements() =>
            new(_owners.ToArray(), _artists.ToArray(), _issues.ToArray());

        public void Include(CandidateFactsRequirements requirements)
        {
            foreach (var owner in requirements.Owners)
            {
                AddOwner(owner.SubjectUuid, owner.CandidateUuid);
            }
            foreach (var artist in requirements.Artists)
            {
                AddArtist(artist);
            }
            foreach (var issue in requirements.Issues)
            {
                AddIssue(issue.Kind, issue.CandidateUuid, issue.FactKind);
            }
        }

        public async Task<CandidateFactsReadSet> ReadReadSetAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<NormalizationCandidateUniverseDependency> candidateUniverses,
            CancellationToken ct
        )
        {
            var owners = await ReadOwnersAsync(connection, transaction, ct);
            var catalogInputs = await ReadCatalogInputsAsync(connection, transaction, ct);

            return new CandidateFactsReadSet(candidateUniverses.ToArray(), owners, catalogInputs);
        }

        private async Task<NormalizationSubjectObservation[]> ReadOwnersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct
        )
        {
            var subjectUuids = _owners.Select(owner => owner.SubjectUuid).Distinct().ToArray();
            if (subjectUuids.Length == 0)
            {
                return [];
            }

            var rows = (
                await connection.QueryAsync<NormalizationSubjectObservation>(
                    new CommandDefinition(
                        """
                        SELECT s.uuid AS "SubjectUuid",s.desired_generation AS "DesiredGeneration",
                            s.editorial_revision AS "EditorialRevision",
                            s.published_work_uuid AS "PublishedWorkUuid"
                        FROM normalization.work_subjects s
                        WHERE s.uuid=ANY(@subjectUuids)
                        ORDER BY s.uuid
                        """,
                        new { subjectUuids },
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToArray();

            RecordMissingOwners(rows);
            return rows;
        }

        private void RecordMissingOwners(IReadOnlyList<NormalizationSubjectObservation> rows)
        {
            var foundUuids = rows.Select(row => row.SubjectUuid).ToHashSet();
            foreach (var owner in _owners.Where(owner => !foundUuids.Contains(owner.SubjectUuid)))
            {
                AddIssue(CandidateFactsIssueKind.MissingOwner, owner.CandidateUuid);
            }
        }

        private async Task<NormalizationCatalogDependency[]> ReadCatalogInputsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct
        )
        {
            if (_artists.Count == 0)
            {
                return [];
            }

            var rows = (
                await connection.QueryAsync<NormalizationCatalogDependency>(
                    new CommandDefinition(
                        """
                        SELECT artist_uuid AS "ArtistUuid",generation AS "Generation"
                        FROM normalization.catalog_artist_revisions
                        WHERE artist_uuid=ANY(@artistUuids)
                        ORDER BY artist_uuid
                        """,
                        new { artistUuids = _artists.ToArray() },
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToArray();

            var found = rows.Select(row => row.ArtistUuid).ToHashSet();
            if (_artists.Any(uuid => !found.Contains(uuid)))
            {
                AddIssue(CandidateFactsIssueKind.MissingCatalogClock, factKind: "artist");
            }

            return rows;
        }
    }

    private sealed record CandidateOwnerRequest(Guid SubjectUuid, Guid? CandidateUuid);

    private sealed record CandidateFactsRequirements(
        IReadOnlyList<CandidateOwnerRequest> Owners,
        IReadOnlyList<Guid> Artists,
        IReadOnlyList<CandidateFactsIssue> Issues
    );
}
