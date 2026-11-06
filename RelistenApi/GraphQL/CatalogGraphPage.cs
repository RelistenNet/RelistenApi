using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Catalog;

namespace Relisten.GraphQL;

public sealed record CatalogGraphEdge(object Node, string Scope, CatalogPageKey Key)
{
    // Nodes-only pages need just the final cursor. Encode other edges only when selected.
    public string Cursor => CatalogCursor.Encode(Scope, Key);
}

public sealed record CatalogGraphPageInfo(string? EndCursor, bool HasNextPage);

public sealed record CatalogGraphPage(
    IReadOnlyList<object> Nodes,
    IReadOnlyList<CatalogGraphEdge> Edges,
    CatalogGraphPageInfo PageInfo,
    long? TotalCount
);

internal static class CatalogCursor
{
    private sealed record Envelope(int Version, string Scope, CatalogPageKey Key);

    public static string Encode(string scope, CatalogPageKey key) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, scope, key)));

    public static CatalogPageKey? Decode(CatalogCollection collection, string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }
        try
        {
            if (cursor.Length > 4096)
            {
                throw new FormatException();
            }
            var value = JsonSerializer.Deserialize<Envelope>(Convert.FromBase64String(cursor));
            if (
                value is null
                || value.Version != 1
                || value.Scope != Scope(collection)
                || value.Key is null
                || !double.IsFinite(value.Key.Score)
                || !double.IsFinite(value.Key.Secondary)
                || value.Key.DescendingLabel is null
                || value.Key.Label is null
                || value.Key.Id is null
            )
            {
                throw new FormatException();
            }
            return value.Key;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new GraphQLException(
                ErrorBuilder
                    .New()
                    .SetCode("invalid_cursor")
                    .SetMessage("The cursor does not belong to this collection and filters.")
                    .Build()
            );
        }
    }

    internal static string Scope(CatalogCollection collection) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(collection)))
        );
}

internal sealed class CatalogConnectionType<TNode> : ObjectType<CatalogGraphPage>
    where TNode : ObjectType, new()
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphPage> descriptor)
    {
        descriptor.Name(typeof(TNode).Name.Replace("Type", "") + "Connection");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(p => p.Nodes).Type<ListType<NonNullType<TNode>>>();
        descriptor.Field(p => p.Edges).Type<ListType<NonNullType<CatalogEdgeType<TNode>>>>();
        descriptor.Field(p => p.PageInfo);
        descriptor.Field(p => p.TotalCount).Cost(10);
    }
}

internal sealed class CatalogEdgeType<TNode> : ObjectType<CatalogGraphEdge>
    where TNode : ObjectType, new()
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphEdge> descriptor)
    {
        descriptor.Name(typeof(TNode).Name.Replace("Type", "") + "Edge");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(e => e.Node).Type<TNode>();
        descriptor.Field(e => e.Cursor);
    }
}
