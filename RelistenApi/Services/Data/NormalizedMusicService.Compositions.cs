using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private static Task SaveComposition(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompositionChange composition,
        Guid decisionUuid,
        bool reviewed,
        CancellationToken ct
    ) =>
        Persist(
            c,
            t,
            "compositions",
            composition,
            "display_title,aliases",
            "@DisplayTitle,@Aliases",
            decisionUuid,
            reviewed,
            ct
        );

    private static async Task SaveWorkCredit(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        WorkCreditChange credit,
        Guid decisionUuid,
        bool reviewed,
        CancellationToken ct
    )
    {
        if (credit.PersonUuid.HasValue && credit.ArtistUuid.HasValue)
        {
            throw new ArgumentException(
                "A work credit resolves to a person or artist, never both."
            );
        }
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.work_credits WHERE uuid=@Uuid AND composition_uuid<>@CompositionUuid)",
            credit,
            "A work credit identity cannot move to another composition.",
            ct
        );
        await RequireUnredirected(c, t, "compositions", credit.CompositionUuid, ct);
        if (credit.PersonUuid is { } person)
        {
            credit = credit with
            {
                PersonUuid = await NormalizedPeopleService.ResolvePersonUuidAsync(c, t, person, ct),
            };
        }
        if (credit.ArtistUuid is { } artist)
        {
            await RequireCatalogBinding(
                c,
                t,
                "work_credits",
                credit.Uuid,
                "artist_uuid",
                artist,
                "artists",
                ct
            );
        }
        await Persist(
            c,
            t,
            "work_credits",
            credit,
            "composition_uuid,role,original_text,person_uuid,artist_uuid",
            "@CompositionUuid,@Role,@OriginalText,@PersonUuid,@ArtistUuid",
            decisionUuid,
            reviewed,
            ct
        );
    }

    private static async Task SaveCompositionLink(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireCompositionChange link,
        Guid decisionUuid,
        bool reviewed,
        CancellationToken ct
    )
    {
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.repertoire_compositions WHERE uuid=@Uuid AND repertoire_song_uuid<>@RepertoireSongUuid)",
            link,
            "A composition assignment cannot move to another repertoire identity.",
            ct
        );
        await RequireUnredirected(c, t, "repertoire_songs", link.RepertoireSongUuid, ct);
        await RequireUnredirected(c, t, "compositions", link.CompositionUuid, ct);
        if (!reviewed && link.MatchReason is not ("qualified_work_identity" or "writer_evidence"))
        {
            throw new ArgumentException(
                "A composition match needs qualified work identity or writer evidence; a title cannot establish it."
            );
        }
        if (!reviewed && link.MatchReason == "qualified_work_identity")
        {
            var identity =
                link.MatchedWorkIdentity
                ?? throw new ArgumentException(
                    "A work link must name the qualified identity observed in its evidence."
                );
            await Require(
                c,
                t,
                """
                SELECT EXISTS(SELECT 1 FROM normalization.music_external_identities WHERE composition_uuid=@uuid AND NOT retracted
                    AND provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey)
                """,
                new
                {
                    uuid = link.CompositionUuid,
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey,
                },
                "The observed external identity does not identify this composition.",
                ct
            );
        }
        if (!reviewed && link.MatchReason == "writer_evidence")
        {
            if (!link.CompleteAuthorship || link.MatchedAuthorUuids.Count == 0)
            {
                throw new ArgumentException(
                    "A writer match requires a complete, resolved author set observed in its supporting evidence."
                );
            }
            var authors = (
                await c.QueryAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT COALESCE(person_uuid,artist_uuid) FROM normalization.work_credits WHERE composition_uuid=@uuid AND role IN ('writer','composer','lyricist') AND NOT retracted",
                        new { uuid = link.CompositionUuid },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (
                authors.Length == 0
                || authors.Any(x => !x.HasValue)
                || !authors.Select(x => x!.Value).ToHashSet().SetEquals(link.MatchedAuthorUuids)
            )
            {
                throw new ArgumentException(
                    "The observed complete author set does not establish this composition."
                );
            }
        }
        await Persist(
            c,
            t,
            "repertoire_compositions",
            link,
            "repertoire_song_uuid,composition_uuid",
            "@RepertoireSongUuid,@CompositionUuid",
            decisionUuid,
            reviewed,
            ct
        );
    }
}
