using System;
using System.Collections.Generic;
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
        CancellationToken ct,
        bool forceRevision = false
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
            ct,
            forceRevision: forceRevision
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
        if (!reviewed && link.EvaluatedCorrespondenceReceiptUuid != null)
        {
            throw new ArgumentException(
                "Evaluated correspondence receipts require the authenticated processor publication route."
            );
        }
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
            var matched = new HashSet<Guid>();
            foreach (var author in link.MatchedAuthorUuids)
            {
                matched.Add(await NormalizedPeopleService.ResolvePersonUuidAsync(c, t, author, ct));
            }
            var authors = await ReadCompleteCompositionAuthors(c, t, link.CompositionUuid, ct);
            if (authors == null || !authors.SetEquals(matched))
            {
                throw new ArgumentException(
                    "Consistent complete source author assertions do not establish this composition."
                );
            }
            link = link with { MatchedAuthorUuids = matched.Order().ToArray() };
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
