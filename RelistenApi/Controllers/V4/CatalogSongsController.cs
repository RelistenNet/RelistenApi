using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

/// <summary>Artist song browsing, exact recording matches, and imported setlist observations.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 422)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 410)]
[ProducesResponseType(typeof(ProblemDetails), 503)]
public sealed class CatalogSongsController(
    CatalogSongReader reader,
    PopularityRefreshRequest refresh,
    ILogger<CatalogSongsController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpGet("artists/{artistId}/songs")]
    [ProducesResponseType(typeof(ArtistSongEntryResource[]), 200)]
    public Task<IActionResult> ArtistSongs(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
                (await reader.ReadArtistSongs(artist, cancellationToken))
                    ?.Select(V4CatalogProjection.SongEntry)
                    .ToArray()
        );

    /// <summary>Finds an artist's canonical song by its stored slug.</summary>
    /// <param name="includeTracks">none omits show tracks; top lists each show's top-ranked
    /// matching source; all lists every matching file.</param>
    /// <remarks>
    /// A merged song's slug returns the survivor, including its current ID and slug.
    /// Imported song slugs are not accepted; resolve them with the reference resolver.
    /// </remarks>
    [HttpGet("artists/{artistId}/songs/by-slug/{slug}")]
    [ProducesResponseType(typeof(SongWithShowsResource), 200)]
    public Task<IActionResult> ArtistSongBySlug(
        string artistId,
        string slug,
        [FromQuery(Name = "include_tracks"), AllowedValues("none", "top", "all")]
            string includeTracks = "none",
        CancellationToken cancellationToken = default
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var song = await reader.ReadArtistSongBySlug(
                    artist,
                    slug,
                    ShowTracks(includeTracks),
                    cancellationToken
                );
                return song is null ? null : V4CatalogProjection.SongWithShows(song);
            }
        );

    /// <param name="includeTracks">none omits show tracks; top lists each show's top-ranked
    /// matching source; all lists every matching file.</param>
    [HttpGet("songs/{songId}")]
    [ProducesResponseType(typeof(SongWithShowsResource), 200)]
    public Task<IActionResult> Song(
        string songId,
        [FromQuery(Name = "include_tracks"), AllowedValues("none", "top", "all")]
            string includeTracks = "none",
        CancellationToken cancellationToken = default
    ) =>
        ReadSong(
            songId,
            async () =>
            {
                var song = await reader.ReadSong(
                    songId,
                    ShowTracks(includeTracks),
                    cancellationToken
                );
                return song is null ? null : V4CatalogProjection.SongWithShows(song);
            }
        );

    [HttpGet("songs/{songId}/tracks")]
    [ProducesResponseType(typeof(SongWithTracksResource), 200)]
    public Task<IActionResult> Tracks(string songId, CancellationToken cancellationToken) =>
        ReadSong(
            songId,
            async () =>
            {
                var detail = await reader.ReadTracks(songId, cancellationToken);
                return detail is null ? null : V4CatalogProjection.SongWithTracks(detail);
            }
        );

    [HttpGet("artists/{artistId}/imported-songs")]
    [ProducesResponseType(typeof(ImportedSongResource[]), 200)]
    public Task<IActionResult> ImportedSongs(
        string artistId,
        CancellationToken cancellationToken
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
                (await reader.ReadImportedSongs(artist, cancellationToken))
                    ?.Select(V4CatalogProjection.ImportedSong)
                    .ToArray()
        );

    [HttpGet("imported-songs/{importedSongId}")]
    [ProducesResponseType(typeof(ImportedSongWithShowsResource), 200)]
    public Task<IActionResult> ImportedSong(
        string importedSongId,
        CancellationToken cancellationToken
    ) =>
        Read(
            importedSongId,
            ImportedSongId.Prefix,
            async song =>
            {
                var detail = await reader.ReadImportedSong(song, cancellationToken);
                return detail is null ? null : V4CatalogProjection.ImportedSongWithShows(detail);
            }
        );

    private Task<IActionResult> ReadSong<T>(string songId, Func<Task<T?>> read)
        where T : class =>
        IsSongId(songId) ? Execute(read) : Task.FromResult<IActionResult>(InvalidId(SongId.Prefix));

    private static CatalogSongShowTracks ShowTracks(string includeTracks) =>
        includeTracks switch
        {
            "top" => CatalogSongShowTracks.Top,
            "all" => CatalogSongShowTracks.All,
            _ => CatalogSongShowTracks.None,
        };

    private static bool IsSongId(string value)
    {
        try
        {
            SongId.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
