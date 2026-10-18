using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>A bounded recovery listing, not a scheduler or a claim that source evidence is still current.</summary>
public sealed record VenueResolutionRecoveryPage(
    IReadOnlyList<VenueResolutionAttempt> Items,
    Guid? NextCursor
);
