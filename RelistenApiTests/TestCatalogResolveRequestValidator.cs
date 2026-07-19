using FluentAssertions;
using Relisten.Api.Models.Api;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestCatalogResolveRequestValidator
{
    [Test]
    public void DeduplicatesReferencesWhilePreservingFirstOccurrenceOrder()
    {
        var artistUuid = Guid.NewGuid();
        var showUuid = Guid.NewGuid();
        var request = Request(
            ("artist", artistUuid.ToString()),
            ("show", showUuid.ToString()),
            ("artist", artistUuid.ToString()));

        var valid = CatalogResolveRequestValidator.TryValidate(request, out var references, out var error);

        valid.Should().BeTrue();
        error.Should().BeNull();
        references.Should().Equal(
            new CatalogReference("artist", artistUuid),
            new CatalogReference("show", showUuid));
    }

    [TestCase("Artist", "00000000-0000-0000-0000-000000000001")]
    [TestCase("artist", "00000000000000000000000000000001")]
    [TestCase("artist", "123")]
    [TestCase("artist", "00000000-0000-0000-0000-000000000000")]
    public void RejectsTypesAndIdentifiersOutsideTheUuidOnlyContract(string catalogType, string catalogUuid)
    {
        var valid = CatalogResolveRequestValidator.TryValidate(
            Request((catalogType, catalogUuid)),
            out var references,
            out var error);

        valid.Should().BeFalse();
        references.Should().BeEmpty();
        error!.Code.Should().Be("invalid_catalog_reference");
    }

    [Test]
    public void EnforcesLimitAfterDeduplication()
    {
        var repeatedUuid = Guid.NewGuid().ToString();
        var accepted = Enumerable
            .Repeat(("artist", repeatedUuid), CatalogResolveRequestValidator.MaxReferenceCount + 1)
            .ToArray();
        var overflow = Enumerable.Range(1, CatalogResolveRequestValidator.MaxReferenceCount + 1)
            .Select(index => ("artist", $"00000000-0000-0000-0000-{index:D12}"))
            .ToArray();

        CatalogResolveRequestValidator.TryValidate(Request(accepted), out var deduplicated, out var acceptedError)
            .Should().BeTrue();
        deduplicated.Should().ContainSingle();
        acceptedError.Should().BeNull();

        CatalogResolveRequestValidator.TryValidate(Request(overflow), out _, out var overflowError)
            .Should().BeFalse();
        overflowError!.Code.Should().Be("too_many_references");
    }

    [Test]
    public void RejectsAnEmptyReferenceList()
    {
        var valid = CatalogResolveRequestValidator.TryValidate(
            Request(),
            out var references,
            out var error);

        valid.Should().BeFalse();
        references.Should().BeEmpty();
        error!.Code.Should().Be("references_required");
    }

    private static CatalogResolveRequest Request(params (string catalogType, string catalogUuid)[] references)
    {
        return new CatalogResolveRequest
        {
            contract_version = CatalogResolveRequestValidator.ContractVersion,
            references = references.Select(reference => new CatalogReferenceRequest
            {
                catalog_type = reference.catalogType,
                catalog_uuid = reference.catalogUuid
            }).ToArray()
        };
    }
}
