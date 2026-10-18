using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Small source-only packages preserve declared pools and never open separate label targets.</summary>
[TestFixture]
public sealed class TestPeopleAdjudicatedBaselineBatch
{
    private static readonly Guid ArtistUuid = new("8ebf2255-9529-40d9-8386-3bebd7330171");
    private static readonly Guid[] SourceUuids =
    [
        new("72071dc2-c8e7-4fa3-8476-4b5c563172be"),
        new("b1a2691e-7590-48d2-a3b3-462e36a315e7"),
        new("4fa4682e-0dcd-49b4-8c75-ac23ed938920"),
    ];
    private string directory = null!;
    private string package = null!;
    private string output = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "people-adjudicated-test-" + Guid.NewGuid().ToString("N")
        );
        package = Path.Combine(directory, "package");
        output = Path.Combine(directory, "output");
        Directory.CreateDirectory(package);
    }

    [TearDown]
    public void Finish()
    {
        Directory.Delete(directory, true);
    }

    [Test]
    public async Task CompleteSourcePackageRetainsAllTasksAndScopedActorBridges()
    {
        var manifestHash = await Package();

        var report = await Run(manifestHash);

        report.State.Should().Be("completed");
        report.PolicyActivationPermitted.Should().BeFalse();
        report.PackageManifestSha256.Should().Be(manifestHash);
        report.Questions.Should().Be(2);
        report.Pairs.Should().Be(2);
        report.AllTasks.Should().Be(3);
        report.Endpoints.Should().Be(4);
        report.Sources.Should().Be(3);
        report.Groups.Should().Be(2);
        report.FeatureOrder.Should().Equal(PeoplePairBaselineBatch.FeatureOrder);
        report
            .BaselineSha256.Should()
            .Be(Hash(await File.ReadAllBytesAsync(Path.Combine(output, report.BaselineFile))));
        report
            .EndpointSha256.Should()
            .Be(Hash(await File.ReadAllBytesAsync(Path.Combine(output, report.EndpointFile))));
        report
            .GroupingSha256.Should()
            .Be(Hash(await File.ReadAllBytesAsync(Path.Combine(output, report.GroupingFile))));
        report
            .BridgeSha256.Should()
            .Be(Hash(await File.ReadAllBytesAsync(Path.Combine(output, report.BridgeFile))));

        var pairs = Rows(output, report.BaselineFile);
        pairs
            .Select(row => row.GetProperty("candidateEndpointId").GetString())
            .Should()
            .Equal("alias", "other-source");
        pairs
            .Should()
            .OnlyContain(row => row.GetProperty("declaredCandidateCount").GetInt32() == 1);
        pairs.Should().OnlyContain(row => row.GetProperty("projectionSupported").GetBoolean());
        pairs[0]
            .GetProperty("features")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .Should()
            .Equal(1, 1, 1, 1, 0);
        pairs[0]
            .GetProperty("comparison")
            .GetProperty("candidateUuid")
            .ValueKind.Should()
            .Be(JsonValueKind.Null);

        var endpoints = Rows(output, report.EndpointFile)
            .ToDictionary(row => row.GetProperty("endpointId").GetString()!);
        endpoints
            .Keys.Should()
            .BeEquivalentTo(new[] { "focus", "alias", "other-source", "kind-only" });
        endpoints["focus"]
            .GetProperty("variants")
            .GetArrayLength()
            .Should()
            .Be(1, "joint taper and transfer roles describe the same source-local actor");
        var focusReference = endpoints["focus"]
            .GetProperty("variants")[0]
            .GetProperty("actorReference");
        var otherReference = endpoints["other-source"]
            .GetProperty("variants")[0]
            .GetProperty("actorReference");
        focusReference
            .GetProperty("referenceUuid")
            .GetGuid()
            .Should()
            .Be(
                otherReference.GetProperty("referenceUuid").GetGuid(),
                "identical raw fields can repeat a naked local UUID"
            );
        focusReference.GetProperty("evidenceId").GetString().Should().Be(EvidenceId(0));
        otherReference.GetProperty("evidenceId").GetString().Should().Be(EvidenceId(1));

        var bridges = Rows(output, report.BridgeFile);
        bridges.Should().ContainSingle();
        bridges[0]
            .GetProperty("reference")
            .GetProperty("evidenceId")
            .GetString()
            .Should()
            .Be(EvidenceId(0));
        bridges[0].GetProperty("reference").GetProperty("field").GetString().Should().Be("taper");
        bridges[0]
            .GetProperty("endpointIds")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .BeEquivalentTo(new[] { "focus", "alias" });
        bridges[0]
            .GetProperty("taskIds")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .BeEquivalentTo(new[] { "within-task", "cross-task" });
        Rows(output, report.GroupingFile)
            .SelectMany(row => row.GetProperty("taskIds").EnumerateArray())
            .Select(value => value.GetString())
            .Should()
            .BeEquivalentTo(new[] { "within-task", "cross-task", "kind-task" });
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
    }

    [Test]
    public async Task AbsentOrChangedSeparateTargetsCannotAlterSourceFeatures()
    {
        var manifestHash = await Package();
        var first = await Run(manifestHash);
        var baseline = await File.ReadAllBytesAsync(Path.Combine(output, first.BaselineFile));
        var target = Path.Combine(package, "targets", "labels.jsonl");
        File.Exists(target).Should().BeFalse();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "not JSON and not a valid label target\n");
        var secondOutput = Path.Combine(directory, "changed-targets");

        var second = await new PeopleAdjudicatedBaselineBatch().RunAsync(
            package,
            secondOutput,
            manifestHash
        );

        (await File.ReadAllBytesAsync(Path.Combine(secondOutput, second.BaselineFile)))
            .Should()
            .Equal(baseline);
        second.PackageManifestSha256.Should().Be(first.PackageManifestSha256);
    }

    [Test]
    public async Task DifferentScopedActorsAcrossSelectorsKeepEveryEdgeButProduceNoFeatureVector()
    {
        var manifestHash = await Package("multiple_actors");

        var report = await Run(manifestHash);

        var pairs = Rows(output, report.BaselineFile);
        pairs.Should().HaveCount(2);
        foreach (var pair in pairs)
        {
            pair.GetProperty("projectionSupported").GetBoolean().Should().BeFalse();
            pair.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Null);
            pair.GetProperty("comparison").ValueKind.Should().Be(JsonValueKind.Null);
            pair.GetProperty("projectionIssue").GetString().Should().StartWith("focus_");
        }
        var endpoint = Rows(output, report.EndpointFile)
            .Single(row => row.GetProperty("endpointId").GetString() == "focus");
        endpoint.GetProperty("status").GetString().Should().Be("ambiguous");
        endpoint.GetProperty("variants").GetArrayLength().Should().Be(2);
    }

    [Test]
    public async Task TwoExactSelectorsForOneScopedActorRemainComparable()
    {
        var manifestHash = await Package("same_actor_selectors");

        var report = await Run(manifestHash);

        var pairs = Rows(output, report.BaselineFile);
        pairs.Should().HaveCount(2);
        pairs.Should().OnlyContain(row => row.GetProperty("projectionSupported").GetBoolean());
        var endpoint = Rows(output, report.EndpointFile)
            .Single(row => row.GetProperty("endpointId").GetString() == "focus");
        endpoint.GetProperty("status").GetString().Should().Be("projected");
        endpoint.GetProperty("selectors").GetArrayLength().Should().Be(2);
        var references = endpoint
            .GetProperty("variants")
            .EnumerateArray()
            .Select(variant => variant.GetProperty("actorReference").GetRawText())
            .ToArray();
        references.Should().HaveCount(2);
        references.Distinct(StringComparer.Ordinal).Should().ContainSingle();
    }

    [Test]
    public async Task OneMissingSelectorCannotBorrowTheFirstSelectorsActor()
    {
        var manifestHash = await Package("missing_actor_selector");

        var report = await Run(manifestHash);

        var pairs = Rows(output, report.BaselineFile);
        pairs.Should().HaveCount(2);
        pairs.Should().OnlyContain(row => !row.GetProperty("projectionSupported").GetBoolean());
        foreach (var pair in pairs)
        {
            pair.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Null);
            pair.GetProperty("comparison").ValueKind.Should().Be(JsonValueKind.Null);
        }
        var endpoint = Rows(output, report.EndpointFile)
            .Single(row => row.GetProperty("endpointId").GetString() == "focus");
        endpoint.GetProperty("status").GetString().Should().Be("not_found");
        endpoint.GetProperty("selectors").GetArrayLength().Should().Be(2);
        endpoint.GetProperty("variants").GetArrayLength().Should().Be(2);
    }

    [Test]
    public async Task OneUnownedContactHypothesisDoesNotBecomeAComparableActor()
    {
        var manifestHash = await Package("unowned_contact");

        var report = await Run(manifestHash);

        var pairs = Rows(output, report.BaselineFile);
        pairs.Should().HaveCount(2);
        pairs[0].GetProperty("projectionSupported").GetBoolean().Should().BeTrue();
        pairs[1].GetProperty("projectionSupported").GetBoolean().Should().BeFalse();
        pairs[1].GetProperty("features").ValueKind.Should().Be(JsonValueKind.Null);
        pairs[1].GetProperty("comparison").ValueKind.Should().Be(JsonValueKind.Null);
        var endpoint = Rows(output, report.EndpointFile)
            .Single(row => row.GetProperty("endpointId").GetString() == "other-source");
        endpoint.GetProperty("status").GetString().Should().Be("unattributed");
        endpoint.GetProperty("variants").GetArrayLength().Should().Be(1);
        endpoint
            .GetProperty("variants")[0]
            .GetProperty("attachment")
            .GetString()
            .Should()
            .Be("unattributed");
    }

    [TestCase("hash")]
    [TestCase("span")]
    [TestCase("ownership")]
    [TestCase("extra_source_artifact")]
    public async Task ChangedSourceAuthenticationFailsBeforeOutputIsCreated(string fault)
    {
        var manifestHash = await Package(fault);

        var run = () => Run(manifestHash);

        await run.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task IncompleteOutputIsPreservedAndASeparateRecoveryExportCanComplete()
    {
        var manifestHash = await Package();
        Directory.CreateDirectory(output);
        var partial = Path.Combine(output, "baseline.partial.jsonl");
        await File.WriteAllTextAsync(partial, "original incomplete artifact");
        var run = () => Run(manifestHash);

        await run.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(partial)).Should().Be("original incomplete artifact");
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        var recoveredOutput = Path.Combine(directory, "recovered");

        var recovered = await new PeopleAdjudicatedBaselineBatch().RunAsync(
            package,
            recoveredOutput,
            manifestHash
        );

        recovered.State.Should().Be("completed");
        Rows(recoveredOutput, recovered.BaselineFile).Should().HaveCount(2);
        File.Exists(Path.Combine(recoveredOutput, "manifest.json")).Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExactNameSelectorsWithNullMentionMetadataRemainSourceNamedComparisons(
        bool contextMissing
    )
    {
        var manifestHash = await Package(contextMissing ? "null_name_fallback" : "null_name");

        var report = await Run(manifestHash);

        var pairs = Rows(output, report.BaselineFile);
        pairs.Should().HaveCount(2);
        pairs.Should().OnlyContain(row => row.GetProperty("projectionSupported").GetBoolean());
        pairs
            .Should()
            .OnlyContain(row => row.GetProperty("comparison").ValueKind == JsonValueKind.Object);
        var endpoint = Rows(output, report.EndpointFile)
            .Single(row => row.GetProperty("endpointId").GetString() == "focus");
        endpoint.GetProperty("mentionKind").ValueKind.Should().Be(JsonValueKind.Null);
        endpoint
            .GetProperty("status")
            .GetString()
            .Should()
            .Be(contextMissing ? "name_only" : "projected");
        var selector = endpoint.GetProperty("selectors")[0];
        selector.GetProperty("kind").GetString().Should().Be("actor_name");
        selector
            .GetProperty("evidence")
            .GetProperty("quote")
            .GetString()
            .Should()
            .Be(contextMissing ? "Alice" : "Alice Vale");
        selector
            .GetProperty("status")
            .GetString()
            .Should()
            .Be(contextMissing ? "name_only" : "matched");
    }

    [TestCase("invalid_name_expression")]
    [TestCase("invalid_expression_null")]
    [TestCase("invalid_contact_name")]
    public async Task AllowingNullNameMetadataDoesNotLoosenOtherSelectorKinds(string fault)
    {
        var manifestHash = await Package(fault);

        var run = () => Run(manifestHash);

        await run.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    private Task<PeopleAdjudicatedBaselineReport> Run(string hash) =>
        new PeopleAdjudicatedBaselineBatch().RunAsync(package, output, hash);

    private async Task<string> Package(string? fault = null)
    {
        const string shared =
            "Recorded and transferred by Alice Vale aka Night Owl (shared@example.test)\nRecorded by Bob Reed (bob@example.test)\n";
        var texts = new[]
        {
            shared,
            fault == "unowned_contact" ? "Alice Vale contact: shared@example.test\n" : shared,
            "unknown taper\n",
        };
        var sources = texts.Select((text, index) => Source(index, text)).ToArray();
        var focus = Endpoint("focus", 0, "name", Selector(texts[0], "actor_name", "Alice Vale"));
        if (fault is "null_name" or "null_name_fallback")
        {
            focus["mention_kind"] = null;
            if (fault == "null_name_fallback")
            {
                focus["selectors"] = new JsonArray(Selector(texts[0], "actor_name", "Alice"));
            }
        }
        if (fault == "invalid_name_expression")
        {
            focus["mention_kind"] = "credit_expression";
        }
        if (fault == "invalid_expression_null")
        {
            focus["mention_kind"] = null;
            focus["selectors"] = new JsonArray(
                Selector(texts[0], "source_expression", "Alice Vale")
            );
        }
        if (fault == "invalid_contact_name")
        {
            focus["selectors"] = new JsonArray(
                Selector(texts[0], "source_contact_reference", "shared@example.test")
            );
        }
        if (fault == "multiple_actors")
        {
            focus["selectors"]!.AsArray().Add(Selector(texts[0], "actor_name", "Bob Reed"));
        }
        if (fault == "same_actor_selectors")
        {
            focus["selectors"]!.AsArray().Add(Selector(texts[0], "actor_name", "Night Owl"));
        }
        if (fault == "missing_actor_selector")
        {
            focus["selectors"]!.AsArray().Add(Selector(texts[0], "actor_name", "Recorded"));
        }
        if (fault == "span")
        {
            focus["selectors"]![0]!["span"]!["end"] = 1;
        }
        if (fault == "ownership")
        {
            focus["artist_uuid"] = "438c5908-e13d-44df-a51c-f5e8357b4e3d";
        }
        var endpoints = new[]
        {
            focus,
            Endpoint("alias", 0, "name", Selector(texts[0], "actor_name", "Night Owl")),
            Endpoint(
                "other-source",
                1,
                fault == "unowned_contact" ? null : "name",
                Selector(
                    texts[1],
                    fault == "unowned_contact" ? "source_contact_reference" : "actor_name",
                    fault == "unowned_contact" ? "shared@example.test" : "Alice Vale"
                )
            ),
            Endpoint(
                "kind-only",
                2,
                "unresolved_reference",
                Selector(texts[2], "source_expression", "unknown taper")
            ),
        };
        var questions = new[]
        {
            Question(
                "within-task",
                "within_recording_coreference",
                "focus",
                ["alias"],
                "identity-group"
            ),
            Question(
                "cross-task",
                "cross_recording_identity",
                "focus",
                ["other-source"],
                "identity-group"
            ),
            Question("kind-task", "actor_kind", "kind-only", [], "kind-group"),
        };
        questions[1]["candidate_scope"] = "reviewed_declared_pairs";
        var groups = new[]
        {
            new JsonObject { ["task_ids"] = new JsonArray("within-task", "cross-task") },
            new JsonObject { ["task_ids"] = new JsonArray("kind-task") },
        };
        var artifacts = new JsonArray();
        foreach (
            var (name, rows) in new (string, JsonObject[])[]
            {
                ("records", sources),
                ("endpoints", endpoints),
                ("questions", questions),
                ("grouping", groups),
            }
        )
        {
            var bytes = Encoding.UTF8.GetBytes(
                string.Concat(rows.Select(row => row.ToJsonString() + "\n"))
            );
            var path = "source/" + name + ".jsonl";
            await Write(path, bytes);
            artifacts.Add(
                new JsonObject
                {
                    ["path"] = path,
                    ["bytes"] = bytes.LongLength,
                    ["rows"] = rows.Length,
                    ["sha256"] = Hash(bytes),
                }
            );
        }
        // Separate target metadata is retained but neither the target file nor its labels
        // belongs to the source projection contract. The missing file must never be opened.
        artifacts.Add(
            new JsonObject
            {
                ["path"] = "targets/labels.jsonl",
                ["bytes"] = 17,
                ["rows"] = 1,
                ["sha256"] = new string('a', 64),
            }
        );
        if (fault == "extra_source_artifact")
        {
            artifacts.Add(
                new JsonObject
                {
                    ["path"] = "source/unconsumed.jsonl",
                    ["bytes"] = 0,
                    ["rows"] = 1,
                    ["sha256"] = new string('b', 64),
                }
            );
        }
        var manifest = new JsonObject
        {
            ["version"] = "people-adjudicated-training-inputs:1",
            ["state"] = "prepared_source_selectors_and_separate_targets",
            ["eligible_for_release_evaluation"] = false,
            ["identity_accuracy_established"] = false,
            ["policy_activation_permitted"] = false,
            ["accepted_data_writes"] = 0,
            ["artifacts"] = artifacts,
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        await Write("manifest.json", manifestBytes);
        if (fault == "hash")
        {
            await File.AppendAllTextAsync(Path.Combine(package, "source", "records.jsonl"), " ");
        }
        return Hash(manifestBytes);
    }

    private static JsonObject Source(int index, string text) =>
        new()
        {
            ["evidence_id"] = EvidenceId(index),
            ["artist"] = new JsonObject
            {
                ["uuid"] = ArtistUuid.ToString(),
                ["name"] = "Synthetic Artist",
            },
            ["artist_partition"] = "development",
            ["family_partition"] = "development",
            ["input_duplicate_family"] = "synthetic-family-" + index,
            ["known_recording_family"] = null,
            ["resource"] = new JsonObject
            {
                ["provider"] = "relisten_catalog_snapshot",
                ["resource_type"] = "source",
                ["resource_uuid"] = SourceUuids[index].ToString(),
            },
            ["raw_record"] = new JsonObject
            {
                ["kind"] = "source_sample",
                ["family"] = null,
                ["row"] = new JsonObject
                {
                    ["id"] = 100 + index,
                    ["uuid"] = SourceUuids[index].ToString(),
                    ["taper"] = text,
                },
            },
        };

    private static JsonObject Endpoint(
        string id,
        int source,
        string? mentionKind,
        JsonObject selector
    ) =>
        new()
        {
            ["endpoint_id"] = id,
            ["evidence_id"] = EvidenceId(source),
            ["artist_uuid"] = ArtistUuid.ToString(),
            ["family_group"] = "synthetic-family-" + source,
            ["known_recording_family"] = null,
            ["mention_kind"] = mentionKind,
            ["actor_reference_resolution"] = "not_projected",
            ["selectors"] = new JsonArray(selector),
        };

    private static JsonObject Selector(string raw, string kind, string quote)
    {
        var start = raw.IndexOf(quote, StringComparison.Ordinal);
        start
            .Should()
            .BeGreaterThanOrEqualTo(0, "synthetic selectors cite the original full field");
        return new()
        {
            ["kind"] = kind,
            ["field_path"] = "/raw_record/row/taper",
            ["field_sha256"] = Hash(Encoding.UTF8.GetBytes(raw)),
            ["span"] = new JsonObject
            {
                ["start"] = raw[..start].EnumerateRunes().Count(),
                ["end"] = raw[..(start + quote.Length)].EnumerateRunes().Count(),
                ["quote"] = quote,
                ["unit"] = "unicode_code_points",
            },
        };
    }

    private static JsonObject Question(
        string id,
        string kind,
        string focus,
        string[] candidates,
        string group
    ) =>
        new()
        {
            ["task_id"] = id,
            ["task_kind"] = kind,
            ["focus_endpoint_id"] = focus,
            ["candidate_endpoint_ids"] = new JsonArray(
                candidates.Select(value => JsonValue.Create(value)).ToArray()
            ),
            ["candidate_scope"] = "original_validated_candidate_pool",
            ["origin"] = "original_draft",
            ["split_group_ids"] = new JsonArray(group),
        };

    private async Task Write(string path, byte[] bytes)
    {
        var destination = Path.Combine(package, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, bytes);
    }

    private static JsonElement[] Rows(string directory, string path) =>
        File.ReadAllLines(Path.Combine(directory, path))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();

    private static string EvidenceId(int index) => "source_sample:" + SourceUuids[index].ToString();

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
