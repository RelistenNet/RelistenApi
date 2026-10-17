using System;
using System.Text.Json;
using Relisten.Data;

namespace Relisten.Api.Models;

/// <summary>
/// A supported engagement identity, not a date, a set number or a provider page containing
/// several engagements. Retain the exact identity across date and presentation corrections.
/// Reviewed keys are operator decisions; callers must retain their supporting evidence.
/// </summary>
public abstract record PerformanceEngagementIdentity
{
    private PerformanceEngagementIdentity() { }

    /// <summary>The durable key shared with recording assignment; its encoding is a compatibility contract.</summary>
    public abstract string PerformanceKey { get; }

    /// <summary>Provider-qualified identity of one engagement, after its scope has been established.</summary>
    public sealed record Provider(string ProviderName, string ResourceType, string ProviderKey)
        : PerformanceEngagementIdentity
    {
        public override string PerformanceKey =>
            ProviderName == "phish.in" && ResourceType == "show"
                ? "phish.in:" + ProviderKey
                : "engagement:"
                    + JsonSerializer.Serialize(new[] { ProviderName, ResourceType, ProviderKey });
    }

    /// <summary>A stable review-prefixed key chosen by a trusted operator for one engagement.</summary>
    public sealed record Reviewed(string Key) : PerformanceEngagementIdentity
    {
        public override string PerformanceKey => Key;
    }
}

/// <summary>
/// Creation is an explicit supported-distinctness decision. A new provider account on the
/// same date alone does not justify it. Existing selects one known same-artist show by UUID;
/// neither choice moves recordings or infers correspondence from date equality.
/// </summary>
public abstract record PerformanceEngagementTarget
{
    private PerformanceEngagementTarget() { }

    public sealed record CreateNew : PerformanceEngagementTarget;

    public sealed record Existing(Guid ShowUuid) : PerformanceEngagementTarget;
}

/// <summary>
/// An explicit accepted presentation update. Omit the record to preserve existing type and
/// label; supply Unknown/null to clear both. A new performance without presentation defaults
/// to Unknown/null. Presentation does not establish engagement identity or correspondence.
/// </summary>
public sealed record PerformanceEngagementPresentation(PerformanceType Type, string? Label = null);
