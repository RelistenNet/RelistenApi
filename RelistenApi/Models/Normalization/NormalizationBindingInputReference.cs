using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>An immutable provider input required to reproduce a catalog binding.</summary>
public sealed record NormalizationBindingInputReference(
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource
);
