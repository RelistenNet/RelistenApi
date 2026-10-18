using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestPeoplePairLearnedScorerParity
{
    private const string LearnedArtifact = """
        {
          "version":"people-five-feature-logistic-v1",
          "featureOrder":["name_token_similarity","exact_name","artist_overlap","contact_agreement","profile_agreement"],
          "coefficients":[2,3,5,7,11],
          "intercept":-1.25,
          "classes":[0,1],
          "scoreKind":"raw_logit",
          "runtimeScorerParameters":{
            "intercept":-1.25,
            "nameSimilarityWeight":2,
            "exactNameWeight":3,
            "artistOverlapWeight":5,
            "contactAgreementWeight":7,
            "profileAgreementWeight":11
          }
        }
        """;

    [TestCase(true, true, 25.75)]
    [TestCase(false, false, 7.75)]
    [TestCase(true, false, 14.75)]
    public void ExportedLearnedParametersProduceTheExpectedRawLogitThroughThePublicEvaluator(
        bool contactAgrees,
        bool profileAgrees,
        double expectedWinnerScore
    )
    {
        using var artifact = JsonDocument.Parse(LearnedArtifact);
        var root = artifact.RootElement;
        root.GetProperty("scoreKind").GetString().Should().Be("raw_logit");
        root.GetProperty("classes")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .Should()
            .Equal(0, 1);
        root.GetProperty("featureOrder")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Equal(
                "name_token_similarity",
                "exact_name",
                "artist_overlap",
                "contact_agreement",
                "profile_agreement"
            );
        var parameters = root.GetProperty("runtimeScorerParameters");
        var scorer = new PeopleAcceptanceLinearScorer(
            parameters.GetProperty("intercept").GetDouble(),
            parameters.GetProperty("nameSimilarityWeight").GetDouble(),
            parameters.GetProperty("exactNameWeight").GetDouble(),
            parameters.GetProperty("artistOverlapWeight").GetDouble(),
            MatchThreshold: -100,
            MinimumRunnerUpMargin: 0,
            ContactAgreementWeight: parameters.GetProperty("contactAgreementWeight").GetDouble(),
            ProfileAgreementWeight: parameters.GetProperty("profileAgreementWeight").GetDouble()
        );
        root.GetProperty("coefficients")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .Should()
            .Equal(
                scorer.NameSimilarityWeight,
                scorer.ExactNameWeight,
                scorer.ArtistOverlapWeight,
                scorer.ContactAgreementWeight,
                scorer.ProfileAgreementWeight
            );
        root.GetProperty("intercept").GetDouble().Should().Be(scorer.Intercept);
        var pair = new IdentityCandidateComparison(
            Guid.NewGuid(),
            IdentityMatchingDomain.People,
            IdentityMatchDisposition.NeedsEvidence,
            true,
            0.5,
            [
                new("name", IdentityFeatureAgreement.Agrees, "fixture exact name"),
                new("artist_context", IdentityFeatureAgreement.Agrees, "fixture artist overlap"),
                new(
                    "actor_contact",
                    contactAgrees
                        ? IdentityFeatureAgreement.Agrees
                        : IdentityFeatureAgreement.Missing,
                    "fixture contact"
                ),
                new(
                    "actor_profile",
                    profileAgrees
                        ? IdentityFeatureAgreement.Agrees
                        : IdentityFeatureAgreement.Missing,
                    "fixture profile"
                ),
            ],
            [],
            null,
            null,
            null
        );
        var claim = CompleteClaim(pair);

        var decision = new PeopleResolutionPolicyEvaluator().Evaluate(
            claim,
            FixturePolicy(scorer),
            1,
            "retained_credit"
        );

        decision.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        decision.WinnerScore.Should().Be(expectedWinnerScore);
        decision.Candidates.Should().ContainSingle().Which.Score.Should().Be(expectedWinnerScore);
        decision
            .WinnerScore.Should()
            .BeGreaterThan(
                1,
                "the runtime score is the exported raw logit, not a sigmoid probability"
            );
        claim.Decision.AutomaticAcceptanceAuthorized.Should().BeFalse();
    }

    private static NormalizationPeopleResolutionClaim CompleteClaim(
        IdentityCandidateComparison pair
    )
    {
        const string name = "Fixture Contributor";
        var hit = new NormalizationCandidateHit(
            pair.CandidateUuid!.Value,
            1,
            name,
            Guid.NewGuid(),
            pair.NameTokenSimilarity,
            ["name"]
        );
        var facts = new PersonCandidateFacts
        {
            Hit = hit,
            Header = new(hit.Uuid, name, 1, hit.OwnerSubjectUuid, "person"),
        };
        var snapshot = new NormalizationCandidateFactsSnapshot<PersonCandidateFacts>(
            NormalizationCandidateService.FactsVersion,
            "fixture-complete-pool",
            new(IdentityMatchingDomain.People, [name], []),
            new(),
            new(IdentityMatchingDomain.People, [hit], false),
            [facts],
            new([], [], []),
            []
        );
        return new(
            new(
                Guid.NewGuid(),
                new(name, RecordingCreditRole.Taper, new(Guid.NewGuid(), "/taper", Quote: name))
            ),
            Guid.NewGuid(),
            null,
            null,
            snapshot,
            [pair],
            new(PeopleResolutionAction.Review, "fixture_unevaluated_proposal")
        );
    }

    private static RegisteredPeopleAcceptancePolicy FixturePolicy(
        PeopleAcceptanceLinearScorer scorer
    )
    {
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "learned-parity-fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.FactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            ["retained_credit"],
            scorer,
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var receipt = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-independent-evaluation-v1",
            "fixture-independent-reviewer",
            "fixture-operator",
            new(10, 0),
            FixtureOnly: true
        );
        receipt = receipt with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(receipt),
        };
        return new(artifact, receipt);
    }
}

/// <summary>Small synthetic bundles authenticate selection and provenance before matcher projection.</summary>
[TestFixture]
public sealed class TestPeoplePairBaselineBatch
{
    private const string AnnotationPath =
        "local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/evidence.jsonl";
    private const string ReservationPath = "local-dev/synthetic-people/reservation-index.json";
    private const string SelectedPath = "local-dev/synthetic-people/original-selected.jsonl";
    private string directory = null!;
    private string repository = null!;
    private string bundle = null!;
    private string output = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "people-pair-baseline-test-" + Guid.NewGuid().ToString("N")
        );
        repository = Path.Combine(directory, "repository");
        bundle = Path.Combine(directory, "bundle");
        output = Path.Combine(directory, "output");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(repository);
    }

    [TearDown]
    public void Stop()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ExactUnicodeSourceProjectionExportsEveryDeclaredCandidateIncludingUnknowns()
    {
        var manifestHash = await Bundle();

        var report = await Run(manifestHash);

        report.State.Should().Be("completed");
        report.Pairs.Should().Be(2);
        report.Questions.Should().Be(1);
        report.AllTasks.Should().Be(2);
        report.BundleManifestSha256.Should().Be(manifestHash);
        report.FeatureOrder.Should().Equal(PeoplePairBaselineBatch.FeatureOrder);
        report.PolicyActivationPermitted.Should().BeFalse();
        report
            .MissingContext.Should()
            .Contain("actor_kind")
            .And.Contain("contacts")
            .And.Contain("profiles");
        report
            .BaselineSha256.Should()
            .Be(Hash(await File.ReadAllBytesAsync(Path.Combine(output, report.BaselineFile))));
        report
            .MatcherAssemblySha256.Should()
            .Be(
                Hash(
                    await File.ReadAllBytesAsync(typeof(IdentityCandidateMatcher).Assembly.Location)
                )
            );
        report
            .RunnerAssemblySha256.Should()
            .Be(
                Hash(
                    await File.ReadAllBytesAsync(typeof(PeoplePairBaselineBatch).Assembly.Location)
                )
            );
        foreach (var (name, receipt) in report.InputFiles)
        {
            var path = Path.Combine(
                name.StartsWith("local-dev/", StringComparison.Ordinal) ? repository : bundle,
                name
            );
            var bytes = await File.ReadAllBytesAsync(path);
            receipt.Sha256.Should().Be(Hash(bytes));
            receipt.Bytes.Should().Be(bytes.LongLength);
        }
        var rows = ReadRows(output);
        rows.Should().HaveCount(2);
        rows.Select(row => row.GetProperty("candidateMentionId").GetString())
            .Should()
            .Equal("mention-1", "mention-2");
        rows.Should().OnlyContain(row => row.GetProperty("declaredCandidateCount").GetInt32() == 2);
        rows[0]
            .GetProperty("features")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .Should()
            .Equal(1, 1, 1, 0, 0);
        rows[1].GetProperty("projectionSupported").GetBoolean().Should().BeFalse();
        rows[1]
            .GetProperty("projectionIssue")
            .GetString()
            .Should()
            .Be("unsupported_mention_expression");
        foreach (var row in rows)
        {
            var comparison = row.GetProperty("comparison");
            comparison.GetProperty("candidateUuid").ValueKind.Should().Be(JsonValueKind.Null);
            comparison.GetProperty("policyVersion").ValueKind.Should().Be(JsonValueKind.Null);
            comparison.GetProperty("supportedRules").GetArrayLength().Should().Be(0);
            row.GetProperty("features")[3].GetDouble().Should().Be(0);
            row.GetProperty("features")[4].GetDouble().Should().Be(0);
        }
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(output, "baseline.partial.jsonl")).Should().BeFalse();
    }

    [Test]
    public async Task LabelAnswersRationalesAndProposedFeaturesCannotAlterTheActualMatcherProjection()
    {
        var manifestHash = await Bundle(answer: "same_actor");
        await Run(manifestHash);
        var first = ReadRows(output).Select(row => row.GetRawText()).ToArray();
        manifestHash = await Bundle(answer: "different_actors");
        var secondOutput = Path.Combine(directory, "other-output");

        await new PeoplePairBaselineBatch().RunAsync(
            repository,
            bundle,
            secondOutput,
            manifestHash
        );

        ReadRows(secondOutput).Select(row => row.GetRawText()).Should().Equal(first);
    }

    [Test]
    public async Task ExistingOutputIsNeitherReusedNorOverwritten()
    {
        var manifestHash = await Bundle();
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "original");

        var run = () => Run(manifestHash);

        await run.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*new people baseline output directory*");
        (await File.ReadAllTextAsync(Path.Combine(output, "keep.txt"))).Should().Be("original");
        Directory.GetFiles(output).Should().ContainSingle();
    }

    [TestCase("reserved", "*reserved or not eligible*")]
    [TestCase("heldout", "*Held-out source selection*")]
    [TestCase("reserved_group", "*Selected source metadata*")]
    [TestCase("reserved_family", "*Selected source metadata*")]
    [TestCase("reservation_receipt", "*reservation provenance*")]
    public async Task ForbiddenSelectionIsRejectedBeforeMalformedSelectedSourceTextCanBeDecoded(
        string fault,
        string expectedMessage
    )
    {
        var manifestHash = await Bundle(fault);

        var run = () => Run(manifestHash);

        await run.Should().ThrowAsync<InvalidDataException>().WithMessage(expectedMessage);
        AssertNoCompletedOutput();
    }

    [Test]
    public async Task AChangedFrozenManifestCannotBeReauthenticatedBySupplyingNewInputHashes()
    {
        var manifestHash = await Bundle();
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(bundle, "manifest.json"))
        )!;
        manifest["untrusted_extra_receipt"] = "changed";
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "manifest.json"),
            manifest.ToJsonString()
        );

        var run = () => Run(manifestHash);

        await run.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*frozen bundle manifest changed*");
        AssertNoCompletedOutput();
    }

    [TestCase("mentions.jsonl")]
    [TestCase("external_reservation")]
    [TestCase("external_selected")]
    [TestCase("annotation")]
    public async Task OriginalExternalAndBundleByteHashesMustStillMatch(string changedFile)
    {
        var manifestHash = await Bundle();
        var path = changedFile switch
        {
            "external_reservation" => Path.Combine(repository, ReservationPath),
            "external_selected" => Path.Combine(repository, SelectedPath),
            "annotation" => Path.Combine(repository, AnnotationPath),
            _ => Path.Combine(bundle, changedFile),
        };
        await File.AppendAllTextAsync(path, " ");

        var run = () => Run(manifestHash);

        await run.Should().ThrowAsync<InvalidDataException>();
        AssertNoCompletedOutput();
    }

    [Test]
    public async Task UnicodeCitationsRejectUtf16OffsetsEvenWhenEveryFileHashIsUpdated()
    {
        var manifestHash = await Bundle("utf16_span");

        var run = () => Run(manifestHash);

        await run.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*exact Unicode source citation*");
        AssertNoCompletedOutput();
    }

    [Test]
    public async Task TheFrozenMentionInventoryCannotOmitAnUnresolvedCandidate()
    {
        var manifestHash = await Bundle("missing_unknown_pool_member");

        var run = () => Run(manifestHash);

        await run.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*frozen mention inventory*");
        AssertNoCompletedOutput();
    }

    [Test]
    public async Task APrecancelledRequestCannotLeaveACompletedExport()
    {
        var manifestHash = await Bundle();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var run = () => Run(manifestHash, cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        AssertNoCompletedOutput();
    }

    private Task<PeoplePairBaselineReport> Run(
        string manifestHash,
        CancellationToken ct = default
    ) => new PeoplePairBaselineBatch().RunAsync(repository, bundle, output, manifestHash, ct);

    private void AssertNoCompletedOutput()
    {
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "baseline.jsonl")).Should().BeFalse();
    }

    private async Task<string> Bundle(string? fault = null, string answer = "same_actor")
    {
        var sourceIndex = new List<JsonObject>();
        var citations = new List<JsonObject>();
        var mentions = new List<JsonObject>();
        var originalLines = new List<byte[]>();
        var allowed = new JsonArray();
        var artist = new JsonObject
        {
            ["uuid"] = Guid.NewGuid().ToString(),
            ["name"] = "Synthetic Artist",
        };
        var names = new[] { "😀 José Contributor", "😀 José Contributor", "unknown taper" };
        for (var index = 0; index < names.Length; index++)
        {
            var id = "source-" + (index + 1);
            var sourceUuid = Guid.NewGuid();
            var sourceId = 100 + index;
            var family = "synthetic-family-" + index;
            var text = "Taper: " + names[index] + "\n";
            var knownFamily =
                fault == "reserved_family" && index == 0 ? "reserved-recording" : null;
            var original = new JsonObject
            {
                ["evidence_id"] = id,
                ["artist_partition"] = "development",
                ["family_partition"] = "development",
                ["input_duplicate_family"] = family,
                ["known_recording_family"] = knownFamily,
                ["artist"] = artist.DeepClone(),
                ["raw_record"] = new JsonObject
                {
                    ["kind"] = "source_sample",
                    ["row"] = new JsonObject
                    {
                        ["id"] = sourceId,
                        ["uuid"] = sourceUuid.ToString(),
                        ["taper"] = text,
                    },
                },
            };
            var originalBytes = Encoding.UTF8.GetBytes(original.ToJsonString());
            if (
                index == 0
                && fault
                    is "reserved"
                        or "heldout"
                        or "reserved_group"
                        or "reserved_family"
                        or "reservation_receipt"
            )
            {
                originalBytes = Encoding.UTF8.GetBytes(
                    "deliberately not JSON selected source text"
                );
            }
            originalLines.Add(originalBytes);
            sourceIndex.Add(
                new()
                {
                    ["evidence_id"] = id,
                    ["artist_partition"] =
                        fault == "heldout" && index == 0 ? "validation" : "development",
                    ["family_partition"] = "development",
                    ["family_group"] = family,
                    ["known_recording_family"] = knownFamily,
                    ["artist"] = artist.DeepClone(),
                    ["annotation_line_number"] = index + 1,
                    ["annotation_line_sha256"] = Hash(originalBytes),
                    ["source_id"] = sourceId,
                    ["source_uuid"] = sourceUuid.ToString(),
                }
            );
            allowed.Add(
                new JsonObject
                {
                    ["evidence_id"] = id,
                    ["artist_partition"] = "development",
                    ["family_partition"] = "development",
                    ["family_group"] = family,
                    ["family"] = knownFamily,
                    ["artist_uuid"] = artist["uuid"]!.GetValue<string>(),
                    ["source_line"] = index + 1,
                    ["reference"] = "source:" + sourceId,
                }
            );
            citations.Add(
                new()
                {
                    ["citation_id"] = "citation-" + index,
                    ["evidence_id"] = id,
                    ["field_path"] = "/raw_record/row/taper",
                    ["field_sha256"] = Hash(Encoding.UTF8.GetBytes(text)),
                    ["span"] = new JsonObject
                    {
                        ["start"] = 7,
                        ["end"] =
                            7
                            + (
                                fault == "utf16_span" && index == 0
                                    ? names[index].Length
                                    : names[index].EnumerateRunes().Count()
                            ),
                        ["quote"] = names[index],
                        ["unit"] = "unicode_code_points",
                    },
                }
            );
            mentions.Add(
                new()
                {
                    ["mention_id"] = "mention-" + index,
                    ["evidence_id"] = id,
                    ["citation_id"] = "citation-" + index,
                    ["name"] = names[index],
                    ["artist"] = artist.DeepClone(),
                    ["mention_kind"] = index == 2 ? "unresolved_reference" : "name",
                }
            );
        }
        var annotation = JoinLines(originalLines.Append(new byte[] { 0xff, 0xfe }));
        var selected = JoinLines(originalLines);
        var reservations = new JsonObject
        {
            ["eligible_source_metadata"] = allowed,
            ["excluded_annotation_metadata"] =
                fault == "reserved"
                    ? new JsonArray(new JsonObject { ["evidence_id"] = "source-1" })
                    : new JsonArray(),
            ["reserved_annotation_family_groups"] =
                fault == "reserved_group" ? new JsonArray("synthetic-family-0") : new JsonArray(),
            ["reserved_known_families"] =
                fault == "reserved_family" ? new JsonArray("reserved-recording") : new JsonArray(),
        };
        var reservationsBytes = Encoding.UTF8.GetBytes(reservations.ToJsonString());
        await WriteBelow(repository, AnnotationPath, annotation);
        await WriteBelow(repository, ReservationPath, reservationsBytes);
        await WriteBelow(repository, SelectedPath, selected);
        var reservationReceipt = Receipt(ReservationPath, reservationsBytes);
        var mentionsBytes = Rows(mentions);
        var poolIds = new JsonArray(
            mentions
                .Select(mention => JsonValue.Create(mention["mention_id"]!.GetValue<string>()))
                .ToArray()
        );
        if (fault == "missing_unknown_pool_member")
        {
            poolIds.RemoveAt(poolIds.Count - 1);
        }
        var questions = new[]
        {
            new JsonObject
            {
                ["task_id"] = "pair-task",
                ["task_kind"] = "cross_recording_identity",
                ["focus_mention_id"] = "mention-0",
                ["candidate_mention_ids"] = new JsonArray("mention-1", "mention-2"),
                ["status"] = "draft_development",
                ["evaluation_eligible"] = false,
                ["policy_activation_permitted"] = false,
                ["answer"] = new JsonObject
                {
                    ["decision"] = answer,
                    ["reason"] = "Untrusted annotation rationale: " + answer,
                },
                ["features"] = new JsonObject
                {
                    ["contact_agreement"] = answer == "same_actor",
                    ["profile_agreement"] = answer == "same_actor",
                    ["name_token_similarity"] = answer == "same_actor" ? 1 : 0,
                },
            },
            new JsonObject
            {
                ["task_id"] = "unknown-kind-task",
                ["task_kind"] = "actor_kind",
                ["focus_mention_id"] = "mention-2",
                ["status"] = "draft_development",
                ["evaluation_eligible"] = false,
                ["policy_activation_permitted"] = false,
            },
        };
        var fileBytes = new Dictionary<string, byte[]>
        {
            ["source-index.jsonl"] = Rows(sourceIndex),
            ["citations.jsonl"] = Rows(citations),
            ["mentions.jsonl"] = mentionsBytes,
            ["labels.jsonl"] = Rows(questions),
            ["candidate-pool.json"] = Encoding.UTF8.GetBytes(
                new JsonObject
                {
                    ["mention_ids"] = poolIds,
                    ["mentions_file_sha256"] = Hash(mentionsBytes),
                    ["identity_assertion"] = false,
                    ["matcher_feature"] = false,
                    ["creation_authorized"] = false,
                }.ToJsonString()
            ),
            ["reservation-receipt.json"] = Encoding.UTF8.GetBytes(
                new JsonObject
                {
                    ["metadata_only_projection"] = fault != "reservation_receipt",
                    ["seed_labels_read"] = false,
                    ["index"] = reservationReceipt.DeepClone(),
                }.ToJsonString()
            ),
        };
        var files = new JsonObject();
        foreach (var (name, bytes) in fileBytes)
        {
            await WriteBelow(bundle, name, bytes);
            files[name] = Receipt(null, bytes);
        }
        var manifest = new JsonObject
        {
            ["usage"] = "development_only",
            ["policy_activation_permitted"] = false,
            ["identity_accuracy_established"] = false,
            ["files"] = files,
            ["reservation_index"] = reservationReceipt,
            ["original_selected_evidence"] = Receipt(SelectedPath, selected),
            ["source_annotation_evidence_sha256"] = Hash(annotation),
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        await WriteBelow(bundle, "manifest.json", manifestBytes);
        return Hash(manifestBytes);
    }

    private static JsonElement[] ReadRows(string path) =>
        File.ReadLines(Path.Combine(path, "baseline.jsonl"))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();

    private static JsonObject Receipt(string? path, byte[] bytes)
    {
        var receipt = new JsonObject { ["bytes"] = bytes.LongLength, ["sha256"] = Hash(bytes) };
        if (path != null)
        {
            receipt["path"] = path;
        }
        return receipt;
    }

    private static byte[] Rows(IEnumerable<JsonObject> values) =>
        JoinLines(values.Select(value => Encoding.UTF8.GetBytes(value.ToJsonString())));

    private static byte[] JoinLines(IEnumerable<byte[]> lines)
    {
        using var stream = new MemoryStream();
        foreach (var line in lines)
        {
            stream.Write(line);
            stream.WriteByte((byte)'\n');
        }
        return stream.ToArray();
    }

    private static async Task WriteBelow(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
