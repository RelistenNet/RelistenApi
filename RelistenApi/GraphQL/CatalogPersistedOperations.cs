using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.PersistedOperations;
using Microsoft.AspNetCore.Http;

namespace Relisten.GraphQL;

/// <summary>
/// Deployment-owned query documents and their edge cache policy. There is no runtime registration.
/// Add a query under GraphQL/Operations/{Catalog,Rankings,Live}; its versioned filename is its public ID.
/// Keep published documents immutable and retain old versions while clients use them.
/// </summary>
public sealed class CatalogPersistedOperations : IOperationDocumentStorage
{
    private const string ResourcePrefix = "Relisten.GraphQL.Operations.";
    private readonly IReadOnlyDictionary<string, Entry> entries;

    private CatalogPersistedOperations(IEnumerable<Entry> operations)
    {
        entries = operations.ToDictionary(operation => operation.Id, StringComparer.Ordinal);
    }

    public static CatalogPersistedOperations Load(Assembly assembly)
    {
        var operations = new List<Entry>();
        foreach (
            var resource in assembly
                .GetManifestResourceNames()
                .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        )
        {
            var relativeName = resource[ResourcePrefix.Length..];
            var category = relativeName[..relativeName.IndexOf('.')];
            var id = System.IO.Path.GetFileNameWithoutExtension(
                relativeName[(category.Length + 1)..]
            );
            var seconds = category switch
            {
                "Catalog" => 300,
                "Rankings" => 30,
                "Live" => 0,
                _ => throw new InvalidOperationException(
                    $"Unknown persisted operation category: {resource}"
                ),
            };
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            var source = reader.ReadToEnd();
            var document = Utf8GraphQLParser.Parse(source);
            var definitions = document.Definitions.OfType<OperationDefinitionNode>().ToArray();
            if (
                definitions.Length != 1
                || definitions[0].Operation != OperationType.Query
                || definitions[0].Name is null
            )
            {
                throw new InvalidOperationException(
                    $"Persisted document must contain one named query: {resource}"
                );
            }
            operations.Add(new Entry(id, new OperationDocumentSourceText(source), seconds));
        }
        return new CatalogPersistedOperations(operations);
    }

    internal bool Contains(string id) => entries.ContainsKey(id);

    public ValueTask<IOperationDocument?> TryReadAsync(
        OperationDocumentId documentId,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(entries.GetValueOrDefault(documentId.Value)?.Document);

    public ValueTask SaveAsync(
        OperationDocumentId documentId,
        IOperationDocument document,
        CancellationToken cancellationToken = default
    ) =>
        throw new NotSupportedException(
            "Catalog operations are registered at deployment, not by requests."
        );

    internal void ApplyCachePolicy(HttpContext http, IExecutionResult? result)
    {
        var request = http.Request;
        if (
            !HttpMethods.IsGet(request.Method)
            || !request.Path.StartsWithSegments(
                CatalogGraphRegistration.PersistedPath,
                StringComparison.OrdinalIgnoreCase
            )
            || request.Headers.Accept != "application/json"
            || request.Headers.ContainsKey("GraphQL-Cost")
            || request.Headers.ContainsKey("Authorization")
            || request.Headers.ContainsKey("Cookie")
            || request.Query.Keys.Any(key => key != "variables")
            || result is not OperationResult { Errors: null or { Count: 0 }, Data: not null }
            || request.RouteValues["operationId"] is not string id
            || !entries.TryGetValue(id, out var operation)
            || operation.CacheSeconds == 0
        )
        {
            return;
        }

        // Run before serialization starts, after the formatter has chosen the HTTP status.
        // Errors (including partial GraphQL results) retain the middleware's no-store default.
        http.Response.OnStarting(() =>
        {
            if (
                http.Response.StatusCode == StatusCodes.Status200OK
                && !http.Response.Headers.ContainsKey("Set-Cookie")
            )
            {
                http.Response.Headers.CacheControl =
                    $"public, max-age=0, s-maxage={operation.CacheSeconds}";
            }
            return Task.CompletedTask;
        });
    }

    private sealed record Entry(string Id, IOperationDocument Document, int CacheSeconds);
}
