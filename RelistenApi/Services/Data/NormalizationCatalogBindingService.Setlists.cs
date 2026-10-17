using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private const int MaximumSetlistArtists = 10_000;

    /// <summary>
    /// Associates one retained account with current catalog identities. The supplied revision must
    /// match immutable retained bytes, and decoding must reproduce the exact account. Page members
    /// retain their page input key; this method does not claim they are the latest account edition.
    /// </summary>
    /// <remarks>
    /// Jerry, Setlist.fm and Phantasy Tour qualify artists globally before expected-artist checking.
    /// Phish.net requires expectedArtistUuid and inspects only that artist's recorded provider
    /// associations. ArtistScopeUuid makes this narrower scope explicit. No lookup uses a date,
    /// title, slug or position to invent a performance, repertoire or composition assignment.
    /// Include Inputs, CatalogInputs and MappingRegistry in exact work admission. Historical input
    /// inspection is allowed; work admission must reject a replaced current input revision.
    /// </remarks>
    public async Task<NormalizationSetlistBindingSnapshot> BindSetlistAsync(
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Uuid == Guid.Empty || expectedArtistUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Retained input and optional artist UUIDs must be nonempty."
            );
        }
        if (System.Transactions.Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Read setlist bindings outside a catalog transaction."
            );
        }
        var (retained, reproducedAccount) = await RequireRetainedSetlistAccountAsync(
            input,
            account,
            cancellationToken
        );
        return await WithCatalogSnapshotAsync(
            async (c, t, ct) =>
            {
                var registry = await c.QuerySingleAsync<NormalizationMappingRegistryDependency>(
                    new CommandDefinition(
                        "SELECT generation AS Generation FROM normalization.catalog_mapping_registry_revision WHERE singleton",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                return retained.Resource.Provider switch
                {
                    "jerrygarcia.com" => await BindJerrySetlistSnapshotAsync(
                        c,
                        t,
                        retained,
                        reproducedAccount,
                        registry,
                        expectedArtistUuid,
                        ct
                    ),
                    "setlist.fm" => await BindSetlistFmSnapshotAsync(
                        c,
                        t,
                        retained,
                        reproducedAccount,
                        registry,
                        expectedArtistUuid,
                        ct
                    ),
                    "phantasytour.com" => await BindPhantasyTourSetlistSnapshotAsync(
                        c,
                        t,
                        retained,
                        reproducedAccount,
                        registry,
                        expectedArtistUuid,
                        ct
                    ),
                    "phish.net" => await BindPhishNetSetlistSnapshotAsync(
                        c,
                        t,
                        retained,
                        reproducedAccount,
                        registry,
                        expectedArtistUuid,
                        ct
                    ),
                    _ => throw new NotSupportedException(
                        "This provider has no setlist catalog association contract."
                    ),
                };
            },
            cancellationToken
        );
    }

    private async Task<(
        NormalizationInputRevision Input,
        ProviderSetlistObservation Account
    )> RequireRetainedSetlistAccountAsync(
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        CancellationToken ct
    )
    {
        var retained = await new NormalizationEvidenceService(db).GetInputAsync(input.Uuid, ct);
        if (
            retained == null
            || retained.Resource != input.Resource
            || retained.MediaType != input.MediaType
            || retained.CaptureVersion != input.CaptureVersion
            || !retained.Payload.AsSpan().SequenceEqual(input.Payload)
        )
        {
            throw new ArgumentException(
                "Setlist input does not match an immutable retained revision."
            );
        }
        var accounts = new NormalizationProviderDecoder()
            .Decode(retained, ct)
            .Setlists.Where(candidate => candidate.Origin == account.Origin)
            .ToArray();
        if (
            accounts.Length != 1
            || !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(accounts[0]),
                JsonSerializer.SerializeToElement(account)
            )
        )
        {
            throw new ArgumentException(
                "The setlist account cannot be reproduced from this retained revision."
            );
        }
        // IReadOnlyList does not make caller-owned arrays immutable. Continue with the decoded
        // account so later awaits cannot let a caller replace a verified performer or identity.
        return (retained, accounts[0]);
    }

    private static async Task<NormalizationSetlistBindingSnapshot> BindImportedSetlistSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        SetlistArtistRow[] artists,
        string legacyIdentifier,
        NormalizationMappingRegistryDependency registry,
        Guid? expectedArtistUuid,
        CancellationToken ct
    )
    {
        var artist = SetlistArtistBinding(artists, expectedArtistUuid);
        SetlistCatalogRow? row = null;
        if (artist.Status == NormalizationCatalogBindingStatus.Bound)
        {
            row = await c.QuerySingleOrDefaultAsync<SetlistCatalogRow>(
                new CommandDefinition(
                    """
                    SELECT sl.uuid AS SetlistShowUuid,v.uuid AS VenueUuid,va.uuid AS VenueArtistUuid,
                        sh.uuid AS ShowUuid,sa.uuid AS ShowArtistUuid
                    FROM setlist_shows sl LEFT JOIN venues v ON v.id=sl.venue_id
                    LEFT JOIN artists va ON va.id=v.artist_id
                    LEFT JOIN setlist_show_assignments assignment ON assignment.setlist_show_id=sl.id
                    LEFT JOIN shows sh ON sh.id=assignment.show_id LEFT JOIN artists sa ON sa.id=sh.artist_id
                    WHERE sl.artist_id=@artistId AND sl.upstream_identifier=@legacyIdentifier
                    """,
                    new { artistId = artists[0].ArtistId, legacyIdentifier },
                    t,
                    cancellationToken: ct
                )
            );
        }
        var clockArtists = artists
            .Select(candidate => candidate.ArtistUuid)
            .Concat(new[] { row?.VenueArtistUuid, row?.ShowArtistUuid }.OfType<Guid>());
        var clocks = await ReadSetlistArtistClocksAsync(c, t, clockArtists, ct);
        var imported =
            artist.Status != NormalizationCatalogBindingStatus.Bound
                ? new NormalizationCatalogBinding<NormalizationCatalogSetlistReference>(
                    artist.Status,
                    null,
                    "artist_unresolved"
                )
            : row == null
                ? new(NormalizationCatalogBindingStatus.Missing, null, "missing_imported_setlist")
            : new(
                NormalizationCatalogBindingStatus.Bound,
                new(artists[0].ArtistUuid, row.SetlistShowUuid, row.VenueUuid),
                "catalog_setlist"
            );
        var performance = ImportedSetlistPerformance(artist, row);
        return new(
            input.Uuid,
            input.Resource,
            account.Origin,
            artist,
            imported,
            performance,
            clocks,
            registry
        );
    }

    private static NormalizationCatalogBinding<NormalizationCatalogPerformanceReference> ImportedSetlistPerformance(
        NormalizationCatalogBinding<NormalizationCatalogArtistReference> artist,
        SetlistCatalogRow? row
    )
    {
        if (artist.Status != NormalizationCatalogBindingStatus.Bound || row == null)
        {
            return new(
                artist.Status == NormalizationCatalogBindingStatus.Bound
                    ? NormalizationCatalogBindingStatus.Missing
                    : artist.Status,
                null,
                "imported_setlist_unresolved"
            );
        }
        if (row.ShowUuid == null)
        {
            return new(
                NormalizationCatalogBindingStatus.Missing,
                null,
                "missing_setlist_assignment"
            );
        }
        if (row.ShowArtistUuid != artist.Value!.ArtistUuid)
        {
            return new(
                NormalizationCatalogBindingStatus.ProviderConflict,
                null,
                "performance_artist_conflict"
            );
        }
        return new(
            NormalizationCatalogBindingStatus.Bound,
            new(artist.Value.ArtistUuid, row.ShowUuid.Value, [], "existing_setlist_assignment"),
            "setlist_assignment"
        );
    }

    private static NormalizationCatalogBinding<NormalizationCatalogArtistReference> SetlistArtistBinding(
        SetlistArtistRow[] artists,
        Guid? expectedArtistUuid
    )
    {
        if (artists.Length > MaximumSetlistArtists)
        {
            throw new InvalidOperationException("Setlist artist lookup exceeds its limit.");
        }
        if (artists.Length == 0)
        {
            return new(NormalizationCatalogBindingStatus.Missing, null, "missing_artist_mapping");
        }
        if (artists.Length > 1)
        {
            return new(
                NormalizationCatalogBindingStatus.Ambiguous,
                null,
                "ambiguous_artist_mapping"
            );
        }
        if (expectedArtistUuid != null && expectedArtistUuid != artists[0].ArtistUuid)
        {
            return new(
                NormalizationCatalogBindingStatus.ProviderConflict,
                null,
                "unexpected_artist"
            );
        }
        return new(
            NormalizationCatalogBindingStatus.Bound,
            new(artists[0].ArtistUuid),
            "mapped_artist"
        );
    }

    private static async Task<NormalizationCatalogDependency[]> ReadSetlistArtistClocksAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IEnumerable<Guid> artistUuids,
        CancellationToken ct
    )
    {
        var artists = artistUuids.Distinct().ToArray();
        var clocks = (
            await c.QueryAsync<NormalizationCatalogDependency>(
                new CommandDefinition(
                    "SELECT artist_uuid AS ArtistUuid,generation AS Generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=ANY(@artists) ORDER BY artist_uuid",
                    new { artists },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (clocks.Length != artists.Length)
        {
            throw new InvalidOperationException(
                "Setlist associations have an unfenced catalog artist."
            );
        }
        return clocks;
    }

    private sealed record SetlistArtistRow(int ArtistId, Guid ArtistUuid);

    private sealed class SetlistCatalogRow
    {
        public Guid SetlistShowUuid { get; set; }
        public Guid? VenueUuid { get; set; }
        public Guid? VenueArtistUuid { get; set; }
        public Guid? ShowUuid { get; set; }
        public Guid? ShowArtistUuid { get; set; }
    }
}
