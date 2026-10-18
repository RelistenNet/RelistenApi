using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Exercises the production extraction envelope; synthetic bindings grant no match authority.</summary>
[TestFixture]
public sealed class TestNormalizationRecordingSourceActorAliases
{
    private readonly NormalizationRecordingExtractor extractor = new(new(), new());
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void HistoricalEnvelopeWithoutSourceActorsKeepsItsExactSerializedPayload()
    {
        const string legacy =
            "{\"Credits\":[],\"UnresolvedCredits\":[],\"Titles\":[],\"DecodeIssues\":[],\"CandidateSearchStatus\":\"not_run\"}";
        var restored = JsonSerializer.Deserialize<NormalizationRecordingExtraction>(legacy)!;

        restored.SourceActorAliases.Should().BeNull();
        JsonSerializer
            .SerializeToUtf8Bytes(restored)
            .Should()
            .Equal(System.Text.Encoding.UTF8.GetBytes(legacy));
        var originalShape = new NormalizationRecordingExtraction([], [], [], []);
        JsonSerializer
            .SerializeToUtf8Bytes(originalShape)
            .Should()
            .Equal(System.Text.Encoding.UTF8.GetBytes(legacy));
    }

    [Test]
    public void ProductionEnvelopeExposesOriginalAliasAssertionsWithoutRecordingCredits()
    {
        const string prose = "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals";
        var context = Archive(prose);
        var result = Extract(context);

        result.Credits.Should().BeEmpty();
        result.UnresolvedCredits.Should().BeEmpty();
        var scope = result.SourceActorAliases.Should().ContainSingle().Subject;
        scope.InputRevisionUuid.Should().Be(context.Input.Uuid);
        scope.Resource.Should().Be(context.Input.Resource);
        scope.RecordingOrigin.Should().Be(context.Decoded.Recordings.Single().Origin);
        scope.FileOrigin.Should().BeNull();
        var assertion = scope.Extraction.Assertions.Should().ContainSingle().Subject;
        assertion.Name.Should().Be("Andy Moresi");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Hot Dog");
        assertion.NameEvidence.JsonPointer.Should().Be("/metadata/description");
        scope.Extraction.Scopes.Should().ContainSingle().Which.Complete.Should().BeTrue();
        result.CandidateSearchStatus.Should().Be("not_run");
        Extract(context).Should().BeEquivalentTo(result);
    }

    [Test]
    public void IncompleteAliasCoverageIsPreservedAlongsideTheSupportedAssertion()
    {
        const string prose =
            "Personnel:\nAlice Vale aka Lark - vocals\n"
            + "Bonus material:\nThe Chimney Tops (aka Pilotdrift)";
        var context = Archive(prose);
        var result = Extract(context);

        var scope = result.SourceActorAliases.Should().ContainSingle().Subject;
        scope.Extraction.Assertions.Should().ContainSingle().Which.Name.Should().Be("Alice Vale");
        scope
            .Extraction.Unresolved.Should()
            .ContainSingle()
            .Which.Evidence.Single()
            .Quote.Should()
            .Be("The Chimney Tops (aka Pilotdrift)");
        scope.Extraction.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
        foreach (var evidence in scope.Extraction.Unresolved.SelectMany(issue => issue.Evidence))
        {
            evidence.InputRevisionUuid.Should().Be(context.Input.Uuid);
            using var document = NormalizationRetainedDocument.Open(context.Input);
            document.ValidateEvidence(evidence);
        }
    }

    [Test]
    public void FileAssertionsRetainOriginalOriginsEvenWithoutPlayableCatalogBindings()
    {
        var context = Archive(
            "Personnel:\nRecording Robin aka Finch - vocals",
            "Personnel:\nFile Avery aka Lark - guitar",
            "Personnel:\nFile Morgan aka Jay - bass"
        );
        var result = Extract(context);
        var recording = context.Decoded.Recordings.Single();

        result.Titles.Should().BeEmpty();
        result.Credits.Should().BeEmpty();
        result.SourceActorAliases.Should().HaveCount(3);
        var wide = result.SourceActorAliases!.Single(scope => scope.FileOrigin == null);
        wide.Extraction.Assertions.Should()
            .ContainSingle()
            .Which.Name.Should()
            .Be("Recording Robin");
        foreach (var file in recording.Files)
        {
            var scope = result.SourceActorAliases.Single(scope => scope.FileOrigin == file.Origin);
            scope.RecordingOrigin.Should().Be(recording.Origin);
            var assertion = scope.Extraction.Assertions.Should().ContainSingle().Subject;
            using var document = NormalizationRetainedDocument.Open(context.Input);
            NormalizationRecordingExtractor.ValidateFileCreditEvidence(
                context.Input,
                document,
                file.Origin,
                assertion.NameEvidence
            );
            foreach (var coverage in scope.Extraction.Scopes)
            {
                foreach (var evidence in coverage.Evidence)
                {
                    NormalizationRecordingExtractor.ValidateFileCreditEvidence(
                        context.Input,
                        document,
                        file.Origin,
                        evidence
                    );
                }
            }
        }
    }

    [Test]
    public void AliasOnlyFileProseCannotBorrowARecordingOrSiblingFilesEvidence()
    {
        var original = Archive(
            "Personnel:\nRecording Robin aka Finch - vocals",
            "Personnel:\nFile Avery aka Lark - guitar",
            "Personnel:\nFile Morgan aka Jay - bass"
        );
        var recording = original.Decoded.Recordings.Single();
        foreach (var foreignText in new[] { recording.Text, recording.Files[1].Text })
        {
            var changed = original with
            {
                Decoded = original.Decoded with
                {
                    Recordings =
                    [
                        recording with
                        {
                            Files =
                            [
                                recording.Files[0] with
                                {
                                    Text = foreignText,
                                },
                                recording.Files[1],
                            ],
                        },
                    ],
                },
            };
            Action extract = () => Extract(changed);
            extract.Should().Throw<ArgumentException>();
        }
    }

    [Test]
    public void LocalDirectoryIncludesOnlyItsSelectedRootAccountWithActualRootCoordinates()
    {
        const string directoryProse = "Personnel:\nDirectory Robin aka Finch - vocals";
        const string selectedProse = "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals";
        const string siblingProse = "Personnel:\nWrong Person aka Wrong Alias - guitar";
        var root = Input(
            new("local", "metadata_root", "alias-catalog"),
            new
            {
                data = new Dictionary<string, object[]>
                {
                    ["1970-xx-xx"] =
                    [
                        new { dir = "chosen", taper_notes = selectedProse },
                        new { dir = "other", taper_notes = siblingProse },
                    ],
                },
            }
        );
        var directory = Input(
            NormalizationProviderResources.LocalDirectory("alias-catalog", "chosen"),
            new { tracks = Array.Empty<object>(), txts = new[] { directoryProse } }
        );
        var decoded = decoder.Decode(directory);
        var binding = Bound(directory, decoded) with
        {
            SupportingInputs = [new(root.Uuid, root.Resource)],
        };
        var result = extractor.ExtractLocal(
            directory,
            decoded,
            binding,
            root,
            NormalizationLocalRecordingSelection.DecodeRoot(root, decoder)
        );

        result.Credits.Should().BeEmpty();
        result.SourceActorAliases.Should().HaveCount(2);
        result
            .SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Select(assertion => assertion.Name)
            .Should()
            .Equal("Directory Robin", "Andy Moresi");
        var rootScope = result.SourceActorAliases.Single(scope =>
            scope.InputRevisionUuid == root.Uuid
        );
        rootScope.Resource.Should().Be(root.Resource);
        rootScope.FileOrigin.Should().BeNull();
        rootScope.RecordingOrigin.Item.Should().Be(directory.Resource);
        var rootName = rootScope.Extraction.Assertions.Single().NameEvidence;
        rootName.JsonPointer.Should().Be("/data/1970-xx-xx/0/taper_notes");
        using var rootDocument = NormalizationRetainedDocument.Open(root);
        rootDocument.ValidateEvidence(rootName);
    }

    [Test]
    public void MissingOriginalFieldsProduceNoAliasCoverage()
    {
        var context = Archive(null, new string?[] { null, null }, format: null);
        var recording = context.Decoded.Recordings.Single();
        recording.Text.Should().BeEmpty();
        recording.Files.Should().HaveCount(2).And.OnlyContain(file => file.Text.Count == 0);

        var result = Extract(context);

        result.SourceActorAliases.Should().NotBeNull().And.BeEmpty();
    }

    [Test]
    public void AnExplicitEmptyFileFieldRetainsItsOriginWhenSiblingTextIsAbsent()
    {
        var context = Archive(null, new string?[] { null, "" }, format: null);
        var recording = context.Decoded.Recordings.Single();

        var result = Extract(context);

        var scope = result.SourceActorAliases.Should().ContainSingle().Subject;
        scope.FileOrigin.Should().Be(recording.Files[1].Origin);
        scope.InputRevisionUuid.Should().Be(context.Input.Uuid);
        scope.Extraction.Assertions.Should().BeEmpty();
        var coverage = scope.Extraction.Scopes.Should().ContainSingle().Subject;
        coverage.Complete.Should().BeFalse();
        coverage.Reason.Should().Be("field_clear_semantics_unavailable");
        var evidence = coverage.Evidence.Should().ContainSingle().Subject;
        evidence.JsonPointer.Should().Be("/files/1/description");
        evidence.Quote.Should().Be("");
        using var document = NormalizationRetainedDocument.Open(context.Input);
        document.ValidateEvidence(evidence);
    }

    [TestCase("revision")]
    [TestCase("identity")]
    public void AFileWithoutTextMustStillAuthenticateItsOriginalOrigin(string invalidPart)
    {
        var original = Archive(null, new string?[] { null }, format: null);
        var recording = original.Decoded.Recordings.Single();
        var file = recording.Files.Single();
        file =
            invalidPart == "revision"
                ? file with
                {
                    Origin = file.Origin with
                    {
                        Evidence = file.Origin.Evidence with { InputRevisionUuid = Guid.NewGuid() },
                    },
                }
                : file with
                {
                    Origin = file.Origin with { Item = new("archive.org", "file", "foreign-file") },
                };
        var changed = original with
        {
            Decoded = original.Decoded with { Recordings = [recording with { Files = [file] }] },
            Binding = original.Binding with
            {
                Files = [original.Binding.Files.Single() with { Observation = file }],
            },
        };

        Action extract = () => Extract(changed);

        extract.Should().Throw<ArgumentException>();
    }

    [Test]
    public void AnEmptyOriginalFieldRemainsAnExplicitIncompleteScope()
    {
        var result = Extract(Archive(""));

        var scope = result.SourceActorAliases.Should().ContainSingle().Subject;
        scope.Extraction.Assertions.Should().BeEmpty();
        scope.Extraction.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
        scope
            .Extraction.Scopes.Single()
            .Evidence.Should()
            .ContainSingle()
            .Which.Quote.Should()
            .Be("");
    }

    [Test]
    public void NewScopePayloadSurvivesPublicEnvelopeSerializationWithoutInventingCandidateResults()
    {
        var result = Extract(Archive("Personnel:\nAlice Vale aka Lark - vocals"));
        var saved = JsonSerializer.SerializeToUtf8Bytes(result);
        var restored = JsonSerializer.Deserialize<NormalizationRecordingExtraction>(saved)!;

        restored.Should().BeEquivalentTo(result);
        restored.CandidateSearchStatus.Should().Be("not_run");
        restored
            .SourceActorAliases.Should()
            .ContainSingle()
            .Which.ProducerVersion.Should()
            .Be(RetainedSourceActorAliasExtractor.Version);
    }

    private NormalizationRecordingExtraction Extract(Context context) =>
        extractor.Extract(context.Input, context.Decoded, context.Binding);

    private Context Archive(string? description, params string?[] fileDescriptions) =>
        Archive(description, fileDescriptions, "Flac");

    private Context Archive(string? description, string?[] fileDescriptions, string? format)
    {
        var input = Input(
            new("archive.org", "item_metadata", "alias-envelope"),
            new
            {
                metadata = new { identifier = "alias-envelope", description },
                files = fileDescriptions
                    .Select(
                        (fileDescription, index) =>
                            new
                            {
                                name = "original-" + index + ".flac",
                                format,
                                description = fileDescription,
                            }
                    )
                    .ToArray(),
            }
        );
        var decoded = decoder.Decode(input);
        return new(input, decoded, Bound(input, decoded));
    }

    private static NormalizationRecordingBindingSnapshot Bound(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded
    ) =>
        new(
            input.Uuid,
            input.Resource,
            new(
                NormalizationCatalogBindingStatus.Bound,
                new(Guid.NewGuid(), Guid.NewGuid(), null, null, ["missing_show"]),
                "bound"
            ),
            decoded
                .Recordings.Single()
                .Files.Select(file => new NormalizationFileBinding(
                    file,
                    new(NormalizationCatalogBindingStatus.Missing, null, "unbound_file")
                ))
                .ToArray(),
            [],
            []
        );

    private static NormalizationInputRevision Input(
        NormalizationResourceKey resource,
        object payload
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload),
            MediaType = "application/json",
            CaptureVersion =
                resource.Provider == "local" ? "local-http-json-v1" : "archive-item-response-v1",
        };

    private sealed record Context(
        NormalizationInputRevision Input,
        ProviderDecodeResult Decoded,
        NormalizationRecordingBindingSnapshot Binding
    );
}
