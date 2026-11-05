using System;
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

    [HttpGet("songs/{songId}")]
    [ProducesResponseType(typeof(SongWithShowsResource), 200)]
    public Task<IActionResult> Song(
        string songId,
        [FromQuery(Name = "include_tracks")] bool includeTracks = false,
        CancellationToken cancellationToken = default
    ) =>
        ReadSong(
            songId,
            async () =>
            {
                var song = await reader.ReadSong(songId, includeTracks, cancellationToken);
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
        where T : class
    {
        try
        {
            SongId.Parse(songId);
        }
        catch (FormatException)
        {
            return Task.FromResult<IActionResult>(InvalidId(SongId.Prefix));
        }
        return Execute(read);
    }
}
