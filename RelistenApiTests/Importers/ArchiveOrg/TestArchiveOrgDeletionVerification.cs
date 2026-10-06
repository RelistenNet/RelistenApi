using System.Net;
using FluentAssertions;
using Relisten.Import;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgDeletionVerification
{
    [Test]
    public async Task BulkGuardRunsBeforeAnyMetadataRequestIncludingNoMp3Candidates()
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var candidates = Enumerable.Range(0, 6).Select(i => $"recording-{i}").ToArray();

        var result = await ArchiveOrgImporter.VerifyDeletionCandidatesAsync(
            candidates, new HashSet<string> { candidates[0] }, http, _ => { });

        result.Guarded.Should().BeTrue();
        result.Identifiers.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task OnlyExplicitDeletionAndSeparateConfirmedNoMp3PolicyReachTheDeletionList()
    {
        using var handler = new MetadataHandler(
            "{\"metadata\":{\"identifier\":\"present\"}}", "{\"errcode\":104}", "{}");
        using var http = new HttpClient(handler);
        var log = new List<string>();

        var result = await ArchiveOrgImporter.VerifyDeletionCandidatesAsync(
            ["present", "deleted", "unknown", "no-mp3"], new HashSet<string> { "no-mp3" }, http, log.Add);

        result.Guarded.Should().BeFalse();
        result.Identifiers.Should().Equal("deleted", "no-mp3");
        handler.Requests.Should().HaveCount(3);
        log.Should().HaveCount(4);
        log.Should().Contain(message => message.Contains("unknown: Unknown"));
    }

    [Test]
    public void MissingFilesCannotBecomeConfirmedNoMp3()
    {
        var act = () => ArchiveOrgImporter.HasNoVbrMp3Files(new RootObject());

        act.Should().Throw<InvalidDataException>().WithMessage("*files array*");
        ArchiveOrgImporter.HasNoVbrMp3Files(new RootObject { files = [] }).Should().BeTrue();
        ArchiveOrgImporter.HasNoVbrMp3Files(new RootObject
        {
            files = [new Relisten.Vendor.ArchiveOrg.Metadata.File { format = "VBR MP3" }]
        }).Should().BeFalse();
    }

    private sealed class MetadataHandler(params string[] responses) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Requests.Count - 1])
            });
        }
    }
}
