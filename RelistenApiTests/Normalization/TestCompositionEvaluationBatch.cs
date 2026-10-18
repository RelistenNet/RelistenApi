using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Exercise real pure music interfaces through authenticated packages and saved outputs.</summary>
[TestFixture]
public sealed class TestCompositionEvaluationBatch
{
    private static readonly Guid InputUuid = new("0626a648-2678-47ce-bb8c-b5fb2339a0d8");
    private static readonly Guid Artist = new("6eb30e40-34f0-41f9-82c4-a72e7b0a560b");
    private static readonly Guid Work = new("6671bffb-f9cc-3ea9-96e4-91cdc8d746a6");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private string directory = null!;
    private string package = null!;
    private string output = null!;
    private readonly Dictionary<string, string> titles = new(StringComparer.Ordinal);

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "composition-evaluation-" + Guid.NewGuid().ToString("N")
        );
        package = Path.Combine(directory, "package");
        output = Path.Combine(directory, "output");
        Directory.CreateDirectory(package);
        titles.Clear();
    }

    [TearDown]
    public void Finish() => Directory.Delete(directory, true);

    [Test]
    public async Task EveryFrozenCandidateIsComparedEvenWhenRetrievalReturnsOnlyOne()
    {
        var claim = Record("claim", "Song");
        var same = Record("same", "Song");
        var rival = Record("rival", "Unrelated");
        var hash = await Package(
            [claim, same, rival],
            [TaskRow("task", ["same"])],
            ["same", "rival"]
        );

        var report = await Run(hash);

        report.CandidateComparisons.Should().Be(2);
        report.PolicyActivationPermitted.Should().BeFalse();
        var outcome = Outcomes().Single();
        outcome.Candidates.Select(candidate => candidate.RecordId).Should().Equal("same", "rival");
        outcome.Candidates.Select(candidate => candidate.Retrieved).Should().Equal(true, false);
        var actual = new IdentityCandidateMatcher();
        var expected = actual.CompareCompositions(Match(claim), Match(rival));
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(outcome.Candidates[1].Comparison, Json),
                JsonSerializer.SerializeToElement(expected, Json)
            )
            .Should()
            .BeTrue();
        outcome
            .Candidates[0]
            .Comparison.Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
        outcome
            .Candidates.Should()
            .OnlyContain(candidate => candidate.Comparison.PolicyVersion == null);
        (await File.ReadAllBytesAsync(Path.Combine(output, "package-manifest.json")))
            .Should()
            .Equal(await File.ReadAllBytesAsync(Path.Combine(package, "manifest.json")));
        foreach (var file in report.OutputFiles)
        {
            Hash(await File.ReadAllBytesAsync(Path.Combine(output, file.Key)))
                .Should()
                .Be(file.Value.Sha256);
        }
    }

    [Test]
    public async Task SupportedQualifiedIdentityRemainsDiagnosticWithoutAnAcceptancePolicy()
    {
        var identity = new CompositionEvaluationIdentity(
            new("musicbrainz.org", "work", Work.ToString("D"), IdentifiesEntity: true),
            new(InputUuid, "/externalWork")
        );
        var claim = Record("claim", "Song") with { ExternalIdentities = [identity] };
        var same = Record("same", "Song") with
        {
            CatalogUuid = Guid.NewGuid(),
            ExternalIdentities = [identity],
        };
        await Run(await Package([claim, same], [TaskRow("task", ["same"])], ["same"]));

        var result = Outcomes().Single().Candidates.Single().Comparison;
        result.SupportedRules.Should().Contain(IdentityMatchRule.QualifiedEntityIdentifier);
        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.PolicyVersion.Should().BeNull();
        result.EvaluationReceiptUuid.Should().BeNull();
    }

    [Test]
    public async Task EmptyPoolAndUnavailableRetrievalDoNotBecomeNegativeOrNewWorkLabels()
    {
        var claim = Record("claim", "Song");
        var tasks = Enum.GetValues<CompositionEvaluationRetrievalState>()
            .Select((state, index) => TaskRow("task" + index, [], state))
            .ToArray();
        await Run(await Package([claim], tasks, []));

        var outcomes = Outcomes();
        outcomes
            .Select(row => row.Retrieval.State)
            .Should()
            .BeEquivalentTo(Enum.GetValues<CompositionEvaluationRetrievalState>());
        outcomes.Should().OnlyContain(row => row.UniverseComplete && row.Candidates.Count == 0);
        outcomes.Should().HaveCount(3);
    }

    [Test]
    public async Task RepertoireUsesArtistScopeAndDoesNotMergeSameTitlesAcrossArtists()
    {
        var claim = Record("claim", "Song", IdentityMatchingDomain.Repertoire, Artist);
        var other = Record("other", "Song", IdentityMatchingDomain.Repertoire, Guid.NewGuid());
        var task = TaskRow("task", ["other"]) with { Domain = IdentityMatchingDomain.Repertoire };
        await Run(
            await Package([claim, other], [task], ["other"], IdentityMatchingDomain.Repertoire)
        );

        var comparison = Outcomes().Single().Candidates.Single().Comparison;
        comparison
            .Features.Single(feature => feature.Name == "artist_scope")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Conflicts);
        comparison.Disposition.Should().NotBe(IdentityMatchDisposition.AutomaticMatch);
        var expected = new IdentityCandidateMatcher().CompareRepertoire(
            new(null, Artist, [new("Song")], []),
            new(null, other.ArtistUuid!.Value, [new("Song")], [])
        );
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(comparison, Json),
                JsonSerializer.SerializeToElement(expected, Json)
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task OriginalWorkDecoderAndLiteralQueryBuilderRunWithoutPublication()
    {
        var claim = Record("claim", "😀 Song + Song");
        var seed = new MusicBrainzSearchSeed(MusicBrainzLookupKind.Work, claim.Titles[0], []);
        var task = TaskRow("task", []) with { SearchSeed = seed };
        var workInput = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", "work", Work.ToString("D")),
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        id = Work,
                        title = "External Work",
                        relations = Array.Empty<object>(),
                    }
                )
            ),
            MediaType = "application/json",
            CaptureVersion = "musicbrainz-work-json-v1",
            CapturedAt = DateTime.UnixEpoch,
        };
        workInput = WithHash(workInput);
        var report = await Run(
            await Package(
                [claim],
                [task],
                [],
                extra: new(workInput, true, "synthetic retained provider response")
            )
        );

        report.DecodedInputs.Should().Be(1);
        var decoded = JsonSerializer.Deserialize<CompositionEvaluationDecodedInput>(
            File.ReadLines(Path.Combine(output, "decoded.jsonl")).Single(),
            Json
        )!;
        decoded.Observations.MusicBrainzWorks.Should().ContainSingle();
        decoded.Observations.InputRevisionUuid.Should().Be(workInput.Uuid);
        var expected = MusicBrainzCandidateQueryBuilder.BuildVariants(seed);
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(Outcomes().Single().QueryPlan, Json),
                JsonSerializer.SerializeToElement(expected, Json)
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task UnsupportedDecoderLeavesRetainedInputsWithoutACompletionMarker()
    {
        var claim = Record("claim", "Song");
        var unsupported = WithHash(
            new NormalizationInputRevision
            {
                Uuid = Guid.NewGuid(),
                Resource = new("unsupported", "work", "source"),
                Payload = Encoding.UTF8.GetBytes("{}"),
                MediaType = "application/json",
                CaptureVersion = "unimplemented-v1",
                CapturedAt = DateTime.UnixEpoch,
            }
        );
        var hash = await Package(
            [claim],
            [TaskRow("task", [])],
            [],
            extra: new(unsupported, true, "synthetic unsupported provider profile")
        );
        var run = async () => await Run(hash);

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "package-manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(output, "decoded.partial.jsonl")).Should().BeTrue();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task FailedLaterRetrievalKeepsEarlierCandidatesAndCitedUnrepresentedHits()
    {
        var claim = Record("claim", "Song");
        var same = Record("same", "Song");
        var retrieval = TaskRow(
            "task",
            ["same"],
            CompositionEvaluationRetrievalState.Unavailable
        ).Retrieval with
        {
            Issues = ["later_page_timeout"],
            UnrepresentedIdentities =
            [
                new(
                    new("musicbrainz.org", "work", Work.ToString("D")),
                    new(InputUuid, "/externalWork")
                ),
            ],
            Evidence = [new(InputUuid, "/externalWork")],
        };
        await Run(
            await Package(
                [claim, same],
                [TaskRow("task", ["same"]) with { Retrieval = retrieval }],
                ["same"]
            )
        );

        var result = Outcomes().Single();
        result.Retrieval.State.Should().Be(CompositionEvaluationRetrievalState.Unavailable);
        result.Retrieval.UnrepresentedIdentities.Should().ContainSingle();
        result.Candidates.Single().Retrieved.Should().BeTrue();
        result.Retrieval.Issues.Should().Contain("later_page_timeout");
    }

    [TestCase("label")]
    [TestCase("universe")]
    [TestCase("citation")]
    public async Task UntrustedLabelsOutsidePoolCandidatesAndBrokenUnicodeCitationsRefuseCompletion(
        string defect
    )
    {
        var claim = Record("claim", "😀 Song");
        var other = Record("other", "Song");
        var task = TaskRow("task", ["other"]);
        var hash = await Package([claim, other], [task], ["other"]);
        var name = defect == "citation" ? "records.jsonl" : "tasks.jsonl";
        var rows = File.ReadAllLines(Path.Combine(package, name))
            .Select(line => JsonNode.Parse(line)!)
            .ToArray();
        if (defect == "label")
        {
            rows[0]["answer"] = new JsonObject { ["decision"] = "same_work" };
        }
        else if (defect == "universe")
        {
            rows[0]["retrieval"]!["candidateIds"] = new JsonArray("not-in-frozen-pool");
        }
        else
        {
            rows[0]["titles"]![0]!["evidence"] = JsonSerializer.SerializeToNode(
                new NormalizedEvidenceReference(
                    InputUuid,
                    "/titles/claim",
                    0,
                    "😀 Song".Length,
                    "😀 Song"
                ),
                Json
            );
        }
        await File.WriteAllLinesAsync(
            Path.Combine(package, name),
            rows.Select(row => row.ToJsonString())
        );
        hash = await Manifest();

        var run = async () => await Run(hash);
        if (defect == "citation")
        {
            await run.Should().ThrowAsync<ArgumentException>();
        }
        else
        {
            await run.Should().ThrowAsync<InvalidDataException>();
        }
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task ChangedInputAndWrongManifestPinCannotProduceCompleteOutputs()
    {
        var hash = await Package([Record("claim", "Song")], [TaskRow("task", [])], []);
        var wrongPin = async () => await Run(new string('0', 64));
        await wrongPin.Should().ThrowAsync<InvalidDataException>();
        await File.AppendAllTextAsync(Path.Combine(package, "records.jsonl"), " ");
        var changed = async () => await Run(hash);
        await changed.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task ExistingOutputAndCancellationPreserveOriginalFiles()
    {
        var hash = await Package([Record("claim", "Song")], [TaskRow("task", [])], []);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep"), "original");
        var existing = async () => await Run(hash);
        await existing.Should().ThrowAsync<IOException>();
        File.ReadAllText(Path.Combine(output, "keep")).Should().Be("original");
        var cancelled = async () =>
            await new CompositionEvaluationBatch().RunAsync(
                package,
                Path.Combine(directory, "cancelled"),
                hash,
                new CancellationToken(true)
            );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(directory, "cancelled")).Should().BeFalse();
    }

    private CompositionEvaluationRecord Record(
        string id,
        string title,
        IdentityMatchingDomain domain = IdentityMatchingDomain.Compositions,
        Guid? artist = null
    )
    {
        titles.Add(id, title);
        return new(
            id,
            domain,
            null,
            artist,
            [new(title, new(InputUuid, "/titles/" + id))],
            [],
            [],
            false,
            null,
            [new(InputUuid, "")]
        );
    }

    private static CompositionMatchRecord Match(CompositionEvaluationRecord record) =>
        new(
            record.CatalogUuid,
            record.Titles.Select(title => new IdentityMatchingName(title.Value)).ToArray(),
            [],
            new HashSet<Guid>(),
            false
        );

    private static CompositionEvaluationTask TaskRow(
        string id,
        string[] retrieved,
        CompositionEvaluationRetrievalState state = CompositionEvaluationRetrievalState.Available
    ) =>
        new(
            id,
            IdentityMatchingDomain.Compositions,
            "claim",
            "pool",
            new(state, retrieved, [], [], []),
            null
        );

    private async Task<string> Package(
        CompositionEvaluationRecord[] records,
        CompositionEvaluationTask[] tasks,
        string[] candidates,
        IdentityMatchingDomain domain = IdentityMatchingDomain.Compositions,
        CompositionEvaluationDocument? extra = null
    )
    {
        var input = WithHash(
            new NormalizationInputRevision
            {
                Uuid = InputUuid,
                Resource = new("offline_evaluation", "original_context", "synthetic"),
                Payload = JsonSerializer.SerializeToUtf8Bytes(
                    new { titles, externalWork = Work.ToString("D") }
                ),
                MediaType = "application/json",
                CaptureVersion = "synthetic-v1",
                CapturedAt = DateTime.UnixEpoch,
            }
        );
        var documents = new List<CompositionEvaluationDocument>
        {
            new(input, false, "package-local offline UUID; synthetic source"),
        };
        if (extra != null)
        {
            documents.Add(extra);
        }
        await WriteRows("documents.jsonl", documents);
        await WriteRows("records.jsonl", records);
        await WriteRows(
            "universes.jsonl",
            new[] { new CompositionEvaluationUniverse("pool", domain, candidates, true, []) }
        );
        await WriteRows("tasks.jsonl", tasks);
        return await Manifest();
    }

    private async Task WriteRows<T>(string name, IEnumerable<T> rows) =>
        await File.WriteAllLinesAsync(
            Path.Combine(package, name),
            rows.Select(row => JsonSerializer.Serialize(row, Json))
        );

    private async Task<string> Manifest()
    {
        var files = new Dictionary<string, CompositionEvaluationFile>();
        foreach (
            var name in new[]
            {
                "documents.jsonl",
                "records.jsonl",
                "universes.jsonl",
                "tasks.jsonl",
            }
        )
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(package, name));
            files.Add(
                name,
                new(bytes.Length, Hash(bytes), File.ReadLines(Path.Combine(package, name)).Count())
            );
        }
        var bytesManifest = JsonSerializer.SerializeToUtf8Bytes(
            new CompositionEvaluationPackageManifest("composition-evaluation-package-v1", files),
            Json
        );
        await File.WriteAllBytesAsync(Path.Combine(package, "manifest.json"), bytesManifest);
        return Hash(bytesManifest);
    }

    private Task<CompositionEvaluationReport> Run(string hash) =>
        new CompositionEvaluationBatch().RunAsync(package, output, hash);

    private CompositionEvaluationOutcome[] Outcomes() =>
        File.ReadLines(Path.Combine(output, "outcomes.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CompositionEvaluationOutcome>(line, Json)!)
            .ToArray();

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static NormalizationInputRevision WithHash(NormalizationInputRevision input) =>
        new()
        {
            Uuid = input.Uuid,
            Resource = input.Resource,
            Payload = input.Payload,
            Sha256 = Hash(input.Payload),
            MediaType = input.MediaType,
            CaptureVersion = input.CaptureVersion,
            CapturedAt = input.CapturedAt,
        };
}
