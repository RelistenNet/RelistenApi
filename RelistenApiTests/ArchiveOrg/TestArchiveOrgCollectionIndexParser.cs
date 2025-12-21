using FluentAssertions;
using NUnit.Framework;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgCollectionIndexParser
{
    [Test]
    public void Parse_ShouldReadCollectionItems()
    {
        var json = TestUtils.ReadFixture("ArchiveOrg/collection-index.json");
        var parsed = ArchiveOrgCollectionIndexParser.Parse(json);

        parsed.items.Should().NotBeNull();
        parsed.items.Count.Should().Be(3);
        parsed.items[0].identifier.Should().Be("Guster");
        parsed.items[0].item_count.Should().Be(12);
    }
}
