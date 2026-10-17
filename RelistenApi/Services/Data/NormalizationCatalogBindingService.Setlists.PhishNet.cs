using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private const int MaximumPhishNetAssociations = 10_000;
    private const int PhishNetAssociationPageSize = 1_000;

    private static async Task<NormalizationSetlistBindingSnapshot> BindPhishNetSetlistSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        NormalizationMappingRegistryDependency registry,
        Guid? expectedArtistUuid,
        CancellationToken ct
    )
    {
        // Phish.net mappings currently have no provider artist identifier. An explicit catalog
        // scope and its stored provider evidence establish an association, not global uniqueness.
        if (expectedArtistUuid == null)
        {
            throw new ArgumentException(
                "Phish.net setlist binding requires an explicit catalog artist scope.",
                nameof(expectedArtistUuid)
            );
        }
        var artists = (
            await c.QueryAsync<SetlistArtistRow>(
                new CommandDefinition(
                    "SELECT id AS ArtistId,uuid AS ArtistUuid FROM artists WHERE uuid=@expectedArtistUuid",
                    new { expectedArtistUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var artist =
            artists.Length == 0
                ? new NormalizationCatalogBinding<NormalizationCatalogArtistReference>(
                    NormalizationCatalogBindingStatus.Missing,
                    null,
                    "missing_scoped_artist"
                )
                : new(
                    NormalizationCatalogBindingStatus.Bound,
                    new(artists[0].ArtistUuid),
                    "scoped_catalog_artist"
                );
        var accountType = account.Origin.Item.ResourceType;
        var accountKey = account.Origin.Item.ProviderKey;
        var validShowId =
            int.TryParse(
                accountKey,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var providerShowId
            )
            && providerShowId > 0;
        var invalidShowId = accountType == "show" && !validShowId;
        if (accountType == "show" && validShowId)
        {
            accountKey = providerShowId.ToString(CultureInfo.InvariantCulture);
        }
        PhishNetAssociationRow[] rows =
            artists.Length == 0
                ? []
                : await ReadPhishNetAssociationsAsync(
                    c,
                    t,
                    artists[0].ArtistId,
                    accountType,
                    accountKey,
                    ct
                );
        var clocks = await ReadSetlistArtistClocksAsync(
            c,
            t,
            artists
                .Select(row => row.ArtistUuid)
                .Concat(rows.Select(row => row.ShowArtistUuid).OfType<Guid>()),
            ct
        );
        var performance = invalidShowId
            ? new NormalizationCatalogBinding<NormalizationCatalogPerformanceReference>(
                NormalizationCatalogBindingStatus.ProviderConflict,
                null,
                "invalid_provider_account_identity"
            )
            : PhishNetPerformance(artist, rows);
        return new(
            input.Uuid,
            input.Resource,
            account.Origin,
            artist,
            new(NormalizationCatalogBindingStatus.Missing, null, "not_represented_in_catalog"),
            performance,
            clocks,
            registry,
            ArtistScopeUuid: expectedArtistUuid
        );
    }

    private static async Task<PhishNetAssociationRow[]> ReadPhishNetAssociationsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        int artistId,
        string accountType,
        string accountKey,
        CancellationToken ct
    )
    {
        var matches = new List<PhishNetAssociationRow>();
        Guid? afterSourceUuid = null;
        while (true)
        {
            var page = await ReadPhishNetAssociationPageAsync(
                c,
                t,
                artistId,
                accountType,
                accountKey,
                afterSourceUuid,
                ct
            );
            // Stored URLs need the same official-host/path interpretation as decoded HTML.
            // Scan every keyset page in this snapshot; unrelated evidence does not consume the
            // matching-account limit, and an over-limit account never returns a partial result.
            matches.AddRange(
                accountType == "setlist_page"
                    ? page.Where(row => PhishNetAssociationPath(row.Permalink) == accountKey)
                    : page
            );
            if (matches.Count > MaximumPhishNetAssociations)
            {
                throw new InvalidOperationException(
                    "Phish.net scoped association lookup exceeds its matching-account limit."
                );
            }
            if (page.Length < PhishNetAssociationPageSize)
            {
                return matches.ToArray();
            }
            afterSourceUuid = page[^1].SourceUuid;
        }
    }

    private static async Task<PhishNetAssociationRow[]> ReadPhishNetAssociationPageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        int artistId,
        string accountType,
        string accountKey,
        Guid? afterSourceUuid,
        CancellationToken ct
    )
    {
        var rows = await c.QueryAsync<PhishNetAssociationRow>(
            new CommandDefinition(
                """
                SELECT src.uuid AS SourceUuid,src.show_id AS SourceShowId,assignment.show_id AS AssignmentShowId,
                    assignment.assignment_kind AS AssignmentKind,sh.uuid AS ShowUuid,sa.uuid AS ShowArtistUuid,
                    assignment.evidence->'provider_evidence'->'phish.net'->>'permalink' AS Permalink
                FROM sources src JOIN source_show_assignments assignment ON assignment.source_id=src.id
                LEFT JOIN shows sh ON sh.id=assignment.show_id LEFT JOIN artists sa ON sa.id=sh.artist_id
                WHERE src.artist_id=@artistId AND (@afterSourceUuid::uuid IS NULL OR src.uuid>@afterSourceUuid) AND (
                    (@accountType='show' AND assignment.evidence->'provider_evidence'->'phish.net'->>'show_id'=@accountKey)
                    OR (@accountType='setlist_page' AND assignment.evidence->'provider_evidence'->'phish.net'->>'permalink' IS NOT NULL))
                ORDER BY src.uuid LIMIT @limit
                """,
                new
                {
                    artistId,
                    accountType,
                    accountKey,
                    afterSourceUuid,
                    limit = PhishNetAssociationPageSize,
                },
                t,
                cancellationToken: ct
            )
        );
        return rows.ToArray();
    }

    private static NormalizationCatalogBinding<NormalizationCatalogPerformanceReference> PhishNetPerformance(
        NormalizationCatalogBinding<NormalizationCatalogArtistReference> artist,
        PhishNetAssociationRow[] rows
    )
    {
        if (artist.Status != NormalizationCatalogBindingStatus.Bound)
        {
            return new(artist.Status, null, "artist_unresolved");
        }
        if (rows.Length == 0)
        {
            return new(
                NormalizationCatalogBindingStatus.Missing,
                null,
                "missing_provider_assignment"
            );
        }
        if (
            rows.Any(row =>
                row.SourceShowId != row.AssignmentShowId
                || row.ShowUuid == null
                || row.ShowArtistUuid != artist.Value!.ArtistUuid
                || row.AssignmentKind is not ("accepted" or "legacy")
            )
        )
        {
            return new(
                NormalizationCatalogBindingStatus.ProviderConflict,
                null,
                "provider_assignment_conflict"
            );
        }
        var performances = rows.Select(row => row.ShowUuid!.Value).Distinct().ToArray();
        if (performances.Length != 1)
        {
            return new(
                NormalizationCatalogBindingStatus.Ambiguous,
                null,
                "ambiguous_provider_performance"
            );
        }
        return new(
            NormalizationCatalogBindingStatus.Bound,
            new(
                artist.Value!.ArtistUuid,
                performances[0],
                rows.Select(row => row.SourceUuid).Distinct().ToArray(),
                "existing_provider_assignment"
            ),
            "provider_assignment"
        );
    }

    private static string? PhishNetAssociationPath(string? value)
    {
        if (value == null)
        {
            return null;
        }
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }
        string? path;
        if (value.StartsWith('/'))
        {
            var suffix = value.IndexOfAny(['?', '#']);
            path = suffix < 0 ? value : value[..suffix];
        }
        else
        {
            path =
                Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "https" or "http"
                && uri.Host is "phish.net" or "www.phish.net"
                    ? uri.AbsolutePath
                    : null;
        }
        return
            path != null && Regex.IsMatch(path, @"^/setlists/[^/]+\.html$", RegexOptions.IgnoreCase)
            ? path
            : null;
    }

    private sealed class PhishNetAssociationRow
    {
        public Guid SourceUuid { get; set; }
        public long? SourceShowId { get; set; }
        public long AssignmentShowId { get; set; }
        public string AssignmentKind { get; set; } = "";
        public Guid? ShowUuid { get; set; }
        public Guid? ShowArtistUuid { get; set; }
        public string? Permalink { get; set; }
    }
}
