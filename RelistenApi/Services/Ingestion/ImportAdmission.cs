using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Ingestion;

/// <summary>Catalog facts required to validate an import before starting durable work.</summary>
public interface IImportAdmissionCatalog
{
    Task<UpstreamProvider[]?> ProvidersForArtistAsync(ArtistId artist);

    /// <summary>Checks durable intake membership independently of provider collection mappings.</summary>
    Task<bool> IsTapersSectionCohortArtistAsync(ArtistId artist);
}

/// <summary>The singleton client resolves scoped catalog services only during admission.</summary>
public sealed class ImportAdmissionCatalog(IServiceScopeFactory scopes) : IImportAdmissionCatalog
{
    public async Task<bool> IsTapersSectionCohortArtistAsync(ArtistId artist)
    {
        using var scope = scopes.CreateScope();
        return await scope
            .ServiceProvider.GetRequiredService<ArchiveIntakeService>()
            .IsCohortArtistAsync(artist);
    }

    public async Task<UpstreamProvider[]?> ProvidersForArtistAsync(ArtistId artist)
    {
        using var scope = scopes.CreateScope();
        var found = await scope
            .ServiceProvider.GetRequiredService<ArtistService>()
            .FindArtistForImport(artist.ToString());
        return found
            ?.upstream_sources.Select(mapping => mapping.upstream_source.Provider)
            .Where(provider =>
                provider.HasValue && IngestionQueues.Providers.ContainsKey(provider.Value)
            )
            .Select(provider => provider!.Value)
            .ToArray();
    }
}

public sealed class ImportAlreadyRunning(string workflowId)
    : Exception("An import or rebuild is already running for this artist.")
{
    public string WorkflowId { get; } = workflowId;
}

public sealed class InvalidImportRequest(string message) : Exception(message);

public sealed class ArtistNotFound(ArtistId artist) : Exception($"Artist {artist} was not found.");

public sealed class UnsupportedImportOperation(string message) : Exception(message);
