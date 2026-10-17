using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Data;

public sealed partial class ArchiveIntakeService
{
    /// <summary>
    /// Proposes an explicitly reviewed performing project, then uses ordinary cohort
    /// materialization when approved. Labels must name this project: broad creator labels
    /// belong to individual performer decisions, never to this shared vocabulary.
    /// A pre-existing catalog match remains deferred rather than creating a duplicate.
    /// </summary>
    public async Task<Guid> ProposeProjectAsync(
        string name,
        string slug,
        string[] reviewedLabels,
        bool approved,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (reviewedLabels.Length == 0 || reviewedLabels.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Reviewed project labels must be nonempty.",
                nameof(reviewedLabels)
            );
        }

        var keys = reviewedLabels
            .Append(name)
            .Select(ArtistRegistryService.NormalizeIdentityKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (
            ArtistRegistryService.NormalizeIdentityKey(name) != "JERRY GARCIA"
            && keys.Contains("JERRY GARCIA", StringComparer.Ordinal)
        )
        {
            throw new ArgumentException(
                "Jerry Garcia is an umbrella creator label, not an alias for another project.",
                nameof(reviewedLabels)
            );
        }
        var candidateId = await transactions.RunArtistRegistryAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var candidate = await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<CandidateRow>(
                        """
                        INSERT INTO normalization.archive_artist_candidates
                            (candidate_id,creator_key,proposed_name,proposed_slug,reviewed_creator_keys)
                        VALUES(@id,@key,@name,@slug,@keys)
                        ON CONFLICT(creator_key) DO UPDATE SET updated_at=now()
                        RETURNING *
                        """,
                        new
                        {
                            id = Guid.NewGuid(),
                            key = ArtistRegistryService.NormalizeIdentityKey(name),
                            name,
                            slug,
                            keys,
                        }
                    )
                );
                candidate = (await LockCandidateAsync(candidate.candidate_id))!;
                if (
                    (candidate.approved || candidate.artist_uuid != null)
                    && (
                        candidate.proposed_name != name
                        || candidate.proposed_slug != slug
                        || !candidate.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(keys)
                    )
                )
                {
                    throw new ArgumentException(
                        "Pause and review an existing project's identity before changing its labels."
                    );
                }

                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_artist_candidates
                        SET proposed_name=@name,proposed_slug=@slug,reviewed_creator_keys=@keys,
                            approved=@approved,updated_at=now()
                        WHERE candidate_id=@id
                        """,
                        new
                        {
                            id = candidate.candidate_id,
                            name,
                            slug,
                            keys,
                            approved,
                        }
                    )
                );
                candidate.proposed_name = name;
                candidate.proposed_slug = slug;
                candidate.reviewed_creator_keys = keys;
                await RefreshMatchesAsync(candidate);
                return candidate.candidate_id;
            }
        );

        // Materialization rechecks the live registry under its own transaction. A race
        // after proposal can defer the candidate, but cannot create a duplicate artist.
        if (approved)
        {
            await MaterializeArtistAsync(candidateId, token, deadline);
        }
        return candidateId;
    }

    private readonly ArchivePerformerReviewService performerReviews = new(db);
}
