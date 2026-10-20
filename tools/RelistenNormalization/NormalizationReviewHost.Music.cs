using Microsoft.AspNetCore.Http;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class NormalizationReviewHost
{
    private sealed record ReviewMusicConfiguration(
        string InputManifest,
        string InputManifestSha256,
        string OutputManifest,
        string OutputManifestSha256
    )
    {
        internal ReviewMusicConfiguration Resolve(string directory) =>
            this with
            {
                InputManifest = Path.GetFullPath(InputManifest, directory),
                OutputManifest = Path.GetFullPath(OutputManifest, directory),
            };
    }

    private async Task<IResult> MusicProjectionAsync(HttpContext context)
    {
        if (musicProjection == null)
        {
            throw new ReviewHttpException(
                503,
                "Live song corrections require a configured completed music bundle."
            );
        }
        var query = context.Request.Query;
        if (
            query["inputManifestSha256"] != musicProjection.InputManifestSha256
            || query["outputManifestSha256"] != musicProjection.OutputManifestSha256
        )
        {
            throw new NormalizationConflictException(
                "The music reader and correction host use different completed bundles."
            );
        }
        var snapshot = await knowledge.ReadSnapshotAsync(context.RequestAborted);
        if (query["knowledgeSha256"] != snapshot.Sha256)
        {
            throw new NormalizationConflictException(
                "Reviewed knowledge changed. Reload the music page."
            );
        }
        var offset = int.Parse(query["offset"].FirstOrDefault() ?? "0");
        return Results.Json(
            await musicProjection.ReadAsync(snapshot, offset, context.RequestAborted),
            ShadowCatalogRun.Json
        );
    }
}
