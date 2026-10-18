using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistMaterializer
{
    private static long MeasureBoundedJson<T>(T value, long maximum)
    {
        // Decisions retain whole source assertions. Measure the actual escaped representation
        // without allocating a potentially quadratic JSON string before enforcing its limit.
        using var stream = new JsonSizeLimitStream(maximum);
        JsonSerializer.Serialize(stream, value);
        return stream.Length;
    }

    private static MusicMaterializationCapacityException WithCapacityOwner(
        MusicMaterializationCapacityException error,
        NormalizationInputContributionsSnapshot membership
    )
    {
        if (error.Owner != null || membership.Owner == null)
        {
            return error;
        }
        var owner = membership.Owner;
        static long Bindings(NormalizationInputContributionState state) =>
            state.Resources.Count
            + state.DependencySubjectUuids.Count
            + state.CatalogArtistUuids.Count
            + state.SourceIdentifiers.Count
            + (state.UsesMappingRegistry ? 1 : 0)
            + (state.ImportedVenueUuids?.Count ?? 0)
            + (state.CandidateUniverses?.Count ?? 0)
            + (state.OwnerObservationSubjectUuids?.Count ?? 0)
            + (state.SourceUuids?.Count ?? 0);
        var stamp = new NormalizationInputContributionsCapacityStamp(
            membership.Contributions.Count,
            membership.Contributions.Sum(Bindings),
            membership
                .Contributions.Sum(value => (decimal)value.Revision)
                .ToString(CultureInfo.InvariantCulture),
            membership.UnmanagedBaseline is { } baseline ? Bindings(baseline) : 0
        );
        return new(
            error.Message,
            new(
                owner.SubjectUuid,
                owner.DesiredGeneration,
                owner.EditorialRevision,
                owner.PublishedWorkUuid
            ),
            stamp
        );
    }

    private async Task<bool> CapacityOwnerChangedAsync(
        NormalizationSubjectObservation owner,
        NormalizationInputContributionsCapacityStamp? membership,
        CancellationToken ct
    )
    {
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: ct
            )
        );
        var changed = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT NOT EXISTS(SELECT 1 FROM normalization.work_subjects
                    WHERE uuid=@SubjectUuid AND desired_generation=@DesiredGeneration AND editorial_revision=@EditorialRevision
                        AND published_work_uuid IS NOT DISTINCT FROM @PublishedWorkUuid::uuid)
                """,
                owner,
                t,
                cancellationToken: ct
            )
        );
        if (!changed && membership != null)
        {
            changed =
                await NormalizationWorkService.ReadInputContributionCapacityStampAsync(
                    c,
                    t,
                    owner.SubjectUuid,
                    ct
                ) != membership;
        }
        await t.CommitAsync(ct);
        return changed;
    }

    private sealed class JsonSizeLimitStream(long maximum) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Count(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);

        private void Count(int count)
        {
            bytes += count;
            if (bytes > maximum)
            {
                throw new MusicMaterializationCapacityException(
                    "The complete encoded setlist materialization exceeds its capacity profile."
                );
            }
        }
    }
}
