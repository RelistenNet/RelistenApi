using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Root-map credits belong to one directory account, even when sibling accounts share a date.</summary>
[TestFixture]
public sealed class TestNormalizationLocalRecordingSelection
{
    private readonly NormalizationProviderDecoder decoder = new();
    private readonly NormalizationRecordingExtractor extractor = new(new(), new());

    [Test]
    public void ExtractsOnlySelectedRootAccountAndDirectoryProseWithOriginalCitations()
    {
        var context = Context();
        var root = NormalizationLocalRecordingSelection.DecodeRoot(context.Root, decoder);
        var result = extractor.ExtractLocal(
            context.Directory,
            context.Decoded,
            context.Binding,
            context.Root,
            root
        );
        result
            .Credits.Select(proposal => proposal.Mention.Name)
            .Should()
            .Equal("Directory Pat", "Zoë Recorder");
        result.Credits.Should().OnlyContain(proposal => proposal.FileOrigin == null);
        var rootCredit = result.Credits.Single(proposal =>
            proposal.Mention.Evidence.InputRevisionUuid == context.Root.Uuid
        );
        rootCredit.Mention.Evidence.JsonPointer.Should().Be("/data/1970-xx-xx/0/taper_notes");
        using var document = NormalizationRetainedDocument.Open(context.Root);
        document.ValidateEvidence(rootCredit.Mention.Evidence);
        result
            .DecodeIssues.Should()
            .ContainSingle(issue => issue.Code == "conflicting_directory_date");
        result
            .Titles.Should()
            .ContainSingle()
            .Which.TrackUuid.Should()
            .Be(context.Binding.Files[0].Binding.Value!.TrackUuid);
        var replay = extractor.ExtractLocal(
            context.Directory,
            context.Decoded,
            context.Binding,
            context.Root,
            root
        );
        replay.Should().BeEquivalentTo(result);
    }

    [Test]
    public void ArrayValuedRootNotesRetainSeparateCitationsAndMalformedEntryIssues()
    {
        var context = Context();
        var root = Input(
            context.Root.Resource,
            """
            {"data":{"1970-xx-xx":[{"dir":"chosen","taper_notes":["Taped by First Pat",42,"Transferred by Second Pat"]}]}}
            """
        );
        var binding = context.Binding with { SupportingInputs = [new(root.Uuid, root.Resource)] };
        var result = extractor.ExtractLocal(
            context.Directory,
            context.Decoded,
            binding,
            root,
            NormalizationLocalRecordingSelection.DecodeRoot(root, decoder)
        );
        var rootCredits = result
            .Credits.Where(proposal => proposal.Mention.Evidence.InputRevisionUuid == root.Uuid)
            .ToArray();
        rootCredits
            .Select(proposal => proposal.Mention.Name)
            .Should()
            .Equal("First Pat", "Second Pat");
        rootCredits
            .Select(proposal => proposal.Mention.Evidence.JsonPointer)
            .Should()
            .Equal("/data/1970-xx-xx/0/taper_notes/0", "/data/1970-xx-xx/0/taper_notes/2");
        result
            .DecodeIssues.Should()
            .ContainSingle(issue =>
                issue.Evidence.JsonPointer == "/data/1970-xx-xx/0/taper_notes/1"
            );
        using var document = NormalizationRetainedDocument.Open(root);
        foreach (var credit in rootCredits)
        {
            document.ValidateEvidence(credit.Mention.Evidence);
        }
    }

    [Test]
    public void UnboundFilesRemainPresentWithoutReceivingRootCreditsOrTitleAssignments()
    {
        var context = Context();
        var files = context
            .Binding.Files.Select(file =>
                file with
                {
                    Binding = new(NormalizationCatalogBindingStatus.Missing, null, "missing_file"),
                }
            )
            .ToArray();
        var binding = context.Binding with { Files = files };
        var result = extractor.ExtractLocal(
            context.Directory,
            context.Decoded,
            binding,
            context.Root,
            NormalizationLocalRecordingSelection.DecodeRoot(context.Root, decoder)
        );
        result
            .Credits.Should()
            .HaveCount(2)
            .And.OnlyContain(proposal => proposal.FileOrigin == null);
        result.Titles.Should().BeEmpty();
        context.Decoded.Recordings.Single().Files.Should().HaveCount(1);
    }

    [Test]
    public void RootAccountBoundIncludesMalformedEntriesAcrossDatesAndHonorsCharset()
    {
        var input = Input(
            new("local", "metadata_root", "root"),
            """
            {"data":{"1970-xx-xx":[null,{"dir":"one","taper_notes":"Taped by Zoë"}],"1971-xx-xx":[{"dir":"two"}]}}
            """,
            Encoding.Unicode,
            "application/json; charset=utf-16"
        );
        var result = NormalizationLocalRecordingSelection.DecodeRoot(input, decoder, 3);
        result.Recordings.Should().HaveCount(2);
        result.Recordings[0].Text.Single().Text.Value.Should().Be("Taped by Zoë");
        FluentActions
            .Invoking(() => NormalizationLocalRecordingSelection.DecodeRoot(input, decoder, 2))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingOrRepeatedRootAccountsCannotSupplyCredits(bool duplicate)
    {
        var context = Context();
        var root = Input(
            context.Root.Resource,
            JsonSerializer.Serialize(
                new
                {
                    data = new Dictionary<string, object[]>
                    {
                        ["1970-xx-xx"] = duplicate
                            ?
                            [
                                new { dir = "chosen", taper_notes = "Taped by Pat" },
                                new { dir = "chosen", taper_notes = "Taped by Other" },
                            ]
                            : [new { dir = "other", taper_notes = "Taped by Other" }],
                    },
                }
            )
        );
        var binding = context.Binding with { SupportingInputs = [new(root.Uuid, root.Resource)] };
        FluentActions
            .Invoking(() =>
                extractor.ExtractLocal(
                    context.Directory,
                    context.Decoded,
                    binding,
                    root,
                    NormalizationLocalRecordingSelection.DecodeRoot(root, decoder)
                )
            )
            .Should()
            .Throw<ArgumentException>();
    }

    [Test]
    public void WrongRootRevisionAndForeignAccountProseAreRejected()
    {
        var context = Context();
        var root = NormalizationLocalRecordingSelection.DecodeRoot(context.Root, decoder);
        var wrongBinding = context.Binding with
        {
            SupportingInputs = [new(Guid.NewGuid(), context.Root.Resource)],
        };
        FluentActions
            .Invoking(() =>
                extractor.ExtractLocal(
                    context.Directory,
                    context.Decoded,
                    wrongBinding,
                    context.Root,
                    root
                )
            )
            .Should()
            .Throw<ArgumentException>();
        var selected = root.Recordings.Single(account =>
            account.Origin.Item == context.Directory.Resource
        );
        var sibling = root.Recordings.Single(account =>
            account.Origin.Item != context.Directory.Resource
        );
        var forged = root with { Recordings = [selected with { Text = sibling.Text }, sibling] };
        FluentActions
            .Invoking(() =>
                extractor.ExtractLocal(
                    context.Directory,
                    context.Decoded,
                    context.Binding,
                    context.Root,
                    forged
                )
            )
            .Should()
            .Throw<ArgumentException>();
    }

    [Test]
    public void CancellationDoesNotProducePartialExtraction()
    {
        var context = Context();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        FluentActions
            .Invoking(() =>
                NormalizationLocalRecordingSelection.DecodeRoot(
                    context.Root,
                    decoder,
                    cancellationToken: cancelled.Token
                )
            )
            .Should()
            .Throw<OperationCanceledException>();
        var decoded = NormalizationLocalRecordingSelection.DecodeRoot(context.Root, decoder);
        FluentActions
            .Invoking(() =>
                extractor.ExtractLocal(
                    context.Directory,
                    context.Decoded,
                    context.Binding,
                    context.Root,
                    decoded,
                    cancelled.Token
                )
            )
            .Should()
            .Throw<OperationCanceledException>();
    }

    private ContextData Context()
    {
        var root = Input(
            new("local", "metadata_root", "catalog-a"),
            """
            {"data":{"1970-xx-xx":[
              {"dir":"chosen","date":"1971-xx-xx","taper_notes":"Taped by Zoë Recorder"},
              {"dir":"other","date":"1972-xx-xx","taper_notes":"Transferred by Wrong Person"}
            ]}}
            """
        );
        var directory = Input(
            NormalizationProviderResources.LocalDirectory("catalog-a", "chosen"),
            """
            {"tracks":[{"mp3":"song.mp3","title":"Song"}],"txts":["Transferred by Directory Pat"]}
            """
        );
        var decoded = decoder.Decode(directory);
        var owner = new NormalizationCatalogRecordingReference(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            []
        );
        var file = decoded.Recordings.Single().Files.Single();
        var binding = new NormalizationRecordingBindingSnapshot(
            directory.Uuid,
            directory.Resource,
            new(NormalizationCatalogBindingStatus.Bound, owner, "exact_media"),
            [
                new(
                    file,
                    new(
                        NormalizationCatalogBindingStatus.Bound,
                        new(
                            owner.ArtistUuid,
                            owner.SourceUuid,
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            "https://example.test/song.mp3"
                        ),
                        "exact_media"
                    )
                ),
            ],
            [],
            [],
            [new(root.Uuid, root.Resource)]
        );
        return new(root, directory, decoded, binding);
    }

    private static NormalizationInputRevision Input(
        NormalizationResourceKey resource,
        string json,
        Encoding? encoding = null,
        string mediaType = "application/json"
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = (encoding ?? Encoding.UTF8).GetBytes(json),
            MediaType = mediaType,
            CaptureVersion = "local-http-json-v1",
        };

    private sealed record ContextData(
        NormalizationInputRevision Root,
        NormalizationInputRevision Directory,
        ProviderDecodeResult Decoded,
        NormalizationRecordingBindingSnapshot Binding
    );
}
