using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>Constructs literal retrieval queries; citations and caller context never confer matching authority.</summary>
public static class MusicBrainzCandidateQueryBuilder
{
    public const string Version = "musicbrainz-literal-search-v1";
    public const int PageSize = 50;
    public const int MaximumPages = 4;
    public const int MaximumVariants = 3;

    public static MusicBrainzSearchDescriptor CreateDescriptor(MusicBrainzSearchPageRequest request)
    {
        ValidatePageRequest(request);
        var kind = request.Query.Kind.ToString().ToLowerInvariant();
        var rendered = RenderQuery(request.Query);

        if (
            Encoding.UTF8.GetByteCount(rendered) > 4096
            || JsonSerializer.SerializeToUtf8Bytes(request).Length > 65536
        )
        {
            throw new ArgumentException("The search query or command exceeds its bound.");
        }

        // Stable identity names the exact external query window, independently of its local caller.
        var descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = Version,
                kind,
                query = rendered,
                limit = PageSize,
                offset = request.Offset,
            }
        );
        var resource = new NormalizationResourceKey(
            "musicbrainz.org",
            kind + "-search",
            Convert.ToHexStringLower(SHA256.HashData(descriptorBytes))
        );

        return new(
            Version,
            request.Query.Kind,
            rendered,
            PageSize,
            request.Offset,
            resource,
            "musicbrainz-" + kind + "-search-json-v1",
            "https://musicbrainz.org/ws/2/"
                + kind
                + "?query="
                + Uri.EscapeDataString(rendered)
                + "&limit=50&offset="
                + request.Offset.ToString(CultureInfo.InvariantCulture)
                + "&fmt=json"
        );
    }

    private static void ValidatePageRequest(MusicBrainzSearchPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query =
            request.Query ?? throw new ArgumentException("A typed search query is required.");
        if (
            !Enum.IsDefined(query.Kind)
            || request.Offset < 0
            || request.Offset % PageSize != 0
            || request.Offset >= PageSize * MaximumPages
        )
        {
            throw new ArgumentException(
                "Use a supported kind and one of the four bounded page offsets."
            );
        }

        RequireCachePolicy(request.CacheMode, request.MaximumCacheAge);
        if (
            request.PlanIdentity is { } plan
            && (plan.PlanUuid == Guid.Empty || !IsHash(plan.RequestSha256))
        )
        {
            throw new ArgumentException(
                "A plan identity requires its nonempty UUID and complete request SHA-256."
            );
        }
    }

    private static string RenderQuery(MusicBrainzSearchQuery query)
    {
        var kind = query.Kind.ToString().ToLowerInvariant();
        var field = query.Kind == MusicBrainzLookupKind.Artist ? "artist" : kind;
        var rendered = field + ":" + Literal(query.Text);

        if (query.RelatedArtist is { } related)
        {
            var expectedUse =
                query.Kind == MusicBrainzLookupKind.Work
                    ? MusicBrainzSearchArtistUse.WorkAuthor
                    : MusicBrainzSearchArtistUse.RecordingPerformer;
            if (
                query.Kind == MusicBrainzLookupKind.Artist
                || related.Use != expectedUse
                || !Enum.IsDefined(related.Use)
                || (related.Name != null) == (related.ExternalArtistMbid != null)
                || related.ExternalArtistMbid == Guid.Empty
            )
            {
                throw new ArgumentException(
                    "A search constraint must distinguish external authors from recording performers."
                );
            }
            rendered += related.ExternalArtistMbid is { } mbid
                ? " AND arid:" + mbid.ToString("D")
                : " AND "
                    + (query.Kind == MusicBrainzLookupKind.Work ? "artist" : "artistname")
                    + ":"
                    + Literal(related.Name!);
        }

        return rendered;
    }

    public static MusicBrainzSearchVariants BuildVariants(MusicBrainzSearchSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (
            !Enum.IsDefined(seed.Kind)
            || seed.RelatedArtists == null
            || seed.RelatedArtists.Count > 100
            || seed.RelatedArtists.Any(context => context == null)
        )
        {
            throw new ArgumentException(
                "Use a supported search seed with at most 100 context assertions."
            );
        }

        seed = seed with { RelatedArtists = seed.RelatedArtists.ToArray() };
        if (seed.Text == null || string.IsNullOrWhiteSpace(seed.Text.Value))
        {
            return new(Version, seed, [], ["MissingSearchText"]);
        }

        var broad = new MusicBrainzSearchQuery(seed.Kind, seed.Text.Value);
        _ = CreateDescriptor(new(broad, 0, MusicBrainzCacheMode.Refresh));
        var issues = new List<string>();
        var qualified = BuildContextQueries(broad, seed.RelatedArtists, issues);

        var distinct = qualified
            .Distinct()
            .OrderBy(
                q => CreateDescriptor(new(q, 0, MusicBrainzCacheMode.Refresh)).RenderedQuery,
                StringComparer.Ordinal
            )
            .ToArray();
        if (distinct.Length >= MaximumVariants)
        {
            issues.Add("QueryVariantLimit");
        }

        return new(
            Version,
            seed,
            new[] { broad }.Concat(distinct.Take(MaximumVariants - 1)).ToArray(),
            issues.Distinct().ToArray()
        );
    }

    private static IReadOnlyList<MusicBrainzSearchQuery> BuildContextQueries(
        MusicBrainzSearchQuery broad,
        IReadOnlyList<MusicBrainzSearchArtistSeed> contexts,
        List<string> issues
    )
    {
        var qualified = new List<MusicBrainzSearchQuery>();
        foreach (var context in contexts)
        {
            if (
                context.Use == MusicBrainzSearchArtistUse.WorkAuthor
                && context.Role?.Value?.ToLowerInvariant()
                    is not ("writer" or "composer" or "lyricist")
            )
            {
                issues.Add("UnsupportedAuthorContext");
                continue;
            }

            MusicBrainzSearchArtistConstraint related;
            if (context.Identity != null)
            {
                var identity =
                    context.Identity.Identity
                    ?? throw new ArgumentException("A qualified artist identity is required.");
                if (
                    identity.Provider != "musicbrainz.org"
                    || identity.ResourceType != "artist"
                    || !Guid.TryParseExact(identity.ProviderKey, "D", out var mbid)
                    || mbid == Guid.Empty
                )
                {
                    throw new ArgumentException(
                        "An artist constraint requires a qualified external MusicBrainz artist identity."
                    );
                }
                related = new(context.Use, ExternalArtistMbid: mbid);
            }
            else
            {
                related = new(
                    context.Use,
                    Name: context.Name?.Value
                        ?? throw new ArgumentException(
                            "A context assertion requires a cited name or external identity."
                        )
                );
            }

            var query = broad with { RelatedArtist = related };
            _ = CreateDescriptor(new(query, 0, MusicBrainzCacheMode.Refresh));
            qualified.Add(query);
        }

        return qualified;
    }

    private static string Literal(string value)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.EnumerateRunes().Count() > 512
            || value.EnumerateRunes().Any(Rune.IsControl)
        )
        {
            throw new ArgumentException(
                "Search terms require 1–512 Unicode scalar values without controls."
            );
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Search terms require valid Unicode.", error);
        }

        var escaped = new StringBuilder("\"");
        foreach (var c in value)
        {
            if ("+-&|!(){}[]^\"~*?:\\/".Contains(c))
            {
                escaped.Append('\\');
            }
            escaped.Append(c);
        }

        return escaped.Append('"').ToString();
    }

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void RequireCachePolicy(MusicBrainzCacheMode mode, TimeSpan? age)
    {
        if (
            !Enum.IsDefined(mode)
            || (mode == MusicBrainzCacheMode.ReuseFresh && age is not { Ticks: > 0 })
            || (mode == MusicBrainzCacheMode.Refresh && age != null)
        )
        {
            throw new ArgumentException("Use an explicit valid cache policy.");
        }
    }
}
