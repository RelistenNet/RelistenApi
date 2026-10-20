using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Private operator UI for reviewed correspondences. It starts neither API Startup nor
/// ingestion jobs. Reader projections supply evidence; the durable knowledge service owns
/// decisions and history. Projection keys never acquire canonical UUID meaning here.
/// </summary>
public sealed partial class NormalizationReviewHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewHostConfiguration configuration;
    private readonly NormalizationExpertKnowledgeService knowledge;
    private readonly ReviewReaderBridge readers;
    private readonly ReviewTailnetAuthentication authentication;
    private ReviewedMusicProjection? musicProjection;

    private NormalizationReviewHost(
        ReviewHostConfiguration configuration,
        NormalizationExpertKnowledgeService knowledge,
        HttpClient http
    )
    {
        this.configuration = configuration;
        this.knowledge = knowledge;
        readers = new(configuration, http);
        authentication = new(configuration);
    }

    /// <summary>
    /// Runs only on the configured literal Tailscale address. Database access is limited
    /// to a dedicated localhost normalization database; callers are verified on every request.
    /// </summary>
    public static async Task RunAsync(string configurationPath, CancellationToken ct = default)
    {
        var configuration = await ReviewHostConfiguration.ReadAsync(configurationPath, ct);
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production }
        );
        builder.WebHost.UseUrls(configuration.BindUrl);
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = 1024 * 1024
        );
        var db = new Relisten.DbService(
            configuration.LocalDatabaseUrl(),
            null,
            null,
            builder.Environment,
            NullLogger<Relisten.DbService>.Instance
        );
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var host = new NormalizationReviewHost(
            configuration,
            new(db, new NormalizationWorkService(db), configuration.EvidenceRoot),
            http
        );
        if (configuration.MusicProjection is { } music)
        {
            host.musicProjection = await ReviewedMusicProjection.OpenAsync(
                music.InputManifest,
                music.InputManifestSha256,
                music.OutputManifest,
                music.OutputManifestSha256,
                ct
            );
        }
        await using var app = builder.Build();
        host.MapMiddleware(app);
        host.MapRoutes(app);
        await app.RunAsync(ct);
    }

    private void MapMiddleware(WebApplication app)
    {
        app.Use(
            async (context, next) =>
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; connect-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";
                try
                {
                    await authentication.AuthorizeAsync(context);
                    await next(context);
                }
                catch (ReviewHttpException error)
                {
                    await Error(context, error.StatusCode, error.Message);
                }
                catch (NormalizationConflictException error)
                {
                    await Error(context, StatusCodes.Status409Conflict, error.Message);
                }
                catch (Exception error)
                    when (error
                            is ArgumentException
                                or FormatException
                                or JsonException
                                or BadHttpRequestException
                    )
                {
                    await Error(context, StatusCodes.Status400BadRequest, error.Message);
                }
                catch (Exception error) when (error is HttpRequestException or IOException)
                {
                    await Error(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        "Review evidence is unavailable. No decision was changed."
                    );
                }
                catch (OperationCanceledException)
                    when (!context.RequestAborted.IsCancellationRequested)
                {
                    await Error(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        "The reader timed out. No decision was changed."
                    );
                }
            }
        );
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet(
            "/api/session",
            (HttpContext context) =>
            {
                var user = ReviewTailnetAuthentication.User(context);
                return Results.Json(
                    new { csrfToken = authentication.CsrfToken(user.Id), reviewer = user.LoginName }
                );
            }
        );
        app.MapGet("/api/reviews/context", (Func<HttpContext, Task<IResult>>)ContextAsync);
        app.MapGet("/api/reviews/candidates", (Func<HttpContext, Task<IResult>>)CandidatesAsync);
        app.MapGet("/api/reviews/queue", (Func<HttpContext, Task<IResult>>)QueueAsync);
        app.MapPost("/api/reviews/prepare", (Func<HttpContext, Task<IResult>>)PrepareAsync);
        app.MapPost("/api/reviews/apply", (Func<HttpContext, Task<IResult>>)ApplyAsync);
        app.MapGet("/api/reviews/history", (Func<HttpContext, Task<IResult>>)HistoryAsync);
        app.MapPost("/api/reviews/retract", (Func<HttpContext, Task<IResult>>)RetractAsync);
        app.MapGet(
            "/api/knowledge",
            (Func<HttpContext, Task<IResult>>)(
                async (HttpContext context) =>
                    Results.Json(await knowledge.ReadSnapshotAsync(context.RequestAborted), Json)
            )
        );
        app.MapPost("/api/evaluate", (Func<HttpContext, Task<IResult>>)EvaluateAsync);
        app.MapGet(
            "/api/music/review-projection",
            (Func<HttpContext, Task<IResult>>)MusicProjectionAsync
        );
        app.MapGet(
            "/",
            (HttpContext context) =>
                Asset(context, "normalization-review.html", "text/html; charset=utf-8")
        );
        app.MapGet(
            "/normalization-review.html",
            (HttpContext context) =>
                Asset(context, "normalization-review.html", "text/html; charset=utf-8")
        );
        app.MapGet(
            "/normalization-review.js",
            (HttpContext context) =>
                Asset(context, "normalization-review.js", "text/javascript; charset=utf-8")
        );
        app.MapGet(
            "/normalization-review.css",
            (HttpContext context) =>
                Asset(context, "normalization-review.css", "text/css; charset=utf-8")
        );
    }

    private async Task<IResult> ContextAsync(HttpContext context)
    {
        var reference = SourceReference(context.Request);
        var source = await readers.SourceAsync(reference, context.RequestAborted);
        ValidateSourceNamespace(source);
        var selector = AccountSelector(source);
        var accountRule = await knowledge.GetRuleAsync(
            RuleUuid(source.Domain, selector),
            context.RequestAborted
        );
        var snapshot = await knowledge.ReadSnapshotAsync(context.RequestAborted);
        var evaluation = new ExpertKnowledgeResolver(snapshot).Evaluate(source.Observation);
        var applicable = snapshot
            .Rules.Where(rule =>
                evaluation.AppliedRules.Any(applied => applied.RuleUuid == rule.RuleUuid)
            )
            .ToArray();
        var rule =
            applicable.Length == 1 && evaluation.Outcome != "conflict" ? applicable[0] : null;
        var hint = OptionalArtist(context.Request.Query["ruleUuid"].FirstOrDefault());
        var selected =
            hint == null
                ? rule ?? accountRule
                : await knowledge.GetRuleAsync(hint.Value, context.RequestAborted);
        if (hint != null && (selected == null || !RuleMatchesSource(selected, source)))
        {
            throw new ArgumentException(
                "This decision does not belong to the selected original account."
            );
        }
        var history =
            selected == null
                ? []
                : await knowledge.GetHistoryAsync(selected.RuleUuid, context.RequestAborted);
        return Results.Json(
            new
            {
                sourceRef = source.Reference,
                evidenceOrigin = readers.EvidenceOrigin(source.Domain),
                source = new
                {
                    label = source.DisplayName,
                    detail = source.Description,
                    source.Literal,
                    source.ArtistUuid,
                    source.Provider,
                    source.Locality,
                    source.Date,
                    evidenceUrl = readers.EvidenceUrl(source),
                },
                currentDecision = rule == null
                    ? null
                    : RuleDisplay(
                        rule,
                        selected?.RuleUuid == rule.RuleUuid ? history.LastOrDefault() : null,
                        await TargetLabelAsync(rule.Definition, context.RequestAborted)
                    ),
                selectedDecision = selected == null
                    ? null
                    : RuleDisplay(
                        selected,
                        history.LastOrDefault(),
                        await TargetLabelAsync(selected.Definition, context.RequestAborted)
                    ),
                applicableDecisions = applicable.Select(item =>
                    RuleDisplay(item, null, item.Definition.Target.StableKey)
                ),
                history = await HistoryDisplayAsync(history, context.RequestAborted),
                evaluation,
                warnings = evaluation.Outcome == "conflict"
                    ? new[] { "Reviewed decisions conflict. No current identity is selected." }
                    : [],
            },
            Json
        );
    }

    private async Task<IResult> CandidatesAsync(HttpContext context)
    {
        var domain = Required(context.Request.Query["domain"].ToString(), "domain");
        var query = Required(context.Request.Query["q"].ToString(), "candidate search");
        var page = await readers.CandidatesAsync(
            domain,
            query,
            OptionalArtist(
                context.Request.Query["artistUuid"].FirstOrDefault()
                    ?? context.Request.Query["artist"].FirstOrDefault()
            ),
            context.Request.Query["scope"].FirstOrDefault(),
            context.RequestAborted
        );
        return Results.Json(
            new
            {
                items = page.Items.Select(item => new
                {
                    targetRef = item.Reference,
                    label = item.DisplayName,
                    detail = item.Description,
                    evidenceUrl = readers.EvidenceUrl(item),
                }),
                evidenceOrigin = readers.EvidenceOrigin(domain),
                page.Complete,
                omittedCount = Math.Max(0, page.Total - page.Items.Length),
            },
            Json
        );
    }

    private async Task<IResult> HistoryAsync(HttpContext context)
    {
        var uuid = Guid.Parse(Required(context.Request.Query["ruleUuid"].ToString(), "rule UUID"));
        return Results.Json(
            new
            {
                items = await HistoryDisplayAsync(
                    await knowledge.GetHistoryAsync(uuid, context.RequestAborted),
                    context.RequestAborted
                ),
            },
            Json
        );
    }

    private async Task<IResult> RetractAsync(HttpContext context)
    {
        var request = await Read<ReviewRetraction>(context);
        var result = await knowledge.SaveAsync(
            new(
                request.OperationUuid,
                request.RuleUuid,
                request.ExpectedRevision,
                "retract",
                null,
                request.Reason
            ),
            ReviewTailnetAuthentication.User(context).LoginName,
            context.RequestAborted
        );
        return Results.Json(result, Json);
    }

    private async Task<IResult> EvaluateAsync(HttpContext context)
    {
        var request = await Read<ReviewEvaluationRequest>(context);
        if (
            request.Observations == null
            || request.Observations.Length > 1000
            || request.Observations.Any(item => item == null)
        )
        {
            throw new ArgumentException("Evaluate at most 1,000 observations per request.");
        }
        var snapshot = await knowledge.ReadSnapshotAsync(context.RequestAborted);
        var resolver = new ExpertKnowledgeResolver(snapshot);
        return Results.Json(
            new
            {
                snapshotSha256 = snapshot.Sha256,
                items = request.Observations.Select(item => new
                {
                    observation = item,
                    evaluation = resolver.Evaluate(item),
                }),
            },
            Json
        );
    }

    private Task Asset(HttpContext context, string name, string contentType)
    {
        context.Response.ContentType = contentType;
        return context.Response.SendFileAsync(
            Path.Combine(configuration.AssetsDirectory!, name),
            context.RequestAborted
        );
    }

    private static async Task Error(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException("Review response failed after it began.");
        }
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }

    private static async Task<T> Read<T>(HttpContext context)
        where T : class =>
        await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, context.RequestAborted)
        ?? throw new ArgumentException("A JSON request is required.");

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value != value.Trim())
        {
            throw new ArgumentException("A bounded " + name + " is required.");
        }
        return value;
    }

    private static ReviewReference SourceReference(HttpRequest request) =>
        new(
            Required(request.Query["domain"].ToString(), "domain"),
            Required(request.Query["namespace"].ToString(), "namespace"),
            Required(
                (
                    request.Query["stableKey"].FirstOrDefault()
                    ?? request.Query["key"].FirstOrDefault()
                ),
                "source key"
            ),
            OptionalArtist(
                request.Query["artistUuid"].FirstOrDefault()
                    ?? request.Query["artist"].FirstOrDefault()
            )
        );

    private static Guid? OptionalArtist(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        return Guid.TryParse(value, out var uuid) && uuid != Guid.Empty
            ? uuid
            : throw new ArgumentException("Artist UUID is invalid.");
    }

    private static bool RuleMatchesSource(ExpertKnowledgeRule rule, ReviewReaderItem source)
    {
        // This display-only copy checks a historical selector without reactivating it.
        var display = rule with
        {
            State = "accepted",
        };
        var resolver = new ExpertKnowledgeResolver(
            ExpertKnowledgeResolver.CreateSnapshot([display])
        );
        return resolver
            .Evaluate(source.Observation)
            .AppliedRules.Any(item => item.RuleUuid == rule.RuleUuid);
    }

    private async Task<string> TargetLabelAsync(
        ExpertKnowledgeDefinition definition,
        CancellationToken ct
    )
    {
        var pin = definition.Evidence[0];
        if (
            pin.Bytes <= MaximumPreparationBytes
            && Path.GetFullPath(pin.Path)
                .StartsWith(
                    configuration.EvidenceRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
        )
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(pin.Path, ct);
                if (
                    bytes.LongLength == pin.Bytes
                    && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))
                        == pin.Sha256
                )
                {
                    var preparation = JsonSerializer.Deserialize<ReviewPreparation>(bytes, Json);
                    if (preparation != null)
                    {
                        return preparation.Target.DisplayName;
                    }
                }
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                // History remains readable when its frozen target is no longer available.
            }
        }
        return definition.Target.StableKey;
    }

    private static object RuleDisplay(
        ExpertKnowledgeRule rule,
        ExpertKnowledgeHistoryEntry? latest,
        string targetLabel
    ) =>
        new
        {
            rule.RuleUuid,
            rule.Revision,
            rule.State,
            action = rule.State == "accepted" ? "accept"
            : rule.State == "rejected" ? "reject"
            : "retract",
            scope = rule.Definition.Selector.AccountStableKey == null ? "alias" : "account",
            targetLabel,
            reason = latest?.Reason,
            reviewer = latest?.Operator,
            createdAt = latest?.CreatedAt,
        };

    private async Task<object[]> HistoryDisplayAsync(
        IReadOnlyList<ExpertKnowledgeHistoryEntry> history,
        CancellationToken ct
    )
    {
        var result = new List<object>();
        ExpertKnowledgeDefinition? definition = null;
        string? targetLabel = null;
        foreach (var entry in history.OrderBy(item => item.Revision))
        {
            if (entry.Command.Definition != null)
            {
                definition = entry.Command.Definition;
                targetLabel = await TargetLabelAsync(definition, ct);
            }
            // Retraction intentionally saves no replacement definition. Display the
            // nearest earlier definition while preserving the original ledger command.
            if (definition == null)
            {
                throw new InvalidOperationException("Review history has no original definition.");
            }
            result.Add(
                new
                {
                    entry.OperationUuid,
                    entry.Revision,
                    entry.Action,
                    entry.Reason,
                    reviewer = entry.Operator,
                    entry.CreatedAt,
                    scope = definition.Selector.AccountStableKey == null ? "alias" : "account",
                    targetLabel,
                }
            );
        }
        return result.ToArray();
    }

    private sealed record ReviewRetraction(
        Guid OperationUuid,
        Guid RuleUuid,
        long ExpectedRevision,
        string Reason
    );

    private sealed record ReviewEvaluationRequest(ExpertKnowledgeObservation[] Observations);
}
