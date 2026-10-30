using System;

namespace Relisten.Catalog;

/// <summary>A known canonical resource has an invalid normalization relationship.</summary>
public sealed class CatalogNormalizationUnavailableException(string message) : Exception(message);
