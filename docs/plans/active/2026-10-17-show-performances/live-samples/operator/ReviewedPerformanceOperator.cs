using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;

/// <summary>
/// Applies a finite operator-reviewed performance assignment from retained description
/// headings. Current provider evidence must still match before any source moves. Assignment
/// order records which supported performance claims an existing date's original show UUID.
/// </summary>
sealed class ReviewedPerformanceOperator(IServiceProvider services, JsonSerializerOptions json)
{
    private readonly SourceService sources = services.GetRequiredService<SourceService>();
    private readonly ArchivePerformerReviewService performerReviews = new(
        services.GetRequiredService<DbService>()
    );

    public async Task RunAsync(string planFile, string rawDirectory, string output)
    {
        var plan =
            JsonSerializer.Deserialize<PerformanceReviewPlan>(File.ReadAllText(planFile), json)
            ?? throw new ArgumentException("A performance review plan is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ReviewBasis);
        var artist =
            await services
                .GetRequiredService<ArtistService>()
                .FindArtistForImport(plan.ArtistIdOrSlug)
            ?? throw new ArgumentException("The reviewed performing artist must already exist.");
        var checkedItems = await CheckEvidenceAsync(plan, artist, rawDirectory);
        var assignments = await AssignAsync(plan, artist, checkedItems, output);

        var handle = await services
            .GetRequiredService<TemporalIngestionClient>()
            .RebuildArtistCatalogAsync(new(new(artist.uuid)));
        Save(
            output,
            new
            {
                artistUuid = artist.uuid,
                assignments,
                handle.WorkflowId,
                handle.ResultRunId,
                status = "rebuilding",
            }
        );
        var result = await handle.GetResultAsync();
        Save(
            output,
            new
            {
                artistUuid = artist.uuid,
                assignments,
                handle.WorkflowId,
                handle.ResultRunId,
                result,
            }
        );
    }

    private async Task<List<CheckedPerformanceItem>> CheckEvidenceAsync(
        PerformanceReviewPlan plan,
        Artist artist,
        string rawDirectory
    )
    {
        var checkedItems = new List<CheckedPerformanceItem>();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Relisten/1.0");

        foreach (var item in plan.Items)
        {
            var type = item.PerformanceType switch
            {
                "early" => PerformanceType.Early,
                "late" => PerformanceType.Late,
                "afternoon" => PerformanceType.Afternoon,
                _ => throw new ArgumentException(
                    "This reviewed-heading command supports early, late or afternoon performances."
                ),
            };
            var current = await ReadCurrentObservationAsync(item, rawDirectory, http);
            CheckHeading(item, current);
            var source = await CheckPublishedPerformerAsync(item, artist, current);
            checkedItems.Add(new(item, source, current, type));
        }

        return checkedItems;
    }

    private static async Task<ArchiveObservationProjection> ReadCurrentObservationAsync(
        PerformanceReviewItem item,
        string rawDirectory,
        HttpClient http
    )
    {
        var raw = await File.ReadAllBytesAsync(
            Path.Combine(rawDirectory, item.Identifier + ".json")
        );
        if (
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(raw)),
                item.MetadataSha256,
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "The retained metadata file differs from the reviewed plan."
            );
        }
        var retained = ArchiveObservationProjector.Project(
            new(item.Identifier),
            System.Text.Encoding.UTF8.GetString(raw)
        );
        var currentRaw = await http.GetStringAsync(
            "https://archive.org/metadata/" + Uri.EscapeDataString(item.Identifier)
        );
        var current = ArchiveObservationProjector.Project(new(item.Identifier), currentRaw);
        var retainedPayload = JObject.Parse(retained.PayloadJson);
        var currentPayload = JObject.Parse(current.PayloadJson);
        if (
            item.Evidence.Any(support =>
                !JToken.DeepEquals(
                    retainedPayload.SelectToken(support.Locator),
                    currentPayload.SelectToken(support.Locator)
                )
            )
        )
        {
            throw new InvalidOperationException(
                "A reviewed billing field changed; review its current wording before moving this source."
            );
        }

        return current;
    }

    private async Task<Source> CheckPublishedPerformerAsync(
        PerformanceReviewItem item,
        Artist artist,
        ArchiveObservationProjection current
    )
    {
        var currentPayload = JObject.Parse(current.PayloadJson);
        var source =
            await sources.ForUpstreamIdentifierFromPrimary(artist, item.Identifier)
            ?? throw new ArgumentException(
                "The reviewed item must already be published under this artist."
            );
        if (
            source.display_date != item.DisplayDate
            || item.ExpectedSourceUuid is { } expected && expected != source.uuid
        )
        {
            throw new ArgumentException(
                "The published source differs from the reviewed date or source identity."
            );
        }
        var performer = await performerReviews.FindPerformerDecisionAsync(item.Identifier);
        if (
            performer == null
            || !performer.Approved
            || performer.SelectedArtistUuid != artist.uuid
            || performer.Alternatives.Length != 0
        )
        {
            throw new ArgumentException(
                "The item needs an approved resolved performer assignment to this artist."
            );
        }
        var performerBasis = JObject.Parse(performer.SupportingPayloadJson);
        if (
            !JToken.DeepEquals(
                performerBasis.SelectToken("metadata.creator"),
                currentPayload.SelectToken("metadata.creator")
            )
            || performer.Evidence.Any(support =>
                !JToken.DeepEquals(
                    performerBasis.SelectToken(support.Locator),
                    currentPayload.SelectToken(support.Locator)
                )
            )
        )
        {
            throw new ArgumentException(
                "The performer review no longer applies to the current billing fields."
            );
        }
        return source;
    }

    private static void CheckHeading(
        PerformanceReviewItem item,
        ArchiveObservationProjection current
    )
    {
        var metadata = JObject.Parse(current.PayloadJson)["metadata"]!;
        foreach (var support in item.Evidence)
        {
            if (
                support.Locator != "metadata.description"
                || support.Start is not { } start
                || support.Length is not { } length
            )
            {
                throw new ArgumentException(
                    "Retain exact description heading spans for this review."
                );
            }
            var field = metadata["description"]?.Value<string>();
            if (
                field == null
                || start < 0
                || length < 1
                || start > field.Length - length
                || field.Substring(start, length) != support.Value
            )
            {
                throw new ArgumentException(
                    "A reviewed heading does not match the current metadata span."
                );
            }
        }
        if (
            !item.Evidence.Any(support =>
                Regex.IsMatch(
                    support.Value,
                    @"\b" + item.PerformanceType + @"\s+(?:show|performance)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
        )
        {
            throw new ArgumentException(
                "The evidence must explicitly name the reviewed performance category."
            );
        }
    }

    private async Task<List<object>> AssignAsync(
        PerformanceReviewPlan plan,
        Artist artist,
        List<CheckedPerformanceItem> checkedItems,
        string output
    )
    {
        var intake = services.GetRequiredService<ArchiveIntakeService>();
        var shows = services.GetRequiredService<ShowPerformanceService>();
        var assignments = new List<object>();
        foreach (var item in checkedItems)
        {
            var observation = await intake.RetainObservationAsync(item.Current);
            var evidence = new
            {
                provider = "archive.org",
                assignment_basis = "operator_reviewed_description_heading",
                item_identifier = item.Review.Identifier,
                supporting_observation_id = observation,
                supporting_content_hash = item.Current.ContentHash,
                retained_metadata_sha256 = item.Review.MetadataSha256,
                review_basis = plan.ReviewBasis,
                evidence = item.Review.Evidence,
                reviewed_fields = ReviewedFieldHashes(item),
            };
            var show = await shows.AssignSourceAsync(
                artist,
                item.Source.id,
                item.Review.PerformanceKey,
                item.Review.DisplayDate,
                item.Type,
                item.Review.PerformanceLabel,
                JsonSerializer.Serialize(evidence, json)
            );
            assignments.Add(
                new
                {
                    item.Review.Identifier,
                    sourceUuid = item.Source.uuid,
                    showUuid = show.uuid,
                    performanceType = item.Review.PerformanceType,
                    observationId = observation,
                    contentHash = item.Current.ContentHash,
                }
            );
            Save(
                output,
                new
                {
                    artistUuid = artist.uuid,
                    assignments,
                    status = "assigning",
                }
            );
        }

        return assignments;
    }

    private static Dictionary<string, string> ReviewedFieldHashes(CheckedPerformanceItem item)
    {
        var payload = JObject.Parse(item.Current.PayloadJson);
        var hashes = new Dictionary<string, string>();
        foreach (var locator in item.Review.Evidence.Select(support => support.Locator).Distinct())
        {
            var field = payload.SelectToken(locator)!.Value<string>()!;
            hashes[locator] = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(field))
            );
        }

        return hashes;
    }

    private void Save(string path, object value)
    {
        File.WriteAllText(
            path + ".tmp",
            JsonSerializer.Serialize(value, json) + Environment.NewLine
        );
        File.Move(path + ".tmp", path, true);
    }
}

sealed record PerformanceReviewPlan(
    string ArtistIdOrSlug,
    string ReviewBasis,
    PerformanceReviewItem[] Items
);

sealed record PerformanceReviewItem(
    string Identifier,
    string MetadataSha256,
    string PerformanceKey,
    string DisplayDate,
    string PerformanceType,
    string PerformanceLabel,
    TapersSectionEvidence[] Evidence,
    Guid? ExpectedSourceUuid = null
);

sealed record CheckedPerformanceItem(
    PerformanceReviewItem Review,
    Source Source,
    ArchiveObservationProjection Current,
    PerformanceType Type
);
