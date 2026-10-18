using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
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
    /// <summary>
    /// Creates one people reader inside the caller's database-enforced read-only RR scope.
    /// The verification query establishes that snapshot before any reusable body is read.
    /// The reader owns no connection/transaction and must be disposed before either ends.
    /// It is sequential and private to this scope, never a Work publication callback cache.
    /// </summary>
    internal Task<PeopleFactsSnapshotReader> CreatePeopleFactsSnapshotReaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default
    ) => PeopleFactsSnapshotReader.CreateAsync(connection, transaction, ct);

    internal sealed class PeopleFactsSnapshotReader : IDisposable
    {
        // This bounds retained encoded body/requirements, not returned facts or process RSS.
        // Oversize and eviction use the ordinary full read and never refuse a candidate.
        internal const int MaximumRetainedBodyBytes = 32 * 1024 * 1024;
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;
        private CachedPersonBody? lastBody;
        private bool disposed;

        internal static async Task<PeopleFactsSnapshotReader> CreateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct
        )
        {
            if (transaction.Connection == null)
            {
                throw new InvalidOperationException(
                    "A people snapshot reader requires a live transaction."
                );
            }
            if (
                !ReferenceEquals(transaction.Connection, connection)
                || transaction.IsolationLevel != IsolationLevel.RepeatableRead
            )
            {
                throw new ArgumentException(
                    "A people snapshot reader requires its exact repeatable-read connection and transaction."
                );
            }
            var readOnlySnapshot = await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT current_setting('transaction_read_only')='on' AND current_setting('transaction_isolation')='repeatable read'",
                    transaction: transaction,
                    cancellationToken: ct
                )
            );
            if (!readOnlySnapshot)
            {
                throw new ArgumentException(
                    "A people snapshot reader requires database-enforced READ ONLY and REPEATABLE READ."
                );
            }
            return new(connection, transaction);
        }

        private PeopleFactsSnapshotReader(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction
        )
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        // Diagnostic acquisition attempts, not a correctness or throughput certificate.
        internal long BodyAcquisitionAttempts { get; private set; }

        // Returned proof collections are read-only and may share complete body values across
        // queries. Consumers project new values; they must not mutate underlying collections.
        internal Task<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> FindAsync(
            NormalizationCandidateQuery query,
            CandidateFactsLimits? limits = null,
            CancellationToken ct = default
        )
        {
            var prepared = PrepareFactsQuery(query, IdentityMatchingDomain.People, limits);
            RequireLiveScope();
            return ReadFactsInSnapshotAsync(
                connection,
                transaction,
                prepared.Query,
                prepared.Names,
                prepared.Limits,
                ReadPeopleAsync,
                ct
            );
        }

        private async Task<PersonCandidateFacts[]> ReadPeopleAsync(
            NpgsqlConnection c,
            NpgsqlTransaction t,
            NormalizationCandidateSet selection,
            CandidateFactsLimits limits,
            CandidateFactsContext context,
            CancellationToken ct
        )
        {
            RequireLiveScope();
            var facts = new List<PersonCandidateFacts>();
            foreach (var hit in selection.Items)
            {
                ct.ThrowIfCancellationRequested();
                var cached = lastBody;
                if (cached != null && cached.Limits == limits && MatchesHeader(cached.Body, hit))
                {
                    context.Include(cached.Requirements);
                    facts.Add(cached.Body with { Hit = hit });
                    continue;
                }

                // Release the prior person before acquiring another. A query's own returned
                // proofs remain its responsibility; this reader retains only one extra entry.
                lastBody = null;
                BodyAcquisitionAttempts++;
                var bodyContext = new CandidateFactsContext();
                var body = await ReadPersonFactsAsync(c, t, hit, limits, bodyContext, ct);
                var requirements = bodyContext.CaptureRequirements();
                context.Include(requirements);
                facts.Add(body);
                RetainIfBounded(body, requirements, limits);
            }
            return facts.ToArray();
        }

        private void RetainIfBounded(
            PersonCandidateFacts body,
            CandidateFactsRequirements requirements,
            CandidateFactsLimits limits
        )
        {
            if (
                !MatchesHeader(body, body.Hit)
                || requirements.Issues.Any(issue =>
                    issue.Kind == CandidateFactsIssueKind.IdentityClosureLimit
                )
            )
            {
                // Overflow closure membership has no promised SQL ordering. Preserve the
                // normal read and diagnostic rather than treating a bounded subset as fixed.
                return;
            }
            var candidate = new CachedPersonBody(
                body with
                {
                    Hit = body.Hit with { NameSimilarity = 0, Reasons = [] },
                },
                requirements,
                limits
            );
            try
            {
                // Count without retaining another serialized copy. This budget is solely an
                // optimization: overflow must never become a semantic capacity exception.
                using var size = new BodyRetentionSizeStream();
                JsonSerializer.Serialize(size, candidate);
                lastBody = candidate;
            }
            catch (BodyRetentionExceededException)
            {
                // The complete body has already been returned; the next access rereads it.
            }
        }

        private static bool MatchesHeader(
            PersonCandidateFacts body,
            NormalizationCandidateHit hit
        ) =>
            body.Header.Uuid == hit.Uuid
            && body.Header.Revision == hit.Revision
            && body.Header.DisplayText == hit.DisplayText
            && body.Header.OwnerSubjectUuid == hit.OwnerSubjectUuid;

        private void RequireLiveScope()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(PeopleFactsSnapshotReader));
            }
            if (!ReferenceEquals(transaction.Connection, connection))
            {
                throw new InvalidOperationException(
                    "The people snapshot reader's original transaction has ended."
                );
            }
        }

        public void Dispose()
        {
            lastBody = null;
            disposed = true;
        }

        private sealed record CachedPersonBody(
            PersonCandidateFacts Body,
            CandidateFactsRequirements Requirements,
            CandidateFactsLimits Limits
        );

        private sealed class BodyRetentionExceededException : Exception { }

        private sealed class BodyRetentionSizeStream : Stream
        {
            private long bytes;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => bytes;
            public override long Position
            {
                get => bytes;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override void Write(byte[] buffer, int offset, int count) => Count(count);

            public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);

            private void Count(int count)
            {
                if (count > MaximumRetainedBodyBytes - bytes)
                {
                    throw new BodyRetentionExceededException();
                }
                bytes += count;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
