using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Api.Models.Normalization;

/// <summary>Consistency of source assertions, not an inference that individual credits form an exhaustive list.</summary>
internal static class CompositionAuthorshipAgreement
{
    internal static bool IsAuthorRole(string role) => role is "writer" or "composer" or "lyricist";

    internal static IReadOnlySet<Guid>? CompleteAuthors(
        IEnumerable<(bool Complete, IReadOnlyList<Guid?> Authors)> sourceAssertions,
        IEnumerable<Guid?> individualAuthors
    )
    {
        var assertions = sourceAssertions.ToArray();
        var complete = assertions.Where(assertion => assertion.Complete).ToArray();
        if (
            complete.Length == 0
            || complete.Any(assertion =>
                assertion.Authors.Count == 0 || assertion.Authors.Any(person => !person.HasValue)
            )
        )
        {
            return null;
        }

        var authors = complete[0].Authors.Select(person => person!.Value).ToHashSet();
        if (
            complete.Any(assertion =>
                !authors.SetEquals(assertion.Authors.Select(person => person!.Value))
            )
        )
        {
            return null;
        }

        // Unknown author-role members cannot be discarded to make a convenient exhaustive
        // person list. Catalog artist credits remain valid claims, but cannot fill this set.
        var additional = assertions
            .SelectMany(assertion => assertion.Authors)
            .Concat(individualAuthors);
        return additional.All(person => person.HasValue && authors.Contains(person.Value))
            ? authors
            : null;
    }
}
