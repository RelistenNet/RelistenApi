using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Bounds immutable input reads before PostgreSQL returns payload bytes. Exceeding any limit
/// fails the complete read; callers must not treat a partial evidence set as complete.
/// </summary>
public sealed record NormalizationInputReadLimits(
    int MaximumInputs = 100,
    long MaximumInputBytes = 32 * 1024 * 1024,
    long MaximumTotalBytes = 128 * 1024 * 1024
)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumInputs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumInputs, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumInputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumTotalBytes, 1);
    }
}
