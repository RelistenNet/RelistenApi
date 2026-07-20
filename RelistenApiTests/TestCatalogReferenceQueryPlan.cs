using FluentAssertions;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestCatalogReferenceQueryPlan
{
    [TestCase("artist", (int)CatalogReferenceResultSets.Artists)]
    [TestCase(
        "show",
        (int)(CatalogReferenceResultSets.Artists |
              CatalogReferenceResultSets.Shows |
              CatalogReferenceResultSets.Tours |
              CatalogReferenceResultSets.Venues |
              CatalogReferenceResultSets.Years))]
    [TestCase(
        "source_track",
        (int)(CatalogReferenceResultSets.Artists |
              CatalogReferenceResultSets.Shows |
              CatalogReferenceResultSets.Sources |
              CatalogReferenceResultSets.SourceTracks |
              CatalogReferenceResultSets.Tours |
              CatalogReferenceResultSets.Venues |
              CatalogReferenceResultSets.Years |
              CatalogReferenceResultSets.SourceSets))]
    public void IncludesOnlyTheResultSetsNeededToHydrateAReference(
        string catalogType,
        int expected)
    {
        var plan = CatalogReferenceQueryPlan.Create([Reference(catalogType)]);

        plan.ResultSets.Should().Be((CatalogReferenceResultSets)expected);
    }

    [Test]
    public void CombinesDependenciesForMixedReferenceTypes()
    {
        var plan = CatalogReferenceQueryPlan.Create([
            Reference("song"),
            Reference("venue")
        ]);

        plan.ResultSets.Should().Be(
            CatalogReferenceResultSets.Artists |
            CatalogReferenceResultSets.Songs |
            CatalogReferenceResultSets.Venues);
    }

    [Test]
    public void BuildsOnlyTargetedHydrationQueries()
    {
        var plan = CatalogReferenceQueryPlan.Create([Reference("artist")]);

        plan.Sql.Should().Contain("FROM artists artist");
        plan.Sql.Should().NotContain(" AS availability");
        plan.Sql.Should().NotContain("LEFT JOIN available");
    }

    private static CatalogReference Reference(string catalogType) =>
        new(catalogType, Guid.NewGuid());
}
