using System;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// An immutable provider input required to reproduce a catalog binding. Generation optionally
/// fences the observed resource head, including equal bytes observed again after a correction.
/// Omit it for historical revision-only expectations; absence preserves their durable identity.
/// </summary>
public sealed record NormalizationBindingInputReference(
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Generation = null
);
