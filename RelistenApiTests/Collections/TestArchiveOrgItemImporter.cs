using System.Reflection;
using FluentAssertions;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestArchiveOrgItemImporter
{
    [Test]
    public void ArchiveMetadataIncludesCreator()
    {
        typeof(Metadata).GetProperty("creator").Should().NotBeNull();
    }

    [Test]
    public void ArchiveOrgImporterExposesItemSpecificImportContract()
    {
        var method = typeof(ArchiveOrgImporter).GetMethod("ImportSingleArchiveIdentifierForArtist");

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<ArchiveItemImportResult>));

        var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
        parameters.Should().Equal(
            typeof(Artist),
            typeof(string),
            typeof(ArchiveOrgImportContext),
            typeof(PerformContext));
    }

    [Test]
    public void ArchiveOrgImportContextDoesNotRequireArtistUpstreamSource()
    {
        var context = new ArchiveOrgImportContext
        {
            upstream_source_id = 1
        };

        context.upstream_source_id.Should().Be(1);
        context.infer_venue_from_description.Should().BeFalse();
    }

    [Test]
    public void SourcePruningRequiresArtistScope()
    {
        var methods = typeof(SourceService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "RemoveSourcesWithUpstreamIdentifiers")
            .ToList();

        methods.Should().ContainSingle();
        methods[0].GetParameters().Select(p => p.ParameterType).Should().Equal(
            typeof(Artist),
            typeof(IEnumerable<string>));
    }
}
