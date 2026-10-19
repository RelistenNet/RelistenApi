using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Authenticates venue observations through published extraction receipts. The request names
/// an original account coordinate; only that account's saved catalog binding can supply a show.
/// Candidate preparation rechecks publication ownership and binding clocks in its own snapshot.
/// </summary>
public sealed class NormalizationVenueSourceService(
    DbService db,
    NormalizationEvidenceService evidence,
    NormalizationProviderDecoder decoder
)
{
    public const string Version = "venue-resolution-source-v1";

    /// <summary>
    /// Prepares the exact venue account from a published extraction. Local claims name the
    /// supporting root revision and account pointer; its saved directory binding supplies ownership.
    /// </summary>
    public async Task<VenueResolutionSource?> PrepareShowClaimAsync(
        VenueShowClaimReference reference,
        CancellationToken cancellationToken = default
    )
    {
        if (
            reference.PublishedAttemptUuid == Guid.Empty
            || reference.InputRevisionUuid == Guid.Empty
            || string.IsNullOrEmpty(reference.VenueJsonPointer)
            || !reference.VenueJsonPointer.StartsWith('/')
            || reference.AccountIndex is < 0
            || !Enum.IsDefined(reference.SourceKind)
        )
        {
            throw new ArgumentException(
                "A show venue claim requires an exact published account coordinate."
            );
        }

        var published = await ReadPublishedBatch(reference, cancellationToken);
        if (published == null)
        {
            return null;
        }

        return reference.SourceKind switch
        {
            VenueShowClaimSourceKind.Recording => await PrepareRecording(
                reference,
                published,
                cancellationToken
            ),
            VenueShowClaimSourceKind.Setlist => await PrepareSetlist(
                reference,
                published,
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(reference)),
        };
    }

    private async Task<VenueResolutionSource> PrepareRecording(
        VenueShowClaimReference reference,
        PublishedBatch published,
        CancellationToken ct
    )
    {
        if (reference.AccountIndex != null)
        {
            throw new ArgumentException("Recording claims do not have a setlist account index.");
        }
        var batch = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(
            published.BatchJson
        )!;
        if (
            batch.ProcessingVersion != NormalizationRecordingProcessor.Version
            || batch.AttemptUuid != reference.PublishedAttemptUuid
            || !NormalizationRecordingReadSet.IsComplete(batch.Ticket, batch.Preparation)
        )
        {
            throw new NormalizationConflictException(
                "Recording venue claims require a compatible complete extraction receipt."
            );
        }
        RequireTicket(batch.Ticket, published);
        var retained = await ReadOriginalInputs(batch.Ticket, ct);
        if (!retained.TryGetValue(reference.InputRevisionUuid, out var input))
        {
            throw new ArgumentException(
                "The requested revision is not a contributing recording input."
            );
        }
        var bindings = NormalizationRecordingReadSet
            .Inputs(batch.Preparation)
            .Select(item => item.Binding)
            .ToArray();
        NormalizationRecordingBindingSnapshot binding;
        ProviderRecordingObservation recording;
        if (input.Resource.Provider == "local" && input.Resource.ResourceType == "metadata_root")
        {
            (binding, recording) = SelectLocalRecording(reference, input, bindings, retained, ct);
        }
        else
        {
            binding = RequireBinding(bindings.Where(item => item.InputRevisionUuid == input.Uuid));
            var decoded = decoder.Decode(input, ct);
            if (
                decoded.Recordings.Count != 1
                || decoded.Recordings[0].Origin.Item != binding.Resource
            )
            {
                throw new NormalizationConflictException(
                    "The original recording input no longer reproduces its supported account contract."
                );
            }
            recording = decoded.Recordings[0];
        }
        var venue = RequireVenue(reference, recording.Venue);
        var bound = binding.Recording;
        var value = bound.Status == NormalizationCatalogBindingStatus.Bound ? bound.Value : null;
        if (
            value != null
            && (value.SourceUuid != batch.SourceUuid || value.ArtistUuid != batch.ArtistUuid)
        )
        {
            throw new NormalizationConflictException(
                "The recording binding does not belong to its published source and artist."
            );
        }
        return new(
            new(
                value?.ShowUuid,
                true,
                venue,
                venue.Origin.Evidence,
                value?.ArtistUuid,
                batch.AttemptUuid,
                ParseDate(recording.LiteralDate, recording.Origin.Item.Provider)
            ),
            batch.Ticket,
            published.ToObservation(),
            reference,
            binding
        );
    }

    private (
        NormalizationRecordingBindingSnapshot Binding,
        ProviderRecordingObservation Recording
    ) SelectLocalRecording(
        VenueShowClaimReference reference,
        NormalizationInputRevision root,
        IReadOnlyList<NormalizationRecordingBindingSnapshot> bindings,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> retained,
        CancellationToken ct
    )
    {
        var decodedRoot = NormalizationLocalRecordingSelection.DecodeRoot(
            root,
            decoder,
            cancellationToken: ct
        );
        var accounts = decodedRoot
            .Recordings.Where(account =>
                account.Venue?.Origin.Evidence.JsonPointer == reference.VenueJsonPointer
            )
            .ToArray();
        if (accounts.Length != 1)
        {
            throw new ArgumentException(
                "The coordinate does not identify one retained Local venue account."
            );
        }

        // The root pointer selects an account, not a catalog recording. Only its published
        // directory contributor can supply the source/show binding; dates never select it.
        var binding = RequireBinding(
            bindings.Where(item =>
                item.Resource == accounts[0].Origin.Item
                && item.SupportingInputs is { Count: 1 }
                && item.SupportingInputs[0]
                    == new NormalizationBindingInputReference(root.Uuid, root.Resource)
            )
        );
        var directory = retained[binding.InputRevisionUuid];
        var selected = NormalizationLocalRecordingSelection.Select(
            directory,
            decoder.Decode(directory, ct),
            binding,
            root,
            decodedRoot,
            ct
        );
        return (binding, selected.RootAccount);
    }

    private static NormalizationRecordingBindingSnapshot RequireBinding(
        IEnumerable<NormalizationRecordingBindingSnapshot> candidates
    )
    {
        var bindings = candidates.ToArray();
        if (bindings.Length == 0 || bindings.Any(binding => !SameJson(binding, bindings[0])))
        {
            throw new ArgumentException(
                "The requested revision has no unambiguous original recording binding."
            );
        }
        return bindings[0];
    }

    private async Task<VenueResolutionSource> PrepareSetlist(
        VenueShowClaimReference reference,
        PublishedBatch published,
        CancellationToken ct
    )
    {
        if (reference.AccountIndex == null)
        {
            throw new ArgumentException(
                "Setlist claims require the original account index, including duplicate accounts."
            );
        }
        var batch = JsonSerializer.Deserialize<NormalizationSetlistProposalBatch>(
            published.BatchJson
        )!;
        if (
            batch.ProcessingVersion != NormalizationSetlistProcessor.Version
            || batch.AttemptUuid != reference.PublishedAttemptUuid
            || !NormalizationSetlistReadSet.IsComplete(batch.Ticket, batch.Preparation)
        )
        {
            throw new NormalizationConflictException(
                "Setlist venue claims require a compatible complete extraction receipt."
            );
        }
        RequireTicket(batch.Ticket, published);
        var input =
            batch.Preparation.Inputs.SingleOrDefault(item =>
                item.Input.InputRevisionUuid == reference.InputRevisionUuid
            )
            ?? throw new ArgumentException(
                "The requested revision is not a contributing setlist input."
            );
        var extracted = batch.Extractions.Single(item =>
            item.InputRevisionUuid == reference.InputRevisionUuid
        );
        var index = reference.AccountIndex.Value;
        if (index >= input.Accounts.Count || index >= extracted.Accounts.Count)
        {
            throw new ArgumentException("The requested setlist account index is absent.");
        }
        var prepared = input.Accounts[index];
        if (
            !SameJson(prepared.Account, extracted.Accounts[index].Account)
            || prepared.Binding.Account != prepared.Account.Origin
        )
        {
            throw new NormalizationConflictException(
                "The selected account does not match its original binding and extraction."
            );
        }
        var retained = await ReadOriginalInputs(batch.Ticket, ct);
        var root = decoder.Decode(retained[reference.InputRevisionUuid], ct);
        // The preparation filters a page to the qualified registration. Preserve that filtered
        // order instead of confusing its account index with an unrelated account on the same page.
        var reproduced = root
            .Setlists.Where(account => account.Origin.Item == prepared.Account.Origin.Item)
            .ToArray();
        if (index >= reproduced.Length || !SameJson(reproduced[index], prepared.Account))
        {
            throw new NormalizationConflictException(
                "Retained bytes do not reproduce the exact selected setlist account."
            );
        }
        var venue = RequireVenue(reference, reproduced[index].Venue);
        var performance = prepared.Binding.Performance;
        var value =
            performance.Status == NormalizationCatalogBindingStatus.Bound
                ? performance.Value
                : null;
        return new(
            new(
                value?.ShowUuid,
                true,
                venue,
                venue.Origin.Evidence,
                value?.ArtistUuid ?? prepared.Binding.Artist.Value?.ArtistUuid,
                batch.AttemptUuid,
                ParseDate(reproduced[index].LiteralDate, reproduced[index].Origin.Item.Provider)
            ),
            batch.Ticket,
            published.ToObservation(),
            reference,
            SetlistBinding: prepared.Binding
        );
    }

    private static ProviderVenueObservation RequireVenue(
        VenueShowClaimReference reference,
        ProviderVenueObservation? venue
    )
    {
        if (
            venue == null
            || venue.Origin.Evidence.InputRevisionUuid != reference.InputRevisionUuid
            || venue.Origin.Evidence.JsonPointer != reference.VenueJsonPointer
        )
        {
            throw new ArgumentException(
                "The coordinate does not identify the selected account's original venue observation."
            );
        }
        return venue;
    }

    private static ObservedValue<DateOnly>? ParseDate(
        ObservedValue<string>? literal,
        string provider
    )
    {
        if (literal == null)
        {
            return null;
        }
        // Only exact provider day representations are interpreted. Locale guessing, partial
        // dates and timestamps remain retained wording without an invented calendar day.
        var formats = provider == "setlist.fm" ? new[] { "dd-MM-yyyy" } : new[] { "yyyy-MM-dd" };
        return DateOnly.TryParseExact(
            literal.Value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date
        )
            ? new(date, literal.Evidence)
            : null;
    }

    private async Task<IReadOnlyDictionary<Guid, NormalizationInputRevision>> ReadOriginalInputs(
        NormalizationWorkTicket ticket,
        CancellationToken ct
    )
    {
        if (ticket.Inputs.Count > 100)
        {
            throw new InvalidOperationException(
                "Venue source preparation exceeds its complete input limit."
            );
        }
        var references = await db.WithWriteConnection(async c => (
                await c.QueryAsync<InputReferenceRow>(
                    new CommandDefinition(
                        """
                        SELECT r.uuid AS "InputRevisionUuid",p.provider AS "Provider",p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey"
                        FROM normalization.input_revisions r JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                        WHERE r.uuid=ANY(@ids) ORDER BY r.uuid
                        """,
                        new { ids = ticket.Inputs.Select(input => input.RevisionUuid).ToArray() },
                        cancellationToken: ct
                    )
                )
            ).Select(row => new NormalizationBindingInputReference(row.InputRevisionUuid, new(row.Provider, row.ResourceType, row.ProviderKey))).ToArray());
        if (references.Length != ticket.Inputs.Count)
        {
            throw new NormalizationConflictException(
                "An original contributing input is unavailable."
            );
        }
        var retained = await evidence.GetInputsAsync(
            references,
            new(100, 32 * 1024 * 1024, 128 * 1024 * 1024),
            ct
        );
        return retained.ToDictionary(input => input.Uuid);
    }

    private Task<PublishedBatch?> ReadPublishedBatch(
        VenueShowClaimReference reference,
        CancellationToken ct
    )
    {
        var recording = reference.SourceKind == VenueShowClaimSourceKind.Recording;
        // This closed switch selects known typed extraction stores. No caller-controlled SQL or
        // arbitrary show UUID participates in source authentication.
        var table = recording ? "recording_processing_attempts" : "setlist_processing_attempts";
        var kind = recording ? "recording" : "setlist";
        var key = recording ? "source_uuid" : "registration_uuid";
        return db.WithWriteConnection<PublishedBatch?>(c =>
            c.QuerySingleOrDefaultAsync<PublishedBatch>(
                new CommandDefinition(
                    $"""
                    SELECT a.batch::text AS "BatchJson",r.ticket::text AS "TicketJson",s.uuid AS "SubjectUuid",
                        s.desired_generation AS "DesiredGeneration",s.editorial_revision AS "EditorialRevision",s.published_work_uuid AS "PublishedWorkUuid"
                    FROM normalization.{table} a JOIN normalization.work_runs r ON r.uuid=a.attempt_uuid AND r.state='published'
                    JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid AND s.domain='extractions' AND s.subject_kind=@kind
                        AND s.subject_uuid=a.{key} AND s.published_work_uuid=a.attempt_uuid
                    WHERE a.attempt_uuid=@attemptUuid AND a.disposition='published' AND a.batch IS NOT NULL
                        AND octet_length(a.batch::text)<=16777216
                    """,
                    new { kind, attemptUuid = reference.PublishedAttemptUuid },
                    cancellationToken: ct
                )
            )
        );
    }

    private static void RequireTicket(NormalizationWorkTicket ticket, PublishedBatch batch)
    {
        if (
            !SameJson(ticket, JsonSerializer.Deserialize<NormalizationWorkTicket>(batch.TicketJson))
            || ticket.SubjectUuid != batch.SubjectUuid
            || ticket.WorkUuid != batch.PublishedWorkUuid
        )
        {
            throw new NormalizationConflictException(
                "Published proposals require their exact original work ticket."
            );
        }
    }

    private static bool SameJson<T>(T first, T second) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(first),
            JsonSerializer.SerializeToElement(second)
        );

    private sealed record PublishedBatch(
        string BatchJson,
        string TicketJson,
        Guid SubjectUuid,
        long DesiredGeneration,
        long EditorialRevision,
        Guid? PublishedWorkUuid
    )
    {
        public NormalizationSubjectObservation ToObservation() =>
            new(SubjectUuid, DesiredGeneration, EditorialRevision, PublishedWorkUuid);
    }

    private sealed record InputReferenceRow(
        Guid InputRevisionUuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    );
}
