using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Kb.Catalogue;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// MaxChunks caps the LLM calls of one run (null: until nothing is left to ask). Reask lists
/// titles to ask about again (a targeted re-ask): their machine decisions are forgotten first
/// and the run asks about nothing else.
public sealed record BuildKbCatalogue(
    ArtistId Artist,
    int? MaxChunks = null,
    IReadOnlyList<string>? Reask = null
);

/// TracksPerChunk is the run's returns curve; StillUnsure the recurring titles that neither
/// pass resolved, most-played first (at most 50), for a person.
public sealed record KbCatalogueResult(
    int Chunks,
    int SongsCreated,
    int KeysResolved,
    int KeysUnresolved,
    int TracksWritten,
    int InputTokens,
    int OutputTokens
)
{
    public IReadOnlyList<int> TracksPerChunk { get; init; } = [];
    public IReadOnlyList<ReviewItem> StillUnsure { get; init; } = [];
}

/// Builds or extends one artist's song catalogue with the LLM (string-theory docs/llm.md §3),
/// then places every track it can. Started by hand (worker --kb-llm-catalogue); a finished
/// band's catalogue may be built once, a touring band's re-run as titles arrive. Each run
/// asks only about titles nothing resolves yet, and never changes an existing row.
[Workflow]
public class KbCatalogueWorkflow
{
    private static readonly ActivityOptions Step = new()
    {
        TaskQueue = IngestionQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(10),
            NonRetryableErrorTypes = ["InvalidOperationException"],
        },
    };

    [WorkflowRun]
    public async Task<KbCatalogueResult> RunAsync(BuildKbCatalogue request)
    {
        var artist = request.Artist.Value;
        var total = new KbCatalogueResult(0, 0, 0, 0, 0, 0, 0);
        var curve = new List<int>();
        // The recurring titles left unsure, by key: a first-pass item stays unless the review
        // pass asks about it again, so a run whose cap ends in the first pass still lists them.
        var unsure = new Dictionary<string, ReviewItem>();

        var only = request.Reask is { Count: > 0 } reask ? reask : null;
        if (only != null)
        {
            await Workflow.ExecuteActivityAsync(
                (KbActivities a) => a.ForgetDecisions(new KbForgetInput(artist, only)),
                Step
            );
        }

        // First pass: every unresolved title (or only the re-asked ones), most-played first.
        total = await Pass(artist, review: false, request.MaxChunks, total, curve, unsure, only);

        // Review pass: the recurring titles still unresolved, asked again with the titles
        // around them as context.
        // MaxChunks is the run's whole budget: the review pass gets what the first pass left.
        var remaining = request.MaxChunks is { } cap ? Math.Max(0, cap - total.Chunks) : (int?)null;
        if (remaining != 0)
        {
            total = await Pass(artist, review: true, remaining, total, curve, unsure, only);
        }

        var matched = await Workflow.ExecuteActivityAsync(
            (KbActivities a) => a.MatchTracks(artist),
            Step
        );
        return total with
        {
            TracksWritten = total.TracksWritten + matched.TracksWritten,
            TracksPerChunk = curve,
            StillUnsure = unsure.Values.OrderByDescending(r => r.Shows).Take(50).ToList(),
        };
    }

    private static async Task<KbCatalogueResult> Pass(
        Guid artist,
        bool review,
        int? maxChunks,
        KbCatalogueResult total,
        List<int> curve,
        Dictionary<string, ReviewItem> unsure,
        IReadOnlyList<string>? only
    )
    {
        // No key is asked twice in a pass, sure or not.
        var skip = new List<string>();
        for (var chunks = 0; maxChunks is not { } max || chunks < max; chunks++)
        {
            var chunk = await Workflow.ExecuteActivityAsync(
                (KbActivities a) =>
                    a.CatalogueChunk(new KbCatalogueChunkInput(artist, skip, review, only)),
                Step
            );
            if (chunk.Done)
            {
                break;
            }

            skip.AddRange(chunk.Asked);
            curve.Add(chunk.TracksWritten);
            foreach (var key in chunk.Asked)
            {
                unsure.Remove(key);
            }
            foreach (var item in chunk.Review)
            {
                unsure[item.Key] = item;
            }
            total = total with
            {
                Chunks = total.Chunks + 1,
                SongsCreated = total.SongsCreated + chunk.SongsCreated,
                KeysResolved = total.KeysResolved + chunk.KeysResolved,
                KeysUnresolved = total.KeysUnresolved + chunk.Unresolved.Count,
                TracksWritten = total.TracksWritten + chunk.TracksWritten,
                InputTokens = total.InputTokens + chunk.InputTokens,
                OutputTokens = total.OutputTokens + chunk.OutputTokens,
            };
        }

        return total;
    }
}
