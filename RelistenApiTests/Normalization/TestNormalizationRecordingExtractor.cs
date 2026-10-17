using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Tests the proposal boundary; synthetic catalog bindings do not establish match accuracy.</summary>
[TestFixture]
public sealed class TestNormalizationRecordingExtractor
{
    private readonly NormalizationRecordingExtractor extractor = new(new(), new());

    [Test]
    public void RepeatedCreditsHaveDistinctStableProposalKeysAndKeepTheirOriginalEvidence()
    {
        var context = Context(
            new Dictionary<string, object?>
            {
                ["description"] = "Taped by Pat<br>Taped by Pat<br>Transferred by Pat",
            },
            ["Song"]
        );

        var result = Extract(context);
        var replay = Extract(context);

        result
            .Credits.Select(c => c.Mention.Role)
            .Should()
            .Equal(
                RecordingCreditRole.Taper,
                RecordingCreditRole.Taper,
                RecordingCreditRole.Transferrer
            );
        result.Credits.Select(c => c.ProposalUuid).Should().OnlyHaveUniqueItems();
        replay.Should().BeEquivalentTo(result);
        result.Credits[0].Mention.Evidence.Should().NotBe(result.Credits[1].Mention.Evidence);
        result.CandidateSearchStatus.Should().Be("not_run");
        using var retained = NormalizationRetainedDocument.Open(context.Input);
        foreach (var credit in result.Credits)
        {
            retained.ValidateEvidence(credit.Mention.Evidence);
        }
    }

    [Test]
    public void CompoundUnicodeTitlesRetainTheFieldAndCodePointOffsetsWithoutAudioClaims()
    {
        const string title = "🎵 Song > Étoile";
        var context = Context([], [title]);

        var result = Extract(context);

        var proposal = result.Titles.Should().ContainSingle().Subject;
        proposal.Title.Should().Be(context.Decoded.Recordings.Single().Files.Single().Title);
        proposal.Extraction!.OriginalTitle.Should().Be(title);
        proposal.Extraction.ArtistUuid.Should().Be(context.Binding.Recording.Value!.ArtistUuid);
        proposal.Extraction.Segments.Select(s => s.Title).Should().Equal("🎵 Song", "Étoile");
        var second = proposal.Extraction.Segments[1];
        second.Evidence.StartCodePoint.Should().Be(9);
        second.Evidence.EndCodePoint.Should().Be(15);
        proposal
            .Extraction.Segments.Should()
            .OnlyContain(segment =>
                segment.OccurrenceUuid == null && segment.StartMs == null && segment.EndMs == null
            );
        foreach (var segment in proposal.Extraction.Segments)
        {
            var quote = string.Concat(
                title
                    .EnumerateRunes()
                    .Skip(segment.Evidence.StartCodePoint)
                    .Take(segment.Evidence.EndCodePoint - segment.Evidence.StartCodePoint)
            );
            quote.Should().Be(segment.Evidence.Quote);
        }
    }

    [Test]
    public void MissingTitleAndUnresolvedSiblingKeepSourceCreditsAndDecoderIssues()
    {
        var context = Context(
            new Dictionary<string, object?>
            {
                ["taper"] = "Pat Recorder",
                ["transferrer"] = "unknown",
            },
            [null, "Unbound song"]
        );
        var files = context.Binding.Files.ToArray();
        files[1] = files[1] with
        {
            Binding = new(NormalizationCatalogBindingStatus.Missing, null, "missing_source_set"),
        };
        context = context with { Binding = context.Binding with { Files = files } };

        var result = Extract(context);

        result.Credits.Should().ContainSingle(c => c.Mention.Name == "Pat Recorder");
        result.UnresolvedCredits.Should().ContainSingle(c => c.Reason == "unknown_credit");
        var title = result.Titles.Should().ContainSingle().Subject;
        title.TrackUuid.Should().Be(files[0].Binding.Value!.TrackUuid);
        title.Title.Should().BeNull();
        title.Extraction.Should().BeNull();
        title.Reason.Should().Be("missing_title");
        result.DecodeIssues.Should().Equal(context.Decoded.Issues);
        context.Binding.Files[1].Binding.Reason.Should().Be("missing_source_set");
    }

    [TestCase("input_revision")]
    [TestCase("input_resource")]
    [TestCase("decoder_revision")]
    [TestCase("decoder_resource")]
    [TestCase("unbound_source")]
    [TestCase("file_artist")]
    [TestCase("file_source")]
    [TestCase("file_origin")]
    public void MismatchedEvidenceOrCatalogOwnershipCannotProduceProposals(string mismatch)
    {
        var context = Context([], ["Song"]);
        var binding = context.Binding;
        if (mismatch == "input_revision")
        {
            binding = binding with { InputRevisionUuid = Guid.NewGuid() };
        }
        if (mismatch == "input_resource")
        {
            binding = binding with
            {
                Resource = binding.Resource with { ProviderKey = "another-source" },
            };
        }
        if (mismatch == "decoder_revision")
        {
            context = context with
            {
                Decoded = context.Decoded with { InputRevisionUuid = Guid.NewGuid() },
            };
        }
        if (mismatch == "decoder_resource")
        {
            context = context with
            {
                Decoded = context.Decoded with
                {
                    Resource = binding.Resource with { ProviderKey = "another-source" },
                },
            };
        }
        if (mismatch == "unbound_source")
        {
            binding = binding with
            {
                Recording = new(
                    NormalizationCatalogBindingStatus.Missing,
                    null,
                    "missing_recording"
                ),
            };
        }
        if (mismatch is "file_artist" or "file_source")
        {
            var file = binding.Files.Single();
            var reference = file.Binding.Value!;
            reference =
                mismatch == "file_artist"
                    ? reference with
                    {
                        ArtistUuid = Guid.NewGuid(),
                    }
                    : reference with
                    {
                        SourceUuid = Guid.NewGuid(),
                    };
            binding = binding with
            {
                Files = [file with { Binding = file.Binding with { Value = reference } }],
            };
        }
        if (mismatch == "file_origin")
        {
            var file = binding.Files.Single();
            binding = binding with
            {
                Files =
                [
                    file with
                    {
                        Observation = file.Observation with
                        {
                            Origin = file.Observation.Origin with
                            {
                                Evidence = new(context.Input.Uuid, "/source"),
                            },
                        },
                    },
                ],
            };
        }
        context = context with { Binding = binding };

        var extract = () => Extract(context);
        extract.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ChangedTitleTextCannotBorrowValidEvidenceEvenWhenBothObservationsAgree()
    {
        var context = Context([], ["Original Song"]);
        var recording = context.Decoded.Recordings.Single();
        var file = recording.Files.Single() with
        {
            Title = recording.Files.Single().Title! with { Value = "Invented Song" },
        };
        context = context with
        {
            Decoded = context.Decoded with { Recordings = [recording with { Files = [file] }] },
            Binding = context.Binding with
            {
                Files = [context.Binding.Files.Single() with { Observation = file }],
            },
        };

        var extract = () => Extract(context);
        extract.Should().Throw<ArgumentException>();
    }

    [TestCase("source:2169954", "Dave Sorochty", RecordingCreditRole.Transferrer)]
    [TestCase("source:1329586", "Keith Litzenberger", RecordingCreditRole.Taper)]
    [TestCase("local:source:4283434", "Scott Bernstein", RecordingCreditRole.Taper)]
    public void RetainedCrossArtistCreditFieldsProduceCitedProposals(
        string evidenceId,
        string name,
        RecordingCreditRole role
    )
    {
        var row = CorpusRow(evidenceId);
        var fields = row.GetProperty("fields")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        // Credit wording is copied exactly from the retained catalog into the snapshot contract.
        // Catalog IDs are synthetic; the fixture does not assert recovered provider bytes.
        var context = Context(fields, []);

        var result = Extract(context);

        result.Credits.Should().Contain(c => c.Mention.Name == name && c.Mention.Role == role);
        using var retained = NormalizationRetainedDocument.Open(context.Input);
        foreach (var proposal in result.Credits)
        {
            retained.ValidateEvidence(proposal.Mention.Evidence);
        }
    }

    [TestCase("track:33689439", "Only My Fear", "Rilke")]
    [TestCase("track:8261162", "BBF3", "The Sad Mafioso")]
    public void RetainedCompoundTitlesRemainSeparateMentions(
        string evidenceId,
        string first,
        string second
    )
    {
        var title = CorpusRow(evidenceId).GetProperty("fields").GetProperty("title").GetString()!;
        var result = Extract(Context([], [title]));

        result
            .Titles.Single()
            .Extraction!.Segments.Select(segment => segment.Title)
            .Should()
            .Equal(first, second);
    }

    [Test]
    public void ANewRetainedRevisionCreatesNewProposalKeysEvenForEqualWording()
    {
        var first = Context(
            new Dictionary<string, object?> { ["taper"] = "Pat Recorder" },
            ["Song"]
        );
        var otherInput = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = first.Input.Resource,
            Payload = first.Input.Payload,
            MediaType = first.Input.MediaType,
            CaptureVersion = first.Input.CaptureVersion,
        };
        var second = Bound(otherInput);

        var firstResult = Extract(first);
        var secondResult = Extract(second);

        secondResult
            .Credits.Single()
            .ProposalUuid.Should()
            .NotBe(firstResult.Credits.Single().ProposalUuid);
        secondResult
            .Titles.Single()
            .ProposalUuid.Should()
            .NotBe(firstResult.Titles.Single().ProposalUuid);
    }

    private NormalizationRecordingExtraction Extract(ExtractionContext context) =>
        extractor.Extract(context.Input, context.Decoded, context.Binding);

    private static ExtractionContext Context(Dictionary<string, object?> fields, string?[] titles)
    {
        var sourceUuid = Guid.NewGuid();
        var artistUuid = Guid.NewGuid();
        var setUuid = Guid.NewGuid();
        fields = new(fields)
        {
            ["id"] = 11,
            ["uuid"] = sourceUuid,
            ["artist_id"] = 7,
        };
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("legacy_catalog", "recording", sourceUuid.ToString("D")),
            MediaType = "application/json",
            CaptureVersion = "legacy-recording-snapshot-v2",
            Payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    representation = "legacy_catalog",
                    source = fields,
                    artist = new
                    {
                        id = 7,
                        uuid = artistUuid,
                        name = "Fixture artist",
                    },
                    show = (object?)null,
                    source_venue = (object?)null,
                    show_venue = (object?)null,
                    configured_artist_mappings = Array.Empty<object>(),
                    sets = new[]
                    {
                        new
                        {
                            id = 21,
                            uuid = setUuid,
                            source_id = 11,
                        },
                    },
                    tracks = titles
                        .Select(
                            (title, index) =>
                                new
                                {
                                    track = new
                                    {
                                        id = 100 + index,
                                        uuid = Guid.NewGuid(),
                                        source_id = 11,
                                        artist_id = 7,
                                        source_set_id = 21,
                                        title,
                                        mp3_url = "https://example.test/" + index + ".mp3",
                                    },
                                    source_context = new
                                    {
                                        id = 11,
                                        uuid = sourceUuid,
                                        artist_id = 7,
                                    },
                                    set_context = new
                                    {
                                        id = 21,
                                        uuid = setUuid,
                                        source_id = 11,
                                    },
                                    artist_context = new { id = 7, uuid = artistUuid },
                                }
                        )
                        .ToArray(),
                }
            ),
        };
        return Bound(input);
    }

    private static ExtractionContext Bound(NormalizationInputRevision input)
    {
        var decoded = new NormalizationProviderDecoder().Decode(input);
        using var document = JsonDocument.Parse(input.Payload);
        var sourceUuid = document.RootElement.GetProperty("source").GetProperty("uuid").GetGuid();
        var artistUuid = document.RootElement.GetProperty("artist").GetProperty("uuid").GetGuid();
        var setUuid = document.RootElement.GetProperty("sets")[0].GetProperty("uuid").GetGuid();
        var files = decoded
            .Recordings.Single()
            .Files.Select(file => new NormalizationFileBinding(
                file,
                new(
                    NormalizationCatalogBindingStatus.Bound,
                    new(
                        artistUuid,
                        sourceUuid,
                        setUuid,
                        Guid.Parse(file.Identities.Single().Identity.ProviderKey),
                        file.PlaybackLocator!.Value
                    ),
                    "bound"
                )
            ))
            .ToArray();
        return new(
            input,
            decoded,
            new(
                input.Uuid,
                input.Resource,
                new(
                    NormalizationCatalogBindingStatus.Bound,
                    new(
                        artistUuid,
                        sourceUuid,
                        null,
                        null,
                        ["missing_show", "missing_source_venue"]
                    ),
                    "bound"
                ),
                files,
                [],
                []
            )
        );
    }

    private static JsonElement CorpusRow(string id)
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "evidence.jsonl"
        );
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("id").GetString() == id)
            {
                return document.RootElement.Clone();
            }
        }
        throw new InvalidOperationException("Retained fixture row is missing: " + id);
    }

    private sealed record ExtractionContext(
        NormalizationInputRevision Input,
        ProviderDecodeResult Decoded,
        NormalizationRecordingBindingSnapshot Binding
    );
}
