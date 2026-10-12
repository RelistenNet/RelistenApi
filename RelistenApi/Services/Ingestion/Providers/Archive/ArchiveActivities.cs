using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.Archive;

/// <summary>Archive I/O and catalog transactions; each invocation receives a fresh importer scope.</summary>
public sealed class ArchiveActivities(ProviderActivityRunner runner)
{
    [Activity("Archive.DiscoverRecordings")]
    public Task<ArchiveSelection> DiscoverRecordings(ProviderWorkflowContext context, int? year) =>
        runner.RunAsync<ArchiveOrgImporter, ArchiveSelection>(
            context,
            UpstreamProvider.ArchiveOrg,
            (i, c) => i.DiscoverRecordingsAsync(c, year)
        );

    [Activity("Archive.ImportItem")]
    public Task<ArchiveItemResult> ImportItem(
        ProviderWorkflowContext context,
        ArchiveItemInput input
    ) =>
        runner.RunAsync<ArchiveOrgImporter, ArchiveItemResult>(
            context,
            UpstreamProvider.ArchiveOrg,
            (i, c) => i.ImportArchiveItemAsync(c, input)
        );

    [Activity("Archive.VerifyDeletionCandidates")]
    public Task<ArchiveDeletionPlan> VerifyDeletionCandidates(
        ProviderWorkflowContext context,
        ArchiveSelection selection,
        ArchiveItemId[] noMp3
    ) =>
        runner.RunAsync<ArchiveOrgImporter, ArchiveDeletionPlan>(
            context,
            UpstreamProvider.ArchiveOrg,
            (i, c) => i.VerifyDeletionsAsync(c, selection, noMp3)
        );

    [Activity("Archive.ApplyVerifiedDeletions")]
    public Task<ArchiveDeletionResult> ApplyVerifiedDeletions(
        ProviderWorkflowContext context,
        ArchiveDeletionPlan plan
    ) =>
        runner.RunAsync<ArchiveOrgImporter, ArchiveDeletionResult>(
            context,
            UpstreamProvider.ArchiveOrg,
            (i, c) => i.ApplyDeletionsAsync(c, plan)
        );
}
