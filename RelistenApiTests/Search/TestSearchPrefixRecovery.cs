using FluentAssertions;
using Relisten.Search;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchPrefixRecovery
{
    [TestCase("pe")]
    [TestCase("peg")]
    [TestCase("pegg")]
    public void ShortPrefixRecoveryPreservesTheOriginalQueryAndFilters(string suffix)
    {
        var primary = Primary(suffix);

        var retry = SearchRequests.WidenEmptyPerformancePrefix(
            new(primary.Q, Typing: true),
            [primary]
        );

        retry.Should().Be(primary with { MaxCandidates = 256 });
    }

    [TestCase("p", "All", 1, null)]
    [TestCase("peggy", "All", 1, null)]
    [TestCase("pe", "Shows", 1, null)]
    [TestCase("pe", "All", 2, null)]
    [TestCase("pe", "All", 1, false)]
    public void PrefixRecoverySkipsOtherQueries(string suffix, string scope, int page, bool? prefix)
    {
        var primary = Primary(suffix) with { Prefix = prefix };
        var input = new SearchInput(
            primary.Q,
            Enum.Parse<SearchScope>(scope),
            Typing: true,
            Page: page
        );

        SearchRequests.WidenEmptyPerformancePrefix(input, [primary]).Should().BeNull();
    }

    private static SearchRequest Primary(string suffix) =>
        SearchRequests
            .Build(
                new SearchQuery($"englishtown {suffix}", Year: 1977),
                SearchScope.All,
                typing: false,
                venueIds: ["venue_raceway"]
            )
            .Single(request => request.Collection == SearchIndexSchema.Performances);
}
