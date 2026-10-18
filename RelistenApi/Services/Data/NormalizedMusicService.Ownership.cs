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
    /// <summary>
    /// Dictionary corrections fence one artist; passage corrections fence one edition; contents
    /// fence one recording; global work edits fence one composition. Cross-owner work is phased.
    /// References consume those owners as dependencies instead of acquiring editorial authority.
    /// </summary>
    private static async Task ValidateOwnership(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid subject,
        MusicPublication publication,
        CancellationToken ct
    )
    {
        var owner = await c.QuerySingleAsync<MusicOwner>(
            new CommandDefinition(
                "SELECT domain AS Domain,subject_kind AS Kind,subject_uuid AS EntityUuid FROM normalization.work_subjects WHERE uuid=@subject",
                new { subject },
                t,
                cancellationToken: ct
            )
        );
        if (owner.Domain == "songs" && owner.Kind == "artist_repertoire")
        {
            if (
                publication.Compositions.Count
                    + publication.WorkCredits.Count
                    + (publication.AuthorshipAssertions?.Count ?? 0)
                    + publication.Editions.Count
                    + publication.Occurrences.Count
                    + publication.TrackContents.Count
                    + (publication.SetlistStructure == null ? 0 : 1)
                > 0
            )
            {
                throw new ArgumentException(
                    "An artist repertoire owner cannot edit compositions, editions or recordings."
                );
            }
            foreach (var song in publication.RepertoireSongs)
            {
                EnsureOwner(song.ArtistUuid, owner);
            }
            foreach (
                var song in publication
                    .ImportedSongs.Select(x => x.RepertoireSongUuid)
                    .Concat(publication.CompositionLinks.Select(x => x.RepertoireSongUuid))
                    .Concat(
                        publication
                            .ExternalIdentities.Where(x => x.RepertoireSongUuid.HasValue)
                            .Select(x => x.RepertoireSongUuid!.Value)
                    )
            )
            {
                EnsureOwner(await SongArtist(c, t, song, publication, ct), owner);
            }
            if (publication.ExternalIdentities.Any(x => x.CompositionUuid.HasValue))
            {
                throw new ArgumentException(
                    "A repertoire owner cannot edit a global external work identity."
                );
            }
            foreach (var merge in publication.Merges)
            {
                if (merge.Domain != "songs")
                {
                    throw new ArgumentException(
                        "A repertoire owner only merges songs in its artist."
                    );
                }
                EnsureOwner(await SongArtist(c, t, merge.RetiredUuid, publication, ct), owner);
                EnsureOwner(await SongArtist(c, t, merge.SurvivorUuid, publication, ct), owner);
            }
            foreach (var split in publication.Splits)
            {
                if (split.Domain != "songs")
                {
                    throw new ArgumentException(
                        "A repertoire owner only splits songs in its artist."
                    );
                }
                EnsureOwner(await SongArtist(c, t, split.RetainedUuid, publication, ct), owner);
            }
            return;
        }
        if (owner.Domain == "songs" && owner.Kind == "setlist")
        {
            EnsureOnly(publication, allowEditions: true, allowOccurrences: true);
            foreach (var edition in publication.Editions)
            {
                EnsureOwner(edition.Uuid, owner);
            }
            if (publication.SetlistStructure is { } structure)
            {
                EnsureOwner(structure.Edition.Uuid, owner);
            }
            foreach (var occurrence in publication.Occurrences)
            {
                EnsureOwner(occurrence.EditionUuid, owner);
            }
            return;
        }
        if (owner.Domain == "songs" && owner.Kind == "recording")
        {
            EnsureOnly(publication, allowContents: true);
            foreach (var content in publication.TrackContents)
            {
                // Current catalog ownership wins after a file moves. Retained ownership
                // only permits historical correction when the catalog reference is absent.
                var source = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        """
                        SELECT COALESCE(
                            (SELECT s.uuid FROM source_tracks tr JOIN sources s ON s.id=tr.source_id WHERE tr.uuid=@SourceTrackUuid),
                            (SELECT source_uuid FROM normalization.track_contents WHERE uuid=@Uuid AND source_track_uuid=@SourceTrackUuid))
                        """,
                        content,
                        t,
                        cancellationToken: ct
                    )
                );
                EnsureOwner(source ?? Guid.Empty, owner);
            }
            return;
        }
        if (owner.Domain == "compositions" && owner.Kind == "composition")
        {
            if (
                publication.RepertoireSongs.Count
                    + publication.ImportedSongs.Count
                    + publication.CompositionLinks.Count
                    + publication.Editions.Count
                    + publication.Occurrences.Count
                    + publication.TrackContents.Count
                    + (publication.SetlistStructure == null ? 0 : 1)
                    > 0
                || publication.ExternalIdentities.Any(x => x.RepertoireSongUuid.HasValue)
            )
            {
                throw new ArgumentException(
                    "A composition owner cannot edit artist repertoire, editions or recordings."
                );
            }
            var splitTargets = publication
                .Splits.Where(x => x.Domain == "compositions")
                .SelectMany(x => x.Targets ?? [])
                .Select(x => x.Uuid)
                .ToHashSet();
            foreach (
                var uuid in publication
                    .Compositions.Select(x => x.Uuid)
                    .Concat(publication.WorkCredits.Select(x => x.CompositionUuid))
                    .Concat((publication.AuthorshipAssertions ?? []).Select(x => x.CompositionUuid))
                    .Concat(
                        publication.ExternalIdentities.Select(x => x.CompositionUuid ?? Guid.Empty)
                    )
            )
            {
                if (!splitTargets.Contains(uuid))
                {
                    EnsureOwner(uuid, owner);
                }
            }
            foreach (var merge in publication.Merges)
            {
                if (merge.Domain != "compositions")
                {
                    throw new ArgumentException(
                        "A composition owner only merges its own global work."
                    );
                }
                EnsureOwner(merge.RetiredUuid, owner);
            }
            foreach (var split in publication.Splits)
            {
                if (split.Domain != "compositions")
                {
                    throw new ArgumentException(
                        "A composition owner only splits its own global work."
                    );
                }
                EnsureOwner(split.RetainedUuid, owner);
            }
            return;
        }
        throw new ArgumentException(
            "Unsupported music owner. Use artist_repertoire, setlist, recording or composition."
        );
    }

    private static void EnsureOnly(
        MusicPublication p,
        bool allowEditions = false,
        bool allowOccurrences = false,
        bool allowContents = false
    )
    {
        if (
            p.RepertoireSongs.Count
                + p.Compositions.Count
                + p.ImportedSongs.Count
                + p.CompositionLinks.Count
                + p.WorkCredits.Count
                + (p.AuthorshipAssertions?.Count ?? 0)
                + p.ExternalIdentities.Count
                + p.Merges.Count
                + p.Splits.Count
                > 0
            || (!allowEditions && p.Editions.Count > 0)
            || (!allowOccurrences && p.Occurrences.Count > 0)
            || (!allowContents && p.TrackContents.Count > 0)
            || (!allowEditions && p.SetlistStructure != null)
        )
        {
            throw new ArgumentException(
                "The publication includes a change owned by another domain subject."
            );
        }
    }

    private static void EnsureOwner(Guid actual, MusicOwner owner)
    {
        if (actual != owner.EntityUuid)
        {
            throw new ArgumentException(
                "The music decision owner does not match the changed entity scope."
            );
        }
    }

    private static async Task<Guid> SongArtist(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid song,
        MusicPublication publication,
        CancellationToken ct
    ) =>
        publication.RepertoireSongs.SingleOrDefault(x => x.Uuid == song)?.ArtistUuid
        ?? await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT artist_uuid FROM normalization.repertoire_songs WHERE uuid=@song",
                new { song },
                t,
                cancellationToken: ct
            )
        )
        ?? throw new ArgumentException("The referenced repertoire identity is unavailable.");

    private static async Task ValidateDependencies(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationWorkTicket ticket,
        MusicPublication publication,
        CancellationToken ct,
        SetlistMaterializationPublicationAuthority? authenticatedSetlistAttempt = null
    )
    {
        var required = new HashSet<(string Domain, string Kind, Guid Uuid)>();
        if (publication.SetlistStructure is { } structure)
        {
            await ValidateStructureReadSet(
                c,
                t,
                ticket,
                structure,
                ct,
                authenticatedSetlistAttempt
            );
        }
        foreach (var link in publication.CompositionLinks)
        {
            required.Add(("compositions", "composition", link.CompositionUuid));
        }
        foreach (
            var song in publication
                .Occurrences.Where(x => x.RepertoireSongUuid.HasValue)
                .Select(x => x.RepertoireSongUuid!.Value)
                .Concat(
                    publication
                        .TrackContents.Where(x => x.RepertoireSongUuid.HasValue)
                        .Select(x => x.RepertoireSongUuid!.Value)
                )
        )
        {
            required.Add(
                ("songs", "artist_repertoire", await SongArtist(c, t, song, publication, ct))
            );
        }
        foreach (
            var occurrence in publication
                .TrackContents.Where(x => x.OccurrenceUuid.HasValue)
                .Select(x => x.OccurrenceUuid!.Value)
        )
        {
            var edition = await c.QuerySingleAsync<Guid>(
                new CommandDefinition(
                    "SELECT edition_uuid FROM normalization.song_occurrences WHERE uuid=@occurrence",
                    new { occurrence },
                    t,
                    cancellationToken: ct
                )
            );
            required.Add(("songs", "setlist", edition));
        }
        foreach (var credit in publication.WorkCredits.Where(x => x.PersonUuid.HasValue))
        {
            var person = await NormalizedPeopleService.ResolvePersonUuidAsync(
                c,
                t,
                credit.PersonUuid!.Value,
                ct
            );
            required.Add(("people", "person", person));
        }
        foreach (
            var member in (publication.AuthorshipAssertions ?? [])
                .SelectMany(x => x.Authors)
                .Where(x => x.PersonUuid.HasValue)
        )
        {
            var person = await NormalizedPeopleService.ResolvePersonUuidAsync(
                c,
                t,
                member.PersonUuid!.Value,
                ct
            );
            required.Add(("people", "person", person));
        }
        foreach (
            var uuid in publication
                .CompositionLinks.Where(link => link.MatchReason == "writer_evidence")
                .SelectMany(link => link.MatchedAuthorUuids)
        )
        {
            var person = await NormalizedPeopleService.ResolvePersonUuidAsync(c, t, uuid, ct);
            required.Add(("people", "person", person));
        }
        foreach (var merge in publication.Merges.Where(x => x.Domain == "compositions"))
        {
            required.Add(("compositions", "composition", merge.SurvivorUuid));
        }
        var dependencies = ticket.Dependencies.Select(x => x.SubjectUuid).ToArray();
        foreach (var dependency in required)
        {
            await Require(
                c,
                t,
                """
                SELECT EXISTS(SELECT 1 FROM normalization.work_subjects WHERE uuid=ANY(@dependencies)
                    AND domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid)
                """,
                new
                {
                    dependencies,
                    dependency.Domain,
                    dependency.Kind,
                    dependency.Uuid,
                },
                "A referenced identity owner is absent from the consumed dependency set.",
                ct
            );
        }
    }

    private static IEnumerable<Guid> ReferencedIdentities(MusicPublication p) =>
        p
            .ImportedSongs.Select(x => x.RepertoireSongUuid)
            .Concat(
                p.CompositionLinks.SelectMany(x =>
                    new[] { x.RepertoireSongUuid, x.CompositionUuid }
                )
            )
            .Concat(p.WorkCredits.Select(x => x.CompositionUuid))
            .Concat((p.AuthorshipAssertions ?? []).Select(x => x.CompositionUuid))
            .Concat(p.Splits.SelectMany(x => x.Targets ?? []).Select(x => x.Uuid))
            .Concat(p.SetlistStructure?.Sets.Select(set => set.Uuid) ?? [])
            .Concat(
                p.Occurrences.SelectMany(x =>
                        new Guid?[]
                        {
                            x.EditionUuid,
                            x.RepertoireSongUuid,
                            x.ParentOccurrenceUuid,
                            x.SupersedesOccurrenceUuid,
                        }
                    )
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
            )
            .Concat(
                p.TrackContents.SelectMany(x => new[] { x.RepertoireSongUuid, x.OccurrenceUuid })
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
            )
            .Concat(
                p.ExternalIdentities.Select(x =>
                    x.RepertoireSongUuid ?? x.CompositionUuid ?? Guid.Empty
                )
            );

    private sealed record MusicOwner(string Domain, string Kind, Guid EntityUuid);
}
