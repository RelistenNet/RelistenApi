using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// An accepted set, including empty or unnamed sets. Revision and basis identify the complete
/// structure decision. Evidence contains only this set's original citations. A null position
/// remains unknown; UUID order among unknown positions does not imply performance order.
/// </summary>
public sealed class NormalizedSetlistSet : NormalizedMusicRelationship
{
    public Guid edition_uuid { get; set; }
    public int? position { get; set; }
    public string? label { get; set; }
    public int? encore_number { get; set; }
}
