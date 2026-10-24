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
/// left to ask).
public sealed record BuildVenueCatalogue(
    IReadOnlyList<Guid>? Artists = null,
    int? MaxChunks = null
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
}

/// Builds or extends the venue catalogue (string-theory docs/matching.md T3): the
/// deterministic match, the LLM one locality at a time for what it leaves, then the match
/// again, which places every show's venue in norm. Started by hand
/// (worker --norm-venues=all|<artist>), like the song catalogue.
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
            // A re-ask is a second model call: only when the budget has it.
            var input = new VenueChunkInput(
                request.Artists,
                skip,
                request.MaxChunks == null || total.Chunks + 2 <= request.MaxChunks
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
                total = total with { Chunks = total.Chunks + 1 };
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
        return total with
        {
            VenuesCreated = total.VenuesCreated + last.VenuesCreated,
            RowsMapped = total.RowsMapped + last.RowsMapped,
            FirstMatch = first,
            LastMatch = last,
        };
    }
}
