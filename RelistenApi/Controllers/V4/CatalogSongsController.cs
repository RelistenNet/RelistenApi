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

namespace Relisten.Controllers.V4;

/// <summary>Artist song browsing, exact recording matches, and imported setlist observations.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 422)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 503)]
public sealed class CatalogSongsController(
    CatalogSongReader reader,
    ILogger<CatalogSongsController> logger
) : CatalogControllerBase(logger)
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
    [ProducesResponseType(typeof(SongDetailResource), 200)]
    public Task<IActionResult> Song(string songId, CancellationToken cancellationToken) =>
        ReadSong(
            songId,
            async () =>
            {
                var song = await reader.ReadSong(songId, cancellationToken);
                return song is null ? null : V4CatalogProjection.SongDetail(song);
            }
        );

    [HttpGet("songs/{songId}/tracks")]
    [ProducesResponseType(typeof(SongTrackMatchResource[]), 200)]
    public Task<IActionResult> Tracks(string songId, CancellationToken cancellationToken) =>
        ReadSong(
            songId,
            async () =>
                (await reader.ReadTracks(songId, cancellationToken))
                    ?.Select(V4CatalogProjection.SongTrackMatch)
                    .ToArray()
        );

    [HttpGet("songs/{songId}/shows")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200)]
    public Task<IActionResult> Shows(string songId, CancellationToken cancellationToken) =>
        ReadSong(
            songId,
            async () =>
                (await reader.ReadShows(songId, cancellationToken))
                    ?.Select(V4CatalogProjection.ShowSummary)
                    .ToArray()
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

    [HttpGet("artists/{artistId}/imported-songs/{importedSongId}")]
    [ProducesResponseType(typeof(ImportedSongWithShowsResource), 200)]
    public Task<IActionResult> ImportedSong(
        string artistId,
        string importedSongId,
        CancellationToken cancellationToken
    )
    {
        if (!ImportedSongId.TryParse(importedSongId, out var song))
        {
            return Task.FromResult<IActionResult>(InvalidId(ImportedSongId.Prefix));
        }
        return Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var detail = await reader.ReadImportedSong(artist, song.Value, cancellationToken);
                return detail is null ? null : V4CatalogProjection.ImportedSongWithShows(detail);
            }
        );
    }

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
