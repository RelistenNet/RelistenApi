using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

internal static class PeopleResolutionCapacity
{
    // Membership counts stored bindings per contributor, even when their observed owner
    // keys overlap. Two contributors consuming 6,789 owners exceed Work's 10,000 default.
    // Select its existing 100,000-binding envelope explicitly for people; retain the default
    // contributor bound. Larger snapshots still fail completely, without truncating owners
    // or replacing their original observations. This is not a checkpoint-byte budget.
    internal static readonly NormalizationInputContributionsLimits ContributionMembership = new(
        MaximumContributors: 100,
        MaximumBindings: 100000
    );
}
