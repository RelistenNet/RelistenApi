using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Relisten.Normalization.Venues;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Normalization.Temporal;

/// Artists limits the legacy rows considered (null: every artist); venues themselves are
/// cross-artist, so a later run for other artists maps onto the venues this one made.
/// MaxChunks caps the run's model calls, a chunk's re-ask included (null: until nothing is
/// left to ask). FlaggedOnly: the artists whose venues are switched on (features.norm_venues),
/// read when the run starts; the nightly schedule's run. HistoryMinShows: after the match,
/// the official names of venues with at least this many shows that have none (VenueHistory),
/// within the same budget; 0 skips it. Parallel: model requests asked at once, each about
/// different states or countries. Reask ("Name@Location" or "Name") and Rematch (every row an
/// older matcher placed) first forget those placements of the run's artists, so the run
/// places them again; re-asked rows go to the model.
public sealed record BuildVenueCatalogue(
    IReadOnlyList<Guid>? Artists = null,
    int? MaxChunks = null,
    bool FlaggedOnly = false,
    int HistoryMinShows = 5,
    int Parallel = 6,
    IReadOnlyList<string>? Reask = null,
    bool Rematch = false
);

public sealed record VenueCatalogueResult(
    int Chunks,
    int VenuesCreated,
    int RowsMapped,
    int Unsure,
    int InputTokens,
    int OutputTokens
)
{
    public VenueMatchResult? FirstMatch { get; init; }
    public VenueMatchResult? LastMatch { get; init; }
    public int VenuesNamed { get; init; }
    public int RowsForgotten { get; init; }
    public int VenuesRetired { get; init; }
}

/// Builds or extends the venue catalogue (string-theory docs/matching.md T3): the
/// deterministic match, the LLM one locality at a time for what it leaves, then the match
/// again, which places every show's venue in norm. Started by hand
/// (worker --norm-venues=all|<artist>) or nightly for the switched-on artists
/// (NormalizationClient.RegisterSchedulesAsync).
[Workflow]
public class VenueCatalogueWorkflow
{
    private static readonly ActivityOptions Step = new()
    {
        TaskQueue = NormalizationQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(10),
            NonRetryableErrorTypes = ["InvalidOperationException"],
        },
    };

    // A chunk calls the model before it writes: a retry would be another paid call, so a
    // failed chunk counts against MaxChunks instead.
    private static readonly ActivityOptions ChunkStep = new()
    {
        TaskQueue = NormalizationQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    };

    private const int FailuresInARow = 3;

    [WorkflowRun]
    public async Task<VenueCatalogueResult> RunAsync(BuildVenueCatalogue request)
    {
        var artists = await ArtistsAsync(request);
        if (artists is { Count: 0 })
        {
            return new VenueCatalogueResult(0, 0, 0, 0, 0, 0);
        }

        request = request with { Artists = artists, FlaggedOnly = false };
        var forgetInput =
            (request.Reask is { Count: > 0 } || request.Rematch) && artists != null
                ? new VenueForgetInput(artists, request.Reask ?? [], request.Rematch)
                : null;
        // Chosen first, then forgotten: a retried forget can't lose what it chose.
        var forget =
            forgetInput != null
                ? await Workflow.ExecuteActivityAsync(
                    (VenueActivities a) => a.PlanForget(forgetInput),
                    Step
                )
                : null;
        if (forget is { Rows.Count: > 0 })
        {
            await Workflow.ExecuteActivityAsync((VenueActivities a) => a.Forget(forget.Rows), Step);
        }

        var held = forget?.Held is { Count: > 0 } h ? h : null;
        var first = await MatchAsync(request.Artists, held);
        var total = new VenueCatalogueResult(0, first.VenuesCreated, first.RowsMapped, 0, 0, 0)
        {
            FirstMatch = first,
        };
        total = await AskAboutSpellingsAsync(request, total, held);
        var last = await MatchAsync(request.Artists, held);
        total = total with
        {
            VenuesCreated = total.VenuesCreated + last.VenuesCreated,
            RowsMapped = total.RowsMapped + last.RowsMapped,
            LastMatch = last,
        };
        total = await NameVenuesAsync(request, total);

        // History merges renamed twins: their shows move to the survivor.
        if (total.VenuesNamed > 0)
        {
            total = total with { LastMatch = await MatchAsync(request.Artists, held) };
        }

        if (forget == null)
        {
            return total;
        }

        return total with
        {
            RowsForgotten = forget.Rows.Count,
            VenuesRetired =
                forget.Venues.Count > 0
                    ? await Workflow.ExecuteActivityAsync(
                        (VenueActivities a) => a.Retire(forget.Venues),
                        Step
                    )
                    : 0,
        };
    }

    /// The run's artists: the request's (null: every artist), or the flagged ones.
    private static async Task<IReadOnlyList<Guid>?> ArtistsAsync(BuildVenueCatalogue request) =>
        request.FlaggedOnly
            ? await Workflow.ExecuteActivityAsync(
                (VenueMatchActivities a) => a.FlaggedArtists(),
                Step
            )
            : request.Artists;

    private static Task<VenueMatchResult> MatchAsync(
        IReadOnlyList<Guid>? artists,
        IReadOnlyList<Guid>? held
    ) => Workflow.ExecuteActivityAsync((VenueActivities a) => a.Match(artists, held), Step);

    private static bool WithinBudget(BuildVenueCatalogue request, VenueCatalogueResult total) =>
        request.MaxChunks is not { } max || total.Chunks < max;

    /// The model for the spellings the match left, step by step until none is left or the
    /// budget is spent. Each step asks up to Parallel requests at once (never more than the
    /// budget has left) and a re-ask round only when that fits too. A failed step counts every
    /// call it could have made; three failures in a row end the run.
    private static async Task<VenueCatalogueResult> AskAboutSpellingsAsync(
        BuildVenueCatalogue request,
        VenueCatalogueResult total,
        IReadOnlyList<Guid>? held
    )
    {
        var skip = new List<string>();
        var failures = 0;
        while (WithinBudget(request, total))
        {
            var remaining = request.MaxChunks is { } max ? max - total.Chunks : int.MaxValue;
            var parallel = Math.Max(1, Math.Min(request.Parallel, remaining));
            var input = new VenueChunkInput(
                request.Artists,
                skip,
                remaining >= 2 * parallel,
                parallel,
                held
            );
            VenueChunkResult chunk;
            try
            {
                chunk = await Workflow.ExecuteActivityAsync(
                    (VenueActivities a) => a.Chunk(input),
                    ChunkStep
                );
                failures = 0;
            }
            catch (ActivityFailureException) when (++failures < FailuresInARow)
            {
                total = total with
                {
                    Chunks = total.Chunks + input.Parallel * (input.MayRetry ? 2 : 1),
                };
                continue;
            }

            if (chunk.Done)
            {
                break;
            }

            skip.AddRange(chunk.Asked);
            total = total with
            {
                Chunks = total.Chunks + chunk.ModelCalls,
                VenuesCreated = total.VenuesCreated + chunk.VenuesCreated,
                RowsMapped = total.RowsMapped + chunk.RowsMapped,
                Unsure = total.Unsure + chunk.Unsure,
                InputTokens = total.InputTokens + chunk.InputTokens,
                OutputTokens = total.OutputTokens + chunk.OutputTokens,
            };
        }

        return total;
    }

    /// Official names for the run's venues the model never saw, one request at a time while
    /// the budget lasts (HistoryMinShows 0 skips it); failures count and end the run as above.
    private static async Task<VenueCatalogueResult> NameVenuesAsync(
        BuildVenueCatalogue request,
        VenueCatalogueResult total
    )
    {
        var (minShows, artists) = (request.HistoryMinShows, request.Artists);
        var failures = 0;
        while (minShows > 0 && WithinBudget(request, total))
        {
            VenueHistoryResult history;
            try
            {
                history = await Workflow.ExecuteActivityAsync(
                    (VenueActivities a) => a.History(minShows, artists),
                    ChunkStep
                );
                failures = 0;
            }
            catch (ActivityFailureException) when (++failures < FailuresInARow)
            {
                total = total with { Chunks = total.Chunks + 1 };
                continue;
            }

            if (history.Done)
            {
                break;
            }

            total = total with
            {
                Chunks = total.Chunks + 1,
                VenuesNamed = total.VenuesNamed + history.Named,
                Unsure = total.Unsure + history.Unsure,
                InputTokens = total.InputTokens + history.InputTokens,
                OutputTokens = total.OutputTokens + history.OutputTokens,
            };
        }

        return total;
    }
}
