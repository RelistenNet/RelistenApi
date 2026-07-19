using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Relisten.Catalog;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public readonly record struct CatalogReference(string CatalogType, Guid CatalogUuid);

public sealed class CatalogAvailabilityValidator(AccountsDbContext dbContext)
{
    public async Task<IReadOnlyList<CatalogReference>> FindUnavailableAsync(
        IReadOnlyCollection<CatalogReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return [];
        }

        var unique = references.Distinct().ToArray();
        var requestedTypes = unique.Select(item => item.CatalogType).ToHashSet(StringComparer.Ordinal);
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = CatalogHydrationAvailabilitySql.BuildAvailableReferences(requestedTypes);

        var catalogTypesParameter = command.CreateParameter();
        catalogTypesParameter.ParameterName = "catalog_types";
        catalogTypesParameter.Value = unique.Select(item => item.CatalogType).ToArray();
        command.Parameters.Add(catalogTypesParameter);

        var catalogUuidsParameter = command.CreateParameter();
        catalogUuidsParameter.ParameterName = "catalog_uuids";
        catalogUuidsParameter.Value = unique.Select(item => item.CatalogUuid).ToArray();
        command.Parameters.Add(catalogUuidsParameter);

        var available = new HashSet<CatalogReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            available.Add(new(reader.GetString(0), reader.GetGuid(1)));
        }

        return unique.Where(reference => !available.Contains(reference)).ToArray();
    }

}
