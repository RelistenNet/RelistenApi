using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPerformanceAdapter
{
    private static readonly JsonSerializerOptions ProviderJson = new(JsonSerializerDefaults.Web);

    private IEnumerable<ShadowCatalogResult> ProcessProvider(ShadowCatalogInput input)
    {
        NormalizationInputRevision revision;
        ProviderDecodeResult decoded;
        try
        {
            revision = ReadProviderRevision(input.Record, input.Uuid);
            if (
                revision.Resource.Provider == "local"
                && revision.Resource.ResourceType == "directory_metadata"
            )
            {
                return ProcessLocalProvider(input, revision);
            }
            decoded = new NormalizationProviderDecoder().Decode(revision);
            using var document = NormalizationRetainedDocument.Open(revision);
            foreach (var account in decoded.Setlists)
            {
                ValidatePerformanceEvidence(
                    document,
                    account.Origin,
                    account.LiteralDate,
                    account.Venue,
                    account.Text
                );
                foreach (var passage in account.Passages)
                {
                    document.ValidateEvidence(passage.Title.Evidence);
                }
            }
            foreach (var recording in decoded.Recordings)
            {
                ValidatePerformanceEvidence(
                    document,
                    recording.Origin,
                    recording.LiteralDate,
                    recording.Venue,
                    recording.Text
                );
            }
        }
        catch (Exception error)
            when (error is JsonException or ArgumentException or NotSupportedException)
        {
            return DomainsFor(input.Kind)
                .Select(domain =>
                    ShadowCatalogResult.For(
                        input,
                        domain,
                        "failed",
                        Version,
                        null,
                        new { input_revision_uuid = input.Uuid, error_type = error.GetType().Name },
                        error is NotSupportedException
                            ? "unsupported_provider_capture"
                            : "invalid_retained_provider_input"
                    )
                )
                .ToArray();
        }

        var accounts = decoded
            .Setlists.Select(
                (account, accountIndex) => ProviderAccount(input, revision, account, accountIndex)
            )
            .ToArray();
        var performances = decoded
            .Setlists.Select(
                (account, index) =>
                    new ShadowProviderPerformance(
                        ProviderAccountKey(account.Origin.Item),
                        account.Origin.Item,
                        account.LiteralDate?.Value,
                        account.Origin,
                        accounts[index].Association
                    )
            )
            .Concat(
                decoded.Recordings.Select(recording => new ShadowProviderPerformance(
                    ProviderAccountKey(recording.Origin.Item),
                    recording.Origin.Item,
                    recording.LiteralDate?.Value,
                    recording.Origin,
                    AssociateRecordingPerformance(input, revision, recording),
                    recording
                ))
            )
            .Distinct()
            .ToArray();
        var evidence = new
        {
            revision.Uuid,
            revision.Resource,
            revision.Sha256,
            revision.CaptureVersion,
            decoded.DecoderVersion,
            decoded.Issues,
            catalog_performance_assignment = "shadow_proposal_only",
        };
        return
        [
            ShadowCatalogResult.For(
                input,
                "performances",
                performances.Length == 0 ? "not_applicable"
                    : performances.All(value => value.Association?.ShowUuid != null) ? "proposed"
                    : "unresolved",
                Version,
                performances,
                evidence,
                performances.Length == 0
                    ? "provider_input_has_no_performance_account"
                    : performances
                        .FirstOrDefault(value => value.Association?.ShowUuid == null)
                        ?.Association?.Reason,
                performances
                    .SelectMany(value =>
                        value.Association?.PerformanceEntityKey is { } key
                            ? new[] { value.EntityKey, key }
                            : new[] { value.EntityKey }
                    )
                    .Distinct()
                    .ToArray()
            ),
            ShadowCatalogResult.For(
                input,
                "setlists",
                accounts.Length == 0 ? "not_applicable"
                    : accounts.All(value => value.Ordered) ? "proposed"
                    : "unresolved",
                Version,
                accounts,
                evidence,
                accounts.Length == 0 ? "provider_input_has_no_setlist_account"
                    : accounts.All(value => value.Ordered) ? null
                    : "provider_account_order_unknown",
                accounts
                    .SelectMany(account =>
                        account
                            .Occurrences.Select(value => value.EntityKey)
                            .Concat(
                                account
                                    .Occurrences.Select(value => value.SongEntityKey)
                                    .OfType<string>()
                            )
                            .Concat(
                                account.Occurrences.SelectMany(value =>
                                    value.SongInterpretation.CandidateSongs.Select(song =>
                                        song.EntityKey
                                    )
                                )
                            )
                            .Append(account.PerformanceEntityKey!)
                            .Prepend(account.EntityKey)
                    )
                    .Distinct()
                    .ToArray()
            ),
        ];
    }

    private static NormalizationInputRevision ReadProviderRevision(
        JsonElement record,
        Guid expectedUuid
    )
    {
        var revision =
            record.GetProperty("input").Deserialize<NormalizationInputRevision>(ProviderJson)
            ?? throw new ArgumentException("Missing retained provider revision.");
        if (
            revision.Uuid != expectedUuid
            || !StringComparer.OrdinalIgnoreCase.Equals(
                revision.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(revision.Payload))
            )
        )
        {
            throw new ArgumentException("Provider revision identity or retained bytes differ.");
        }
        return revision;
    }

    private ShadowSetlistAccount ProviderAccount(
        ShadowCatalogInput input,
        NormalizationInputRevision revision,
        ProviderSetlistObservation account,
        int accountIndex
    )
    {
        var accountKey = ShadowCatalogIdentity.Key(
            "setlist_account",
            $"{revision.Uuid:D}\n{accountIndex}"
        );
        // A provider's sequence creates passage references only when it actually asserts
        // order. Set boundaries stay in the original observation and never split a show.
        ShadowSetlistOccurrence[] occurrences = account.Ordered
            ? account
                .Passages.Select(
                    (passage, index) =>
                    {
                        var interpretation = InterpretProviderTitle(input, passage);
                        return new ShadowSetlistOccurrence(
                            ShadowCatalogIdentity.Key(
                                "occurrence",
                                $"{revision.Uuid:D}\n{accountIndex}\n{index}"
                            ),
                            passage.Ordinal,
                            passage.SetOrdinal,
                            passage.Title.Value,
                            interpretation.Song?.EntityKey,
                            passage,
                            interpretation
                        );
                    }
                )
                .ToArray()
            : [];
        ShadowSetlistMembership[] memberships = account.Ordered
            ? []
            : account
                .Passages.Select(passage => new ShadowSetlistMembership(
                    null,
                    null,
                    null,
                    passage.Title.Value
                ))
                .ToArray();
        var association = AssociatePerformance(
            input,
            account.LiteralDate?.Value,
            account.Origin,
            account.LiteralDate,
            account.Venue,
            account.Text
        );
        return new(
            accountKey,
            null,
            association.ShowUuid,
            account.LiteralDate?.Value,
            association.PerformanceEntityKey ?? ProviderAccountKey(account.Origin.Item),
            account.Ordered,
            memberships,
            occurrences,
            account,
            association
        );
    }

    private static void ValidatePerformanceEvidence(
        NormalizationRetainedDocument document,
        ProviderObservationOrigin origin,
        ObservedValue<string>? date,
        ProviderVenueObservation? venue,
        IReadOnlyList<ProviderTextObservation> text
    )
    {
        document.ValidateEvidence(origin.Evidence);
        if (date != null)
        {
            document.ValidateEvidence(date.Evidence);
        }
        if (venue != null)
        {
            foreach (var name in venue.Names)
            {
                document.ValidateEvidence(name.Evidence);
            }
            if (venue.City != null)
            {
                document.ValidateEvidence(venue.City.Evidence);
            }
        }
        foreach (var observation in text)
        {
            document.ValidateEvidence(observation.Text.Evidence);
        }
    }

    private static string ProviderAccountKey(NormalizationResourceKey identity) =>
        ShadowCatalogIdentity.Key("performance_account", JsonSerializer.Serialize(identity));

    private ShadowRepertoireSegment InterpretProviderTitle(
        ShadowCatalogInput input,
        ProviderPassageObservation passage
    )
    {
        var title = passage.Title.Value;
        var extraction = new TrackTitleExtractor().Extract(input.Artist.Uuid, title);
        var single =
            extraction.IsResolved && extraction.Segments.Count == 1 ? extraction.Segments[0] : null;
        var kind = single?.Kind ?? TrackContentKind.Unknown;
        if (kind == TrackContentKind.Song && single?.Title != title)
        {
            kind = TrackContentKind.Unknown;
        }
        var evidence = passage.Title.Evidence;
        // HTML observations can have decoded titles with differently encoded original
        // spans. Keep the whole actual provider citation; never transplant decoded offsets
        // into markup or claim a compound provider passage is a single identified song.
        var mention = new TrackTitleSegment(
            0,
            kind,
            title,
            new(
                "provider_title",
                evidence.JsonPointer,
                evidence.Quote ?? title,
                evidence.StartCodePoint ?? 0,
                evidence.EndCodePoint ?? title.EnumerateRunes().Count()
            ),
            TitleCoverage.Unknown,
            TitleTransition.Unknown
        );
        return repertoire.ResolveTitleMention(input.Artist.Id, input.Artist.Uuid, mention);
    }
}
