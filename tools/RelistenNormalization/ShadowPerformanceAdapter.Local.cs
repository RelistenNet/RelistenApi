using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPerformanceAdapter
{
    // A root supports many directories. Retain it once during observation so each directory
    // can reproduce its own account without treating the root as one recording or setlist.
    private readonly Dictionary<
        Guid,
        (NormalizationInputRevision Input, ProviderDecodeResult Decoded)
    > localRoots = [];

    private void ObserveLocalRoot(JsonElement record)
    {
        var revision = ReadProviderRevision(
            record,
            record.GetProperty("row").GetProperty("uuid").GetGuid()
        );
        var decoded = NormalizationLocalRecordingSelection.DecodeRoot(
            revision,
            new NormalizationProviderDecoder()
        );
        if (
            localRoots.TryGetValue(revision.Uuid, out var prior)
            && (prior.Input.Sha256 != revision.Sha256 || prior.Input.Resource != revision.Resource)
        )
        {
            throw new InvalidDataException(
                "One retained Local root revision has conflicting payloads."
            );
        }
        localRoots.TryAdd(revision.Uuid, (revision, decoded));
    }

    private ShadowCatalogResult[] ProcessLocalProvider(
        ShadowCatalogInput input,
        NormalizationInputRevision revision
    )
    {
        var directory = new NormalizationProviderDecoder().Decode(revision).Recordings.Single();
        var provenance =
            input
                .Record.GetProperty("retained_local")
                .Deserialize<RetainedLocalProviderProvenance>(ProviderJson)
            ?? throw new ArgumentException("Missing retained Local pair.");
        var binding = provenance.CatalogBinding;
        ProviderRecordingObservation? account = null;
        string? reason = null;
        if (
            provenance.InputRevisionUuid != revision.Uuid
            || provenance.Body.Bytes != revision.Payload.LongLength
            || !StringComparer.OrdinalIgnoreCase.Equals(provenance.Body.Sha256, revision.Sha256)
            || revision.Resource
                != NormalizationProviderResources.LocalDirectory(
                    provenance.Root,
                    provenance.Directory
                )
        )
        {
            reason = "invalid_retained_local_pair";
        }
        else if (!localRoots.TryGetValue(provenance.RootInputRevisionUuid, out var root))
        {
            reason = "missing_retained_local_root";
        }
        else if (
            NormalizationProviderResources.LocalRootForDirectory(revision.Resource)
                != root.Input.Resource
            || provenance.RootBody.Bytes != root.Input.Payload.LongLength
            || !StringComparer.OrdinalIgnoreCase.Equals(
                provenance.RootBody.Sha256,
                root.Input.Sha256
            )
        )
        {
            reason = "invalid_retained_local_pair";
        }
        else
        {
            // Selection is by the decoder's exact tuple identity, never the root date or
            // matching prose. Other root accounts cannot supply this directory's context.
            var accounts = root
                .Decoded.Recordings.Where(value => value.Origin.Item == revision.Resource)
                .ToArray();
            if (accounts.Length != 1)
            {
                reason =
                    accounts.Length == 0 ? "directory_not_in_root" : "multiple_directory_accounts";
            }
            else
            {
                account = accounts[0];
                using var document = NormalizationRetainedDocument.Open(root.Input);
                ValidatePerformanceEvidence(
                    document,
                    account.Origin,
                    account.LiteralDate,
                    account.Venue,
                    account.Text
                );
            }
        }
        using (var document = NormalizationRetainedDocument.Open(revision))
        {
            ValidatePerformanceEvidence(
                document,
                directory.Origin,
                directory.LiteralDate,
                directory.Venue,
                directory.Text
            );
            foreach (var file in directory.Files)
            {
                document.ValidateEvidence(file.Origin.Evidence);
                if (file.PlaybackLocator != null)
                {
                    document.ValidateEvidence(file.PlaybackLocator.Evidence);
                }
            }
        }

        var show =
            reason == null && binding.Status == "bound"
                ? BoundLocalShow(input, binding, provenance, directory)
                : null;
        reason ??=
            binding.Status != "bound" ? binding.Reason ?? "local_catalog_binding_unresolved"
            : show == null ? binding.ShowReason ?? "retained_local_catalog_conflict"
            : null;
        var providerDate = ShadowPerformanceDate.Parse(account?.LiteralDate?.Value);
        var catalogDate = ShadowPerformanceDate.Parse(show?.LiteralDate);
        if (
            reason == null
            && providerDate != null
            && catalogDate != null
            && !providerDate.CompatibleWith(catalogDate)
        )
        {
            reason = "provider_date_conflicts_with_catalog_assignment";
        }
        var association = new ShadowPerformanceAssociation(
            show?.Uuid,
            show == null ? null : "show:" + show.Uuid.ToString("D"),
            "retained_local_exact_media",
            reason,
            account?.LiteralDate?.Value,
            account?.Origin ?? directory.Origin,
            account?.LiteralDate,
            [],
            show == null
                ? []
                :
                [
                    new(
                        show.Uuid,
                        show.Id,
                        show.LiteralDate,
                        show.PerformanceType,
                        show.PerformanceLabel,
                        show.PerformanceKey,
                        show.VenueId,
                        false
                    ),
                ],
            LocalCatalogBinding: binding
        );
        var performance = new ShadowProviderPerformance(
            ProviderAccountKey(revision.Resource),
            revision.Resource,
            account?.LiteralDate?.Value,
            account?.Origin ?? directory.Origin,
            association,
            directory
        );
        var evidence = new
        {
            input_revision_uuid = revision.Uuid,
            supporting_root_revision_uuid = provenance.RootInputRevisionUuid,
            decoder = NormalizationProviderDecoder.Version,
            retained_local = provenance,
            root_account = account,
            catalog_performance_assignment = "shadow_proposal_only",
        };
        return
        [
            ShadowCatalogResult.For(
                input,
                "performances",
                show == null ? "unresolved" : "proposed",
                Version,
                new[] { performance },
                evidence,
                reason,
                show == null
                    ? [performance.EntityKey]
                    : [performance.EntityKey, association.PerformanceEntityKey!]
            ),
            ShadowCatalogResult.For(
                input,
                "setlists",
                "not_applicable",
                Version,
                Array.Empty<ShadowSetlistAccount>(),
                evidence,
                "provider_input_has_no_setlist_account"
            ),
        ];
    }

    private ShowHome? BoundLocalShow(
        ShadowCatalogInput input,
        RetainedLocalCatalogBinding binding,
        RetainedLocalProviderProvenance provenance,
        ProviderRecordingObservation directory
    )
    {
        // Provider evidence must agree with the captured catalog inventory from the first
        // pass. Neither a root date nor a sampling hint may pick a replacement show.
        var source = Object(input.Record, "source_context");
        var show = Object(input.Record, "show_context");
        if (
            source == null
            || show == null
            || binding.SourceUuid == null
            || binding.ShowUuid == null
            || binding.RootArtists.Count != 1
            || binding.RootArtists[0].ArtistUuid != input.Artist.Uuid
            || binding.RootArtists[0].ArtistId != input.Artist.Id
            || binding.RootArtists[0].Root != provenance.Root
            || Uuid(source.Value, "uuid") != binding.SourceUuid
            || Uuid(show.Value, "uuid") != binding.ShowUuid
            || Number(source.Value, "artist_id") != input.Artist.Id
            || Number(show.Value, "artist_id") != input.Artist.Id
            || Number(source.Value, "show_id") != Number(show.Value, "id")
            || Number(source.Value, "id") is not { } sourceId
            || conflictingSources.Contains(sourceId)
            || !sources.TryGetValue(sourceId, out var indexed)
            || indexed.Uuid != binding.SourceUuid
            || indexed.ArtistId != input.Artist.Id
            || indexed.ShowId != Number(show.Value, "id")
            || Number(show.Value, "id") is not { } showId
            || !shows.TryGetValue(showId, out var indexedShow)
            || HasConflict(indexedShow)
            || indexedShow.Uuid != binding.ShowUuid
            || indexedShow.ArtistId != input.Artist.Id
        )
        {
            return null;
        }
        var supported = binding.Files.Any(anchor =>
            anchor.Status == "bound"
            && anchor.SourceUuid == binding.SourceUuid
            && anchor.TrackUuid != null
            && anchor.SourceSetUuid != null
            && anchor.FileOrdinal >= 0
            && anchor.FileOrdinal < directory.Files.Count
            && anchor.MediaUrl
                == RetainedLocalProviderInputs.MediaUrl(
                    provenance.Root,
                    provenance.Directory,
                    directory.Files[anchor.FileOrdinal]
                )
        );
        return supported ? indexedShow : null;
    }
}
