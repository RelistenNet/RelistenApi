using System;

namespace Relisten.GraphQL;

/// <summary>Public operation budgets, deliberately large enough for catalog web pages.
/// Tune from measured workloads rather than lowering page sizes to hide expensive reads.</summary>
public sealed class CatalogGraphOptions
{
    public int MaxPageSize { get; set; } = 500;
    public int MaxIds { get; set; } = 500;
    public int MaxConcurrentReads { get; set; } = 4;
    public int ExecutionSeconds { get; set; } = 20;
    public int MaxDocumentBytes { get; set; } = 262144;
    public int MaxDepth { get; set; } = 24;
    public int MaxMaterializedRows { get; set; } = 100000;
    public double MaxFieldCost { get; set; } = 200000;
    public double MaxTypeCost { get; set; } = 500000;

    internal void Validate()
    {
        if (
            MaxPageSize < 1
            || MaxIds < 1
            || MaxConcurrentReads < 1
            || ExecutionSeconds < 1
            || MaxDocumentBytes < 1
            || MaxDepth < 1
            || MaxMaterializedRows < 1
            || !double.IsFinite(MaxFieldCost)
            || MaxFieldCost <= 0
            || !double.IsFinite(MaxTypeCost)
            || MaxTypeCost <= 0
        )
        {
            throw new InvalidOperationException("GraphQL limits must be positive finite values.");
        }
    }
}
