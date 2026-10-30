using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Catalog;

/// <summary>Resolves saved identities and hydrates entity patches within one catalog snapshot.</summary>
public sealed class CatalogResolver(
    DbService db,
    CatalogReferenceLookup lookup,
    CatalogResolverHydrator hydrator
)
{
    public async Task<CatalogResolveResult> Resolve(
        IReadOnlyList<CatalogReference> references,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var resolved = await lookup.Read(session, references, cancellationToken);
        var targets = resolved
            .Select(item => item.Target)
            .OfType<CatalogResolvedTarget>()
            .DistinctBy(target => (target.Kind, target.Id))
            .ToArray();
        var entities = await hydrator.Read(session, targets, cancellationToken);
        return new(resolved, entities);
    }
}

public sealed record CatalogResolveResult(
    IReadOnlyList<CatalogReferenceResolution> References,
    CatalogResolvedEntities Entities
);
