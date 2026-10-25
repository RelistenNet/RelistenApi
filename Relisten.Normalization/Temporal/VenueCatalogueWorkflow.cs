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
/// different states or countries.
public sealed record BuildVenueCatalogue(
    IReadOnlyList<Guid>? Artists = null,
    int? MaxChunks = null,
    bool FlaggedOnly = false,
    int HistoryMinShows = 5,
    int Parallel = 6
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
        if (request.FlaggedOnly)
        {
            var flagged = await Workflow.ExecuteActivityAsync(
                (VenueMatchActivities a) => a.FlaggedArtists(),
                Step
            );
            if (flagged.Count == 0)
            {
                return new VenueCatalogueResult(0, 0, 0, 0, 0, 0);
            }

            request = request with { Artists = flagged, FlaggedOnly = false };
        }

        var first = await Workflow.ExecuteActivityAsync(
            (VenueActivities a) => a.Match(request.Artists),
            Step
        );
        var total = new VenueCatalogueResult(0, first.VenuesCreated, first.RowsMapped, 0, 0, 0);
        var skip = new List<string>();
        var failures = 0;
        while (request.MaxChunks is not { } max || total.Chunks < max)
        {
            VenueChunkResult chunk;
            // At most the budget's remaining calls at once; a re-ask round only when it fits too.
            var remaining = request.MaxChunks is { } max2 ? max2 - total.Chunks : int.MaxValue;
            var parallel = Math.Max(1, Math.Min(request.Parallel, remaining));
            var input = new VenueChunkInput(
                request.Artists,
                skip,
                remaining >= 2 * parallel,
                parallel
            );
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
                // A failed step may have made every call it could: all of them count.
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

        var last = await Workflow.ExecuteActivityAsync(
            (VenueActivities a) => a.Match(request.Artists),
            Step
        );
        total = total with
        {
            VenuesCreated = total.VenuesCreated + last.VenuesCreated,
            RowsMapped = total.RowsMapped + last.RowsMapped,
            FirstMatch = first,
            LastMatch = last,
        };

        // The names of the venues the model never saw, while the budget lasts.
        failures = 0;
        while (
            request.HistoryMinShows > 0 && (request.MaxChunks is not { } cap || total.Chunks < cap)
        )
        {
            VenueHistoryResult history;
            var minShows = request.HistoryMinShows;
            var artists = request.Artists;
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

        // History merges renamed twins: their shows move to the survivor.
        if (total.VenuesNamed > 0)
        {
            total = total with
            {
                LastMatch = await Workflow.ExecuteActivityAsync(
                    (VenueActivities a) => a.Match(request.Artists),
                    Step
                ),
            };
        }

        return total;
    }
}
