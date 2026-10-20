using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

public sealed partial class NormalizationReviewHost
{
    private const int MaximumPreparationBytes = 4 * 1024 * 1024;

    private async Task<IResult> PrepareAsync(HttpContext context)
    {
        var request = await Read<ReviewPreparationRequest>(context);
        if (request.SourceRef == null)
        {
            throw new ArgumentException("An original source reference is required.");
        }
        if (
            request.Action is not ("accept" or "reject")
            || request.Scope is not ("account" or "alias")
        )
        {
            throw new ArgumentException("Choose accept or reject and account or alias scope.");
        }
        Required(request.Reason, "review reason");
        var source = await readers.SourceAsync(request.SourceRef, context.RequestAborted);
        ValidateSourceNamespace(source);
        if (source.Domain == "songs" && request.Scope != "account")
        {
            throw new ArgumentException(
                "Repertoire review currently supports exact captured accounts. Reusable aliases need an explicit captured-scope selector first."
            );
        }
        var target = await readers.SourceAsync(
            request.TargetRef ?? throw new ArgumentException("Choose a target."),
            context.RequestAborted
        );
        if (source.Domain != target.Domain)
        {
            throw new ArgumentException("Source and target must belong to the same domain.");
        }
        ValidateTargetNamespace(target);
        if (
            source.Domain == "songs"
            && (
                source.ArtistUuid != target.ArtistUuid
                || RepertoireCoordinates(source.StableKey)[0]
                    != RepertoireCoordinates(target.StableKey)[0]
            )
        )
        {
            throw new ArgumentException(
                "Reviewed repertoire correspondences must keep the same artist and captured scope."
            );
        }
        var selector =
            request.Scope == "account" ? AccountSelector(source) : AliasSelector(request, source);
        var uuid = RuleUuid(source.Domain, selector);
        var snapshot = await knowledge.ReadSnapshotAsync(context.RequestAborted);
        var previous = snapshot.Rules.SingleOrDefault(rule => rule.RuleUuid == uuid);
        var placeholder = new ExpertKnowledgeEvidencePin(
            "preparation.json",
            1,
            new string('0', 64)
        );
        var definition = new ExpertKnowledgeDefinition(
            source.Domain,
            selector,
            new(target.Domain, target.Namespace, target.StableKey),
            [placeholder],
            request.Reason
        );
        ExpertKnowledgeResolver.ValidateDefinition(definition);
        var previewRule = new ExpertKnowledgeRule(
            uuid,
            (previous?.Revision ?? 0) + 1,
            request.Action == "accept" ? "accepted" : "rejected",
            definition,
            Guid.NewGuid(),
            DateTime.UtcNow
        );
        var resolver = new ExpertKnowledgeResolver(
            ExpertKnowledgeResolver.CreateSnapshot(
                snapshot.Rules.Where(rule => rule.RuleUuid != uuid).Append(previewRule).ToArray()
            )
        );
        var observations =
            request.Scope == "account"
                ? new ReviewObservationPage([source], 1, true)
                : await readers.ObservationsAsync(
                    source.Domain,
                    selector.Literal!,
                    selector.ArtistUuid,
                    context.RequestAborted
                );
        var affected = observations
            .Items.Where(item =>
                resolver
                    .Evaluate(item.Observation)
                    .AppliedRules.Any(applied => applied.RuleUuid == uuid)
            )
            .ToArray();
        if (!observations.Items.Any(item => RuleMatchesSource(previewRule, item)))
        {
            throw new ArgumentException(
                "The selected scope matches no current account. Check the exact name and context."
            );
        }
        var user = ReviewTailnetAuthentication.User(context);
        var preparation = new ReviewPreparation(
            request.PreparationUuid ?? Guid.NewGuid(),
            user.Id,
            user.LoginName,
            DateTimeOffset.UtcNow,
            request.Action,
            request.Scope,
            request.Reason,
            uuid,
            previous?.Revision ?? 0,
            selector,
            source,
            target,
            observations,
            affected.Select(item => item.Reference).ToArray(),
            snapshot.Sha256
        );
        var saved = await SavePreparationAsync(preparation, context.RequestAborted);
        return Results.Json(
            new
            {
                saved.PreparationUuid,
                sourceLabel = saved.Source.DisplayName,
                targetLabel = saved.Target.DisplayName,
                saved.Action,
                saved.Scope,
                affected = new
                {
                    count = affected.Length,
                    items = affected
                        .Take(100)
                        .Select(item => new
                        {
                            label = item.DisplayName,
                            detail = item.Description,
                        }),
                    complete = observations.Complete && affected.Length <= 100,
                    omittedCount = Math.Max(0, affected.Length - 100)
                        + Math.Max(0, observations.Total - observations.Items.Length),
                },
                warnings = !observations.Complete
                    ? new[]
                    {
                        "The retained preview is incomplete. Additional accounts may match this reusable decision.",
                    }
                : affected.Length == 0
                    ? new[]
                    {
                        "Existing account decisions currently take precedence. This reusable rule can guide later accounts within its scope.",
                    }
                : [],
            },
            Json
        );
    }

    private async Task<IResult> ApplyAsync(HttpContext context)
    {
        var request = await Read<ReviewApplication>(context);
        var path = PreparationPath(request.PreparationUuid);
        if (!File.Exists(path))
        {
            throw new NormalizationConflictException(
                "This retained preparation is unavailable. Prepare the decision again."
            );
        }
        var bytes = await File.ReadAllBytesAsync(path, context.RequestAborted);
        if (bytes.Length > MaximumPreparationBytes)
        {
            throw new ArgumentException("The retained review preparation exceeds its byte limit.");
        }
        var preparation =
            JsonSerializer.Deserialize<ReviewPreparation>(bytes, Json)
            ?? throw new ArgumentException("The retained review preparation is invalid.");
        var user = ReviewTailnetAuthentication.User(context);
        if (
            preparation.PreparationUuid != request.PreparationUuid
            || preparation.ReviewerId != user.Id
        )
        {
            throw new ReviewHttpException(403, "This preparation belongs to another reviewer.");
        }
        var pin = new ExpertKnowledgeEvidencePin(
            path,
            bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes))
        );
        var definition = new ExpertKnowledgeDefinition(
            preparation.Source.Domain,
            preparation.Selector,
            new(
                preparation.Target.Domain,
                preparation.Target.Namespace,
                preparation.Target.StableKey
            ),
            [pin],
            preparation.Reason
        );
        var command = new ExpertKnowledgeSaveCommand(
            request.OperationUuid,
            preparation.RuleUuid,
            preparation.ExpectedRevision,
            preparation.Action,
            definition,
            preparation.Reason
        );

        // A lost successful reply remains replayable after its source changes. The durable
        // service still compares the entire original operation before returning that receipt.
        var history = await knowledge.GetHistoryAsync(preparation.RuleUuid, context.RequestAborted);
        if (!history.Any(entry => entry.OperationUuid == request.OperationUuid))
        {
            await RevalidatePreparationAsync(preparation, context.RequestAborted);
        }
        var result = await knowledge.SaveAsync(command, user.LoginName, context.RequestAborted);
        return Results.Json(result, Json);
    }

    private async Task RevalidatePreparationAsync(
        ReviewPreparation preparation,
        CancellationToken ct
    )
    {
        if ((await knowledge.ReadSnapshotAsync(ct)).Sha256 != preparation.KnowledgeSnapshotSha256)
        {
            throw new NormalizationConflictException(
                "Reviewed knowledge changed. Prepare the affected-account preview again."
            );
        }
        var source = await readers.SourceAsync(preparation.Source.Reference, ct);
        var target = await readers.SourceAsync(preparation.Target.Reference, ct);
        if (source != preparation.Source || target != preparation.Target)
        {
            throw new NormalizationConflictException(
                "The original account or target changed. Prepare the decision again."
            );
        }
        if (preparation.Scope == "alias")
        {
            var observations = await readers.ObservationsAsync(
                source.Domain,
                preparation.Selector.Literal!,
                preparation.Selector.ArtistUuid,
                ct
            );
            if (
                !JsonSerializer
                    .SerializeToUtf8Bytes(observations, Json)
                    .AsSpan()
                    .SequenceEqual(
                        JsonSerializer.SerializeToUtf8Bytes(preparation.Observations, Json)
                    )
            )
            {
                throw new NormalizationConflictException(
                    "The affected accounts changed. Prepare the decision again."
                );
            }
        }
    }

    private async Task<ReviewPreparation> SavePreparationAsync(
        ReviewPreparation preparation,
        CancellationToken ct
    )
    {
        if (preparation.PreparationUuid == Guid.Empty)
        {
            throw new ArgumentException("A nonempty preparation UUID is required.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preparation, Json);
        if (bytes.Length > MaximumPreparationBytes)
        {
            throw new ArgumentException(
                "The review evidence exceeds four MiB. Narrow the alias scope."
            );
        }
        var path = PreparationPath(preparation.PreparationUuid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (
                var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            var original = JsonSerializer.Deserialize<ReviewPreparation>(
                await File.ReadAllBytesAsync(path, ct),
                Json
            )!;
            // The browser can retry its UUID after a lost preview reply. Its immutable
            // evidence and choices must match; timestamps are not part of those choices.
            if (
                !JsonSerializer
                    .SerializeToUtf8Bytes(original with { CreatedAt = preparation.CreatedAt }, Json)
                    .AsSpan()
                    .SequenceEqual(bytes)
            )
            {
                throw new NormalizationConflictException(
                    "The preparation UUID already names another preview."
                );
            }
            return original;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return preparation;
    }

    private string PreparationPath(Guid uuid) =>
        Path.Combine(configuration.EvidenceRoot, "preparations", uuid.ToString("D") + ".json");

    private static ExpertKnowledgeSelector AccountSelector(ReviewReaderItem source) =>
        new(
            source.Namespace,
            source.StableKey,
            ArtistUuid: source.Namespace == "repertoire" ? source.ArtistUuid : null
        );

    private static ExpertKnowledgeSelector AliasSelector(
        ReviewPreparationRequest request,
        ReviewReaderItem source
    )
    {
        if (
            string.IsNullOrWhiteSpace(request.Literal)
            || request.Literal.Length > 4096
            || request.Literal != source.Literal
        )
        {
            throw new ArgumentException(
                "The alias must equal the original account's exact literal."
            );
        }
        return new(
            source.Namespace,
            Literal: request.Literal,
            ArtistUuid: source.Namespace == "repertoire" ? source.ArtistUuid : request.ArtistUuid,
            Provider: request.Provider,
            Locality: request.Locality,
            From: request.From,
            Through: request.Through
        );
    }

    private static void ValidateTargetNamespace(ReviewReaderItem target)
    {
        if (
            (target.Domain, target.Namespace)
            is not (

                ("venues", "legacy_venue")
                or
                ("people", "person_group")
                or
                ("songs", "repertoire")
                or
                ("compositions", "composition_work")
            )
        )
        {
            throw new ArgumentException("Select a reviewed target from this domain's reader.");
        }
    }

    private static void ValidateSourceNamespace(ReviewReaderItem source)
    {
        if (
            (source.Domain, source.Namespace)
            is not (

                ("venues", "legacy_venue")
                or
                ("people", "person_group")
                or
                ("songs", "repertoire")
                or
                ("compositions", "repertoire")
            )
        )
        {
            throw new ArgumentException("Choose an original account from this domain's reader.");
        }
    }

    private static Guid RuleUuid(string domain, ExpertKnowledgeSelector selector) =>
        new(
            SHA256
                .HashData(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            version = "normalization-review-selector-v1",
                            domain,
                            selector,
                        },
                        Json
                    )
                )
                .AsSpan(0, 16)
        );

    private static string[] RepertoireCoordinates(string key)
    {
        var coordinates = JsonSerializer.Deserialize<string[]>(key, Json);
        return
            coordinates is { Length: 2 }
            && coordinates.All(value => !string.IsNullOrWhiteSpace(value))
            ? coordinates
            : throw new ArgumentException(
                "An exact captured scope and repertoire key are required."
            );
    }

    private sealed record ReviewReference(
        string Domain,
        string Namespace,
        string StableKey,
        Guid? ArtistUuid = null
    );

    private sealed record ReviewPreparationRequest(
        ReviewReference SourceRef,
        ReviewReference? TargetRef,
        string Action,
        string Scope,
        string Reason,
        string? Literal = null,
        Guid? ArtistUuid = null,
        string? Provider = null,
        string? Locality = null,
        DateOnly? From = null,
        DateOnly? Through = null,
        Guid? PreparationUuid = null
    );

    private sealed record ReviewApplication(Guid OperationUuid, Guid PreparationUuid);

    private sealed record ReviewPreparation(
        Guid PreparationUuid,
        string ReviewerId,
        string ReviewerLogin,
        DateTimeOffset CreatedAt,
        string Action,
        string Scope,
        string Reason,
        Guid RuleUuid,
        long ExpectedRevision,
        ExpertKnowledgeSelector Selector,
        ReviewReaderItem Source,
        ReviewReaderItem Target,
        ReviewObservationPage Observations,
        ReviewReference[] Affected,
        string KnowledgeSnapshotSha256
    );

    private sealed record ReviewReaderItem(
        string Domain,
        string Namespace,
        string StableKey,
        string? Literal,
        Guid? ArtistUuid,
        string? Provider,
        string? Locality,
        DateOnly? Date,
        string DisplayName,
        string Description,
        string? EvidenceUrl = null,
        string? OriginalJson = null
    )
    {
        internal ReviewReference Reference => new(Domain, Namespace, StableKey, ArtistUuid);
        internal ExpertKnowledgeObservation Observation =>
            new(Domain, Namespace, StableKey, Literal, ArtistUuid, Provider, Locality, Date);
    }

    private sealed record ReviewObservationPage(ReviewReaderItem[] Items, int Total, bool Complete);

    /// <summary>Only fixed configured reader origins are fetched. Client evidence URLs are display data.</summary>
    private sealed class ReviewReaderBridge(ReviewHostConfiguration configuration, HttpClient http)
    {
        internal async Task<ReviewReaderItem> SourceAsync(
            ReviewReference reference,
            CancellationToken ct
        )
        {
            ValidateReference(reference);
            var page = await FetchAsync(
                reference.Domain,
                "/api/review-source",
                new()
                {
                    ["key"] = reference.StableKey,
                    ["namespace"] = reference.Namespace,
                    ["artist"] = reference.ArtistUuid?.ToString("D"),
                },
                ct
            );
            var exact = page
                .Items.Where(item =>
                    item.Domain == reference.Domain
                    && item.Namespace == reference.Namespace
                    && item.StableKey == reference.StableKey
                    && (reference.ArtistUuid == null || item.ArtistUuid == reference.ArtistUuid)
                )
                .ToArray();
            if (exact.Length != 1)
            {
                throw new NormalizationConflictException(
                    "The reader cannot uniquely authenticate this original reference."
                );
            }
            return exact[0];
        }

        internal Task<ReviewObservationPage> CandidatesAsync(
            string domain,
            string query,
            Guid? artistUuid,
            string? scope,
            CancellationToken ct
        )
        {
            if (domain == "songs" && (artistUuid == null || string.IsNullOrWhiteSpace(scope)))
            {
                throw new ArgumentException(
                    "Repertoire candidates require the original artist and captured scope."
                );
            }
            return FetchAsync(
                domain,
                "/api/review-candidates",
                new()
                {
                    ["q"] = query,
                    ["artist"] = artistUuid?.ToString("D"),
                    ["scope"] = scope,
                },
                ct
            );
        }

        internal async Task<ReviewObservationPage> ObservationsAsync(
            string domain,
            string literal,
            Guid? artistUuid,
            CancellationToken ct
        )
        {
            var items = new List<ReviewReaderItem>();
            var offset = 0;
            var total = 0;
            var complete = false;
            var evidenceBytes = 0;
            var seen = new HashSet<ReviewReference>();
            while (true)
            {
                var page = await FetchAsync(
                    domain,
                    "/api/review-observations",
                    new()
                    {
                        ["literal"] = literal,
                        ["artist"] = artistUuid?.ToString("D"),
                        ["offset"] = offset.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        ["limit"] = "100",
                    },
                    ct
                );
                if (
                    page.Total < 0
                    || page.Items.Length > 100
                    || page.Items.Any(item => item.Literal != literal)
                )
                {
                    throw new ArgumentException(
                        "The reader returned invalid exact-literal coverage."
                    );
                }
                if (offset != 0 && total != page.Total)
                {
                    throw new NormalizationConflictException(
                        "Reader coverage changed during preview."
                    );
                }
                total = page.Total;
                foreach (var item in page.Items)
                {
                    if (!seen.Add(item.Reference))
                    {
                        throw new NormalizationConflictException(
                            "The reader repeated an account during preview."
                        );
                    }
                    evidenceBytes += System.Text.Encoding.UTF8.GetByteCount(item.OriginalJson!);
                    if (evidenceBytes > 3 * 1024 * 1024)
                    {
                        throw new ArgumentException(
                            "Exact-literal review evidence exceeds three MiB. This alias needs a smaller reviewed cohort."
                        );
                    }
                }
                items.AddRange(page.Items);
                offset += page.Items.Length;
                if (offset >= total)
                {
                    // Reader `complete` describes a single response containing the entire
                    // result. We establish union coverage from all nonrepeating pages.
                    complete = offset == total;
                    break;
                }
                if (page.Items.Length == 0)
                {
                    break;
                }
            }
            return new(items.ToArray(), total, complete);
        }

        private async Task<ReviewObservationPage> FetchAsync(
            string domain,
            string route,
            Dictionary<string, string?> query,
            CancellationToken ct
        )
        {
            var reader = domain switch
            {
                "venues" => configuration.VenueReader,
                "people" => configuration.PeopleReader,
                "compositions" or "songs" => configuration.MusicReader,
                _ => throw new ArgumentException("Unsupported review domain."),
            };
            query["domain"] = domain;
            var address = new Uri(
                new Uri(reader),
                route
                    + "?"
                    + string.Join(
                        "&",
                        query
                            .Where(pair => pair.Value != null)
                            .Select(pair =>
                                Uri.EscapeDataString(pair.Key)
                                + "="
                                + Uri.EscapeDataString(pair.Value!)
                            )
                    )
            );
            using var response = await http.GetAsync(
                address,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                throw new ArgumentException("The reader rejected these lookup coordinates.");
            }
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new NormalizationConflictException(
                    "This retained reader reference is unavailable."
                );
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var block = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(block, ct)) != 0)
            {
                if (buffer.Length + count > MaximumPreparationBytes)
                {
                    throw new ArgumentException("The reader response exceeds four MiB.");
                }
                buffer.Write(block, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var result =
                document.RootElement.Deserialize<ReviewObservationPage>(Json)
                ?? throw new ArgumentException("The reader returned no review data.");
            if (result.Items == null || result.Items.Any(item => item.Domain != domain))
            {
                throw new ArgumentException("The reader returned a mismatched review domain.");
            }
            var originals = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            return result with
            {
                Items = result
                    .Items.Select(
                        (item, index) => item with { OriginalJson = originals[index].GetRawText() }
                    )
                    .ToArray(),
            };
        }

        private static void ValidateReference(ReviewReference reference)
        {
            Required(reference.Domain, "reference domain");
            Required(reference.Namespace, "reference namespace");
            Required(reference.StableKey, "reference key");
            if (reference.ArtistUuid == Guid.Empty)
            {
                throw new ArgumentException("Reference artist UUID is invalid.");
            }
        }
    }
}
