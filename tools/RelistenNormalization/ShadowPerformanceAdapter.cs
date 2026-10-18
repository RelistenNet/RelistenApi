using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Connects captured shows, sources, files and setlist accounts in the shadow reader.
/// Legacy UUIDs retain their meaning. A file set, date or unordered membership cannot
/// establish a new performance, an ordered occurrence or a different playback owner.
/// </summary>
public sealed partial class ShadowPerformanceAdapter : IShadowCatalogAdapter
{
    private readonly Dictionary<long, SourceHome> sources = [];
    private readonly Dictionary<long, ShowHome> shows = [];
    private readonly HashSet<long> conflictingSources = [];
    private readonly HashSet<long> conflictingShows = [];
    private readonly ShadowRepertoireAdapter repertoire = new();

    public string Version =>
        "shadow-performances-v4/"
        + repertoire.Version
        + "/decoder="
        + NormalizationProviderDecoder.Version;

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind switch
        {
            "source_sample" => ["performances"],
            "track_sample" => ["performances", "file_contents"],
            "setlist_sample" or "provider_input" => ["performances", "setlists"],
            _ => [],
        };

    public void Observe(JsonElement record)
    {
        // The runner also observes actual extraction DTOs. They share Kind values
        // with raw catalog rows but cannot supply catalog ownership or UUID indexes.
        if (!record.TryGetProperty("row", out var row))
        {
            return;
        }
        repertoire.Observe(record);
        var kind = Text(record, "kind");
        if (kind == "show_sample")
        {
            ObserveShow(row);
        }
        if (kind == "source_sample")
        {
            ObserveSource(row);
        }
        if (kind == "track_sample")
        {
            if (Object(record, "source_context") is { } source)
            {
                ObserveSource(source);
            }
        }
        if (kind is "source_sample" or "track_sample" && Object(record, "show_context") is { } show)
        {
            ObserveShow(show);
        }
    }

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (input.Kind == "provider_input")
        {
            return ProcessProvider(input);
        }
        if (input.Kind == "setlist_sample")
        {
            return ProcessLegacySetlist(input);
        }
        if (input.Kind is not ("source_sample" or "track_sample"))
        {
            return [];
        }

        var home = CatalogHome(input);
        var results = new List<ShadowCatalogResult>
        {
            ShadowCatalogResult.For(
                input,
                "performances",
                home.Unresolved.Count == 0 ? "proposed" : "unresolved",
                Version,
                home,
                new { basis = "captured_catalog_assignment", source_set_is_performance = false },
                home.Unresolved.FirstOrDefault(),
                HomeKeys(home)
            ),
        };
        if (input.Kind == "track_sample")
        {
            results.Add(FileContents(input, home));
        }
        return results;
    }

    private ShadowCatalogResult FileContents(ShadowCatalogInput input, ShadowPerformanceHome home)
    {
        // Reuse the same repertoire interpretation that the song reader publishes. A second
        // title matcher here would let the file and song views disagree about one mention.
        var playback = new ShadowFilePlayback(
            input.Uuid,
            home.SourceUuid,
            home.ShowUuid,
            Text(input.Row, "mp3_url")
        );
        ShadowCatalogResult? titleResult;
        try
        {
            titleResult = repertoire.Process(input).SingleOrDefault();
        }
        catch (Exception error)
            when (error
                    is InvalidDataException
                        or JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or ArgumentException
            )
        {
            // A bad title proof must not erase a valid captured performance or audio URL.
            return ShadowCatalogResult.For(
                input,
                "file_contents",
                "failed",
                Version,
                new ShadowFileContents(home, playback, null),
                new { error_type = error.GetType().Name },
                "invalid_title_extraction",
                HomeKeys(home)
            );
        }
        var interpretation = titleResult?.Proposal as ShadowRepertoireTrack;
        var unresolved = interpretation == null || titleResult?.Outcome is "unresolved" or "failed";
        var keys = HomeKeys(home).Concat(titleResult?.EntityKeys ?? []).Distinct().ToArray();
        return ShadowCatalogResult.For(
            input,
            "file_contents",
            unresolved ? "unresolved" : "proposed",
            Version,
            new ShadowFileContents(home, playback, interpretation),
            new
            {
                title_interpretation = "actual_local_extraction",
                playback_basis = "current_catalog_file",
                audio_boundaries = "unknown",
            },
            interpretation == null ? "missing_title_extraction" : titleResult?.Reason,
            keys
        );
    }

    private IEnumerable<ShadowCatalogResult> ProcessLegacySetlist(ShadowCatalogInput input)
    {
        var members = input
            .Record.GetProperty("unordered_memberships")
            .EnumerateArray()
            .Select(member =>
            {
                var song = Object(member, "song");
                var uuid = song is { } row ? Uuid(row, "uuid") : null;
                return new ShadowSetlistMembership(
                    Number(member, "song_id"),
                    uuid,
                    uuid is { } known ? ShadowRepertoireAdapter.KnownEntityKey(known) : null,
                    song is { } value ? Text(value, "name") ?? Text(value, "title") : null
                );
            })
            .ToArray();
        var key = "setlist:" + input.Uuid.ToString("D");
        var association = AssociatePerformance(input, Text(input.Row, "date"));
        var account = new ShadowSetlistAccount(
            key,
            input.Uuid,
            association.ShowUuid,
            Text(input.Row, "date"),
            association.PerformanceEntityKey,
            false,
            members,
            [],
            null,
            association
        );
        return
        [
            ShadowCatalogResult.For(
                input,
                "performances",
                association.ShowUuid.HasValue ? "proposed" : "unresolved",
                Version,
                association,
                new { basis = "legacy_setlist_account", setlist_uuid = input.Uuid },
                association.Reason,
                association
                    .Candidates.Select(value => "show:" + value.ShowUuid.ToString("D"))
                    .Prepend(key)
                    .Distinct()
                    .ToArray()
            ),
            ShadowCatalogResult.For(
                input,
                "setlists",
                "unresolved",
                Version,
                account,
                new
                {
                    basis = "stored_unordered_memberships",
                    membership_count = members.Length,
                    order_known = false,
                },
                "legacy_memberships_do_not_retain_order",
                members
                    .Select(value => value.SongEntityKey)
                    .OfType<string>()
                    .Concat(
                        association.PerformanceEntityKey is { } performance
                            ? new[] { performance }
                            : []
                    )
                    .Prepend(key)
                    .Distinct()
                    .ToArray()
            ),
        ];
    }
}
