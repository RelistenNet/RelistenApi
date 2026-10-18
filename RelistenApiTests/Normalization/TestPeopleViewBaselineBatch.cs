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

/// <summary>Independent source views preserve complete bags and diagnostic-only uncertainty.</summary>
[TestFixture]
public sealed class TestPeopleViewBaselineBatch
{
    private static readonly Guid ArtistUuid = new("2d6802b1-70e5-4846-a809-f99f5f3eedda");
    private static readonly Guid[] SourceUuids =
    [
        new("fd2957bb-9484-4fe0-b301-5458c93972da"),
        new("db47a698-0223-46cb-8cb6-fc053c4a2b50"),
        new("6b603c4f-9df4-47c0-9290-a14588223919"),
    ];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private string directory = null!;
    private string package = null!;
    private string output = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "people-view-baseline-" + Guid.NewGuid().ToString("N")
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
    public async Task IndependentTwoByThreeFieldViewsProduceSixActualComparisonsWithoutMergingReferences()
    {
        var manifestHash = await Package();

        var report = await Run(manifestHash);

        report.State.Should().Be("completed");
        report.PolicyActivationPermitted.Should().BeFalse();
        report.PackageManifestSha256.Should().Be(manifestHash);
        report.FeatureOrder.Should().Equal(PeoplePairBaselineBatch.FeatureOrder);
        report.Counts.Tasks.Should().Be(2);
        report.Counts.Endpoints.Should().Be(3);
        report.Counts.Edges.Should().Be(1);
        report.Counts.Views.Should().Be(5);
        report.Counts.LeafPairs.Should().Be(6);
        report.Counts.CompleteBags.Should().Be(1);
        report.Counts.PartialBags.Should().Be(0);
        report.Counts.UnsupportedBags.Should().Be(0);
        var endpoints = EndpointRows(report);
        var left = endpoints["left"];
        var right = endpoints["right"];
        left.GetProperty("coverage").GetString().Should().Be("complete");
        right.GetProperty("coverage").GetString().Should().Be("complete");
        left.GetProperty("matchRecordsAgree").GetBoolean().Should().BeTrue();
        right.GetProperty("matchRecordsAgree").GetBoolean().Should().BeTrue();
        left.GetProperty("views").GetArrayLength().Should().Be(2);
        right.GetProperty("views").GetArrayLength().Should().Be(3);
        var leftTaper = View(left, "taper");
        var rightTaper = View(right, "taper");
        leftTaper
            .GetProperty("key")
            .GetProperty("referenceUuid")
            .GetGuid()
            .Should()
            .Be(rightTaper.GetProperty("key").GetProperty("referenceUuid").GetGuid());
        leftTaper
            .GetProperty("key")
            .GetProperty("evidenceId")
            .GetString()
            .Should()
            .Be(EvidenceId(0));
        rightTaper
            .GetProperty("key")
            .GetProperty("evidenceId")
            .GetString()
            .Should()
            .Be(EvidenceId(1));

        var bag = Rows(output, report.Bags.Path).Should().ContainSingle().Which;
        bag.GetProperty("complete").GetBoolean().Should().BeTrue();
        bag.GetProperty("leftViewCount").GetInt32().Should().Be(2);
        bag.GetProperty("rightViewCount").GetInt32().Should().Be(3);
        bag.GetProperty("expectedLeafPairs").GetInt32().Should().Be(6);
        var leaves = Rows(output, report.LeafPairs.Path);
        leaves.Should().HaveCount(6);
        var matcher = new IdentityCandidateMatcher();
        foreach (var leaf in leaves)
        {
            var leftView = left.GetProperty("views")
                .EnumerateArray()
                .Single(view =>
                    JsonElement.DeepEquals(view.GetProperty("key"), leaf.GetProperty("leftViewKey"))
                );
            var rightView = right
                .GetProperty("views")
                .EnumerateArray()
                .Single(view =>
                    JsonElement.DeepEquals(
                        view.GetProperty("key"),
                        leaf.GetProperty("rightViewKey")
                    )
                );
            var leftRecord = MatchRecord(leftView.GetProperty("projection").GetProperty("record"));
            var rightRecord = MatchRecord(
                rightView.GetProperty("projection").GetProperty("record")
            );
            var actual = matcher.ComparePeople(leftRecord, rightRecord);

            JsonElement
                .DeepEquals(
                    leaf.GetProperty("comparison"),
                    JsonSerializer.SerializeToElement(actual, Json)
                )
                .Should()
                .BeTrue("each leaf must preserve the public matcher's actual result");
            leaf.GetProperty("features")
                .EnumerateArray()
                .Select(value => value.GetDouble())
                .Should()
                .Equal(1, 1, 1, 1, 0);
            leaf.GetProperty("comparison")
                .GetProperty("candidateUuid")
                .ValueKind.Should()
                .Be(JsonValueKind.Null);
            leaf.GetProperty("edgeId")
                .GetString()
                .Should()
                .Be(bag.GetProperty("edgeId").GetString());
        }
        JsonSerializer
            .Deserialize<string[]>(bag.GetProperty("edgeId").GetString()!)
            .Should()
            .Equal("identity-task", "right");
        report.EndpointViews.Rows.Should().Be(3);
        report.LeafPairs.Rows.Should().Be(6);
        report
            .LeafPairs.Bytes.Should()
            .Be(new FileInfo(Path.Combine(output, report.LeafPairs.Path)).Length);
        report
            .LeafPairs.Sha256.Should()
            .Be(Hash(await File.ReadAllBytesAsync(Path.Combine(output, report.LeafPairs.Path))));
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
    }

    [Test]
    public async Task ACompleteBagCanRetainDifferentViewRecordsWithoutChoosingOne()
    {
        var hash = await Package("different_records");

        var report = await Run(hash);

        var left = EndpointRows(report)["left"];
        left.GetProperty("coverage").GetString().Should().Be("complete");
        left.GetProperty("matchRecordsAgree").GetBoolean().Should().BeFalse();
        left.GetProperty("views").GetArrayLength().Should().Be(2);
        report.Counts.CompleteBags.Should().Be(1);
        var leaves = Rows(output, report.LeafPairs.Path);
        leaves.Should().HaveCount(6);
        leaves
            .Select(row => row.GetProperty("features").GetRawText())
            .Distinct(StringComparer.Ordinal)
            .Should()
            .HaveCount(2);
    }

    [Test]
    public async Task OneNamedFallbackUsesExactSelectorCoordinatesWithoutFabricatingAnActorReference()
    {
        var hash = await Package("named_fallback");

        var report = await Run(hash);

        var left = EndpointRows(report)["left"];
        left.GetProperty("coverage").GetString().Should().Be("complete");
        var view = left.GetProperty("views").EnumerateArray().Should().ContainSingle().Which;
        var key = view.GetProperty("key");
        key.GetProperty("kind").GetString().Should().Be("source_selector");
        key.GetProperty("referenceUuid").ValueKind.Should().Be(JsonValueKind.Null);
        key.GetProperty("anchor").GetProperty("quote").GetString().Should().Be("Alice");
        key.GetProperty("fieldSha256")
            .GetString()
            .Should()
            .Be(Hash(Encoding.UTF8.GetBytes("Alice Vale aka Night Owl (shared@example.test)")));
        view.GetProperty("facts").ValueKind.Should().Be(JsonValueKind.Null);
        report.Counts.LeafPairs.Should().Be(3);
        report.Counts.CompleteBags.Should().Be(1);
    }

    [Test]
    public async Task MissingDescriptionPreservesClearLeafDiagnosticsButCannotBecomeACompleteBag()
    {
        var hash = await Package("partial");

        var report = await Run(hash);

        var left = EndpointRows(report)["left"];
        left.GetProperty("coverage").GetString().Should().Be("partial");
        left.GetProperty("selectors").GetArrayLength().Should().Be(3);
        left.GetProperty("views").GetArrayLength().Should().Be(2);
        report.Counts.LeafPairs.Should().Be(6);
        report.Counts.CompleteBags.Should().Be(0);
        report.Counts.PartialBags.Should().Be(1);
        report.Counts.UnsupportedBags.Should().Be(0);
        var bag = Rows(output, report.Bags.Path).Should().ContainSingle().Which;
        bag.GetProperty("complete").GetBoolean().Should().BeFalse();
        Rows(output, report.LeafPairs.Path).Should().HaveCount(6);
    }

    [Test]
    public async Task OneAmbiguousUrlSelectorRetainsBothHypothesesAndProducesNoArbitraryLeaf()
    {
        var hash = await Package("ambiguous");

        var report = await Run(hash);

        var left = EndpointRows(report)["left"];
        left.GetProperty("coverage").GetString().Should().Be("ambiguous");
        left.GetProperty("variants").GetArrayLength().Should().Be(2);
        left.GetProperty("views").GetArrayLength().Should().Be(0);
        left.GetProperty("matchRecordsAgree").ValueKind.Should().Be(JsonValueKind.Null);
        report.Counts.LeafPairs.Should().Be(0);
        report.Counts.CompleteBags.Should().Be(0);
        report.Counts.PartialBags.Should().Be(0);
        report.Counts.UnsupportedBags.Should().Be(1);
        Rows(output, report.LeafPairs.Path).Should().BeEmpty();
        Rows(output, report.Bags.Path)
            .Should()
            .ContainSingle()
            .Which.GetProperty("complete")
            .GetBoolean()
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task SameFieldBaseAliasAndJointRolesShareOneViewWithBothSelectorMemberships()
    {
        var hash = await Package("same_field_alias");

        var report = await Run(hash);

        var left = EndpointRows(report)["left"];
        left.GetProperty("coverage").GetString().Should().Be("complete");
        left.GetProperty("variants").GetArrayLength().Should().Be(2);
        var view = left.GetProperty("views").EnumerateArray().Should().ContainSingle().Which;
        view.GetProperty("selectorIndexes")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .Should()
            .Equal(0, 1);
        view.GetProperty("key").GetProperty("kind").GetString().Should().Be("source_actor");
        view.GetProperty("key").GetProperty("field").GetString().Should().Be("taper");
        report.Counts.LeafPairs.Should().Be(3);
        report.Counts.CompleteBags.Should().Be(1);
    }

    [TestCase("archive.org")]
    [TestCase("local")]
    [TestCase("phish.in")]
    public async Task NativeFieldsProjectTheirCompleteOriginalValueRatherThanThePriorCatalogName(
        string provider
    )
    {
        var hash = await NativePackage(NativeOrigin(provider));

        var report = await Run(hash);

        report.State.Should().Be("completed");
        report.PackageManifestSha256.Should().Be(hash);
        var left = EndpointRows(report)["left"];
        left.GetProperty("coverage").GetString().Should().Be("complete");
        var view = left.GetProperty("views").EnumerateArray().Should().ContainSingle().Which;
        var record = MatchRecord(view.GetProperty("projection").GetProperty("record"));
        record.Names.Select(name => name.Wording).Should().Equal("Native Recorder");
        record
            .ActorContext!.Contacts.Should()
            .ContainSingle(contact => contact.LookupKey == "native@example.test");
        report.Counts.LeafPairs.Should().Be(1);
        report.Counts.CompleteBags.Should().Be(1);
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
    }

    [TestCase("missing")]
    [TestCase("null")]
    [TestCase("wrong_shape")]
    [TestCase("missing_kind")]
    [TestCase("wrong_kind")]
    [TestCase("unknown_provider")]
    [TestCase("invalid_evidence_hash")]
    [TestCase("invalid_resource_hash")]
    [TestCase("null_hash")]
    public async Task NativeFieldsWithoutSupportedProvenanceCannotCompleteAnExport(string defect)
    {
        JsonNode? origin = NativeOrigin("archive.org");
        switch (defect)
        {
            case "missing":
            case "null":
                origin = null;
                break;
            case "wrong_shape":
                origin = JsonValue.Create("unstructured provenance");
                break;
            case "missing_kind":
                origin.AsObject().Remove("kind");
                break;
            case "wrong_kind":
                origin["kind"] = "importer_execution";
                break;
            case "unknown_provider":
                origin["provider"] = "unverified-provider";
                break;
            case "invalid_evidence_hash":
                origin["evidence_sha256"] = new string('z', 64);
                break;
            case "invalid_resource_hash":
                origin["resource_key_sha256"] = "short";
                break;
            case "null_hash":
                origin["evidence_sha256"] = null;
                break;
        }
        var hash = await NativePackage(origin, includeNullOrigin: defect == "null");
        var run = () => Run(hash);

        await run.Should().ThrowAsync<InvalidDataException>();

        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        Directory.Exists(output).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SeparateTargetsCannotChangeViewPairsOrThePreservedOldComparator(
        bool nativeSource
    )
    {
        var hash = nativeSource
            ? await NativePackage(NativeOrigin("archive.org"))
            : await Package();
        var first = await Run(hash);
        var leaves = await File.ReadAllBytesAsync(Path.Combine(output, first.LeafPairs.Path));
        var baseline = await File.ReadAllBytesAsync(Path.Combine(output, first.Baseline.Path));
        var target = Path.Combine(package, "targets", "labels.jsonl");
        File.Exists(target).Should().BeFalse();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "unparseable synthetic target bytes\n");
        var secondOutput = Path.Combine(directory, "changed-target");

        var second = await new PeopleViewBaselineBatch().RunAsync(package, secondOutput, hash);
        var comparatorOutput = Path.Combine(directory, "old-comparator");
        var old = await new PeopleAdjudicatedBaselineBatch().RunAsync(
            package,
            comparatorOutput,
            hash
        );

        (await File.ReadAllBytesAsync(Path.Combine(secondOutput, second.LeafPairs.Path)))
            .Should()
            .Equal(leaves);
        (await File.ReadAllBytesAsync(Path.Combine(secondOutput, second.Baseline.Path)))
            .Should()
            .Equal(baseline);
        (await File.ReadAllBytesAsync(Path.Combine(comparatorOutput, old.BaselineFile)))
            .Should()
            .Equal(baseline);
        first.ComparatorManifest.Path.Should().Be("comparator/manifest.json");
        Rows(output, first.Baseline.Path)
            .Should()
            .ContainSingle()
            .Which.GetProperty("comparison")
            .ValueKind.Should()
            .Be(nativeSource ? JsonValueKind.Object : JsonValueKind.Null);
    }

    [Test]
    public async Task LeafCountOverflowCannotPromoteTheCompletedComparatorToTopLevelCompletion()
    {
        const string actor = "Recorded by Alice Vale (shared@example.test)";
        var fields = Enumerable.Range(0, 100).ToDictionary(index => "credit_" + index, _ => actor);
        var records = new[] { Source(0, fields), Source(1, fields) };
        JsonArray Selectors() =>
            new(
                fields
                    .Select(pair =>
                        (JsonNode)Selector(
                            pair.Key,
                            pair.Value,
                            "source_contact_reference",
                            "shared@example.test"
                        )
                    )
                    .ToArray()
            );
        var candidates = Enumerable.Range(0, 101).Select(index => "right-" + index).ToArray();
        var endpoints = candidates
            .Select(id => Endpoint(id, 1, null, Selectors()))
            .Prepend(Endpoint("left", 0, null, Selectors()))
            .ToArray();
        var hash = await WritePackage(
            records,
            endpoints,
            [Question("overflow-task", "cross_recording_identity", "left", candidates)],
            [new JsonObject { ["task_ids"] = new JsonArray("overflow-task") }]
        );
        var run = () => Run(hash);

        await run.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*exceeds 1,000,000 leaf pairs*");

        // 100 independently scoped fields on either side yield 10,000 leaves per edge.
        // The 101 edges exceed the real count bound; no million-row leaf artifact is needed.
        var comparator = Path.Combine(output, "comparator", "manifest.json");
        File.Exists(comparator).Should().BeTrue();
        using var completedComparator = JsonDocument.Parse(
            await File.ReadAllBytesAsync(comparator)
        );
        completedComparator.RootElement.GetProperty("state").GetString().Should().Be("completed");
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "view-pairs.jsonl")).Should().BeFalse();
    }

    [Test]
    public async Task ExistingIncompleteOutputIsNotOverwrittenAndANewDirectoryCanRecover()
    {
        var hash = await Package();
        Directory.CreateDirectory(output);
        var partial = Path.Combine(output, "view-pairs.partial.jsonl");
        await File.WriteAllTextAsync(partial, "retained partial output");
        var run = () => Run(hash);

        await run.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(partial)).Should().Be("retained partial output");
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        var recoveredOutput = Path.Combine(directory, "recovered");

        var recovered = await new PeopleViewBaselineBatch().RunAsync(
            package,
            recoveredOutput,
            hash
        );

        recovered.State.Should().Be("completed");
        File.Exists(Path.Combine(recoveredOutput, "manifest.json")).Should().BeTrue();
    }

    private Task<PeopleViewBaselineReport> Run(string hash) =>
        new PeopleViewBaselineBatch().RunAsync(package, output, hash);

    private Dictionary<string, JsonElement> EndpointRows(PeopleViewBaselineReport report) =>
        Rows(output, report.EndpointViews.Path)
            .ToDictionary(row => row.GetProperty("endpointId").GetString()!);

    [Test]
    public async Task RoleFreeAliasEvidenceSurvivesThePublicViewExportWithoutRecordingFacts()
    {
        const string roster = "The band:\nAlice Vale AKA Night Owl - guitar";
        const string other = "Night Owl";
        var hash = await WritePackage(
            [Source(0, new() { ["description"] = roster }), Source(1, new() { ["taper"] = other })],
            [
                Endpoint(
                    "left",
                    0,
                    null,
                    new(
                        Selector("description", roster, "actor_name", "Alice Vale"),
                        Selector("description", roster, "actor_name", "Night Owl")
                    )
                ),
                Endpoint("right", 1, null, new(Selector("taper", other, "actor_name", other))),
            ],
            [Question("alias-task", "cross_recording_identity", "left", ["right"])],
            [new JsonObject { ["task_ids"] = new JsonArray("alias-task") }]
        );

        var report = await Run(hash);

        report.Counts.CompleteBags.Should().Be(1);
        report.Counts.LeafPairs.Should().Be(1);
        var left = EndpointRows(report)["left"];
        var view = left.GetProperty("views").EnumerateArray().Should().ContainSingle().Which;
        view.GetProperty("selectorIndexes")
            .EnumerateArray()
            .Select(x => x.GetInt32())
            .Should()
            .Equal(0, 1);
        view.GetProperty("facts").ValueKind.Should().Be(JsonValueKind.Null);
        var assertion = view.GetProperty("sourceAssertion");
        assertion
            .GetProperty("context")
            .GetProperty("heading")
            .GetProperty("quote")
            .GetString()
            .Should()
            .Be("The band:");
        assertion
            .GetProperty("nameEvidence")
            .GetProperty("quote")
            .GetString()
            .Should()
            .Be("Alice Vale");
        var record = MatchRecord(view.GetProperty("projection").GetProperty("record"));
        record.Names.Select(name => name.Wording).Should().Equal("Alice Vale", "Night Owl");
        record.Kind.Should().BeNull();
        record.Uuid.Should().BeNull();
        record.ActorContext.Should().BeNull();
        report.PolicyActivationPermitted.Should().BeFalse();
    }

    private async Task<string> Package(string scenario = "complete")
    {
        const string actor = "Alice Vale aka Night Owl (shared@example.test)";
        var leftFields = new Dictionary<string, string>
        {
            ["taper"] = actor,
            ["transferrer"] = actor,
        };
        var rightFields = new Dictionary<string, string>
        {
            ["taper"] = actor,
            ["transferrer"] = actor,
            ["description"] = "Recorded by " + actor,
        };
        var leftSelectors = new JsonArray(
            Selector("taper", actor, "source_contact_reference", "shared@example.test"),
            Selector("transferrer", actor, "source_contact_reference", "shared@example.test")
        );
        string? leftKind = null;
        if (scenario == "different_records")
        {
            const string other = "Bob Reed (bob@example.test)";
            leftFields["transferrer"] = other;
            leftSelectors[1] = Selector(
                "transferrer",
                other,
                "source_contact_reference",
                "bob@example.test"
            );
        }
        if (scenario == "named_fallback")
        {
            leftKind = "name";
            leftSelectors = new(Selector("taper", actor, "actor_name", "Alice"));
        }
        if (scenario == "partial")
        {
            const string missing = "https://example.test/no-supported-actor";
            leftFields["description"] = missing;
            leftSelectors.Add(Selector("description", missing, "source_url_reference", missing));
        }
        if (scenario == "ambiguous")
        {
            const string raw =
                "Recorded by Alice Vale and Bob Reed; profile: https://github.com/shared";
            leftFields = new() { ["description"] = raw };
            leftSelectors = new(
                Selector("description", raw, "source_url_reference", "https://github.com/shared")
            );
        }
        if (scenario == "same_field_alias")
        {
            var raw = "Recorded and transferred by " + actor;
            leftFields = new() { ["taper"] = raw };
            leftKind = "name";
            leftSelectors = new(
                Selector("taper", raw, "actor_name", "Alice Vale"),
                Selector("taper", raw, "actor_name", "Night Owl")
            );
        }
        var rightSelectors = new JsonArray(
            rightFields
                .Select(pair =>
                    (JsonNode)Selector(
                        pair.Key,
                        pair.Value,
                        "source_contact_reference",
                        "shared@example.test"
                    )
                )
                .ToArray()
        );
        return await WritePackage(
            [
                Source(0, leftFields),
                Source(1, rightFields),
                Source(2, new() { ["taper"] = "unknown taper" }),
            ],
            [
                Endpoint("left", 0, leftKind, leftSelectors),
                Endpoint("right", 1, null, rightSelectors),
                Endpoint(
                    "kind-only",
                    2,
                    "unresolved_reference",
                    new(Selector("taper", "unknown taper", "source_expression", "unknown taper"))
                ),
            ],
            [
                Question("identity-task", "cross_recording_identity", "left", ["right"]),
                Question("kind-task", "actor_kind", "kind-only", []),
            ],
            [
                new JsonObject { ["task_ids"] = new JsonArray("identity-task") },
                new JsonObject { ["task_ids"] = new JsonArray("kind-task") },
            ]
        );
    }

    private async Task<string> WritePackage(
        JsonObject[] records,
        JsonObject[] endpoints,
        JsonObject[] questions,
        JsonObject[] groups
    )
    {
        var artifacts = new JsonArray();
        foreach (
            var (name, rows) in new (string, JsonObject[])[]
            {
                ("records", records),
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
        artifacts.Add(
            new JsonObject
            {
                ["path"] = "targets/labels.jsonl",
                ["bytes"] = 17,
                ["rows"] = 1,
                ["sha256"] = new string('a', 64),
            }
        );
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
        return Hash(manifestBytes);
    }

    private Task<string> NativePackage(JsonNode? origin, bool includeNullOrigin = false)
    {
        const string native = "Native Recorder (native@example.test)";
        var source = Source(0, new() { ["taper"] = "Prior Catalog Recorder" });
        source["resource"]!["provider"] = "relisten_retained_provider_field";
        source["raw_record"]!["row"]!["taper"] = native;
        if (origin != null || includeNullOrigin)
        {
            source["provider_origin"] = origin;
        }
        return WritePackage(
            [source, Source(1, new() { ["taper"] = "Native Recorder" })],
            [
                Endpoint(
                    "left",
                    0,
                    null,
                    new(Selector("taper", native, "actor_name", "Native Recorder"))
                ),
                Endpoint(
                    "right",
                    1,
                    null,
                    new(Selector("taper", "Native Recorder", "actor_name", "Native Recorder"))
                ),
            ],
            [Question("native-task", "cross_recording_identity", "left", ["right"])],
            [new JsonObject { ["task_ids"] = new JsonArray("native-task") }]
        );
    }

    private static JsonObject NativeOrigin(string provider) =>
        new()
        {
            ["provider"] = provider,
            ["kind"] = "retained_provider_input",
            ["evidence_sha256"] = Hash(Encoding.UTF8.GetBytes("operator-retained native response")),
            ["resource_key_sha256"] = Hash(Encoding.UTF8.GetBytes("native resource")),
        };

    private static JsonObject Source(int index, Dictionary<string, string> fields)
    {
        var raw = new JsonObject { ["id"] = 100 + index, ["uuid"] = SourceUuids[index].ToString() };
        foreach (var (name, value) in fields)
        {
            raw[name] = value;
        }
        return new()
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
                ["row"] = raw,
            },
        };
    }

    private static JsonObject Endpoint(string id, int source, string? kind, JsonArray selectors) =>
        new()
        {
            ["endpoint_id"] = id,
            ["evidence_id"] = EvidenceId(source),
            ["artist_uuid"] = ArtistUuid.ToString(),
            ["family_group"] = "synthetic-family-" + source,
            ["known_recording_family"] = null,
            ["mention_kind"] = kind,
            ["actor_reference_resolution"] = "not_projected",
            ["selectors"] = selectors,
        };

    private static JsonObject Selector(string field, string raw, string kind, string quote)
    {
        var start = raw.IndexOf(quote, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "synthetic selectors cite original text");
        return new()
        {
            ["kind"] = kind,
            ["field_path"] = "/raw_record/row/" + field.Replace("~", "~0").Replace("/", "~1"),
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

    private static JsonObject Question(string id, string kind, string focus, string[] candidates) =>
        new()
        {
            ["task_id"] = id,
            ["task_kind"] = kind,
            ["focus_endpoint_id"] = focus,
            ["candidate_endpoint_ids"] = new JsonArray(
                candidates.Select(value => JsonValue.Create(value)).ToArray()
            ),
            ["candidate_scope"] = "reviewed_declared_pairs",
            ["origin"] = "original_draft",
            ["split_group_ids"] = new JsonArray(id + "-group"),
        };

    private async Task Write(string path, byte[] bytes)
    {
        var destination = Path.Combine(package, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, bytes);
    }

    private static JsonElement View(JsonElement endpoint, string field) =>
        endpoint
            .GetProperty("views")
            .EnumerateArray()
            .Single(view => view.GetProperty("key").GetProperty("field").GetString() == field);

    private static PersonMatchRecord MatchRecord(JsonElement row)
    {
        var uuid = row.GetProperty("uuid");
        PersonActorMatchContext? context =
            row.TryGetProperty("actorContext", out var actorContext)
            && actorContext.ValueKind != JsonValueKind.Null
                ? actorContext.Deserialize<PersonActorMatchContext>(Json)
                : null;
        return new(
            uuid.ValueKind == JsonValueKind.Null ? null : uuid.GetGuid(),
            row.GetProperty("kind").GetString(),
            row.GetProperty("names").Deserialize<IdentityMatchingName[]>(Json)!,
            row.GetProperty("externalIdentities").Deserialize<MatchingExternalIdentity[]>(Json)!,
            row.GetProperty("artistUuids")
                .EnumerateArray()
                .Select(value => value.GetGuid())
                .ToHashSet(),
            context
        );
    }

    private static JsonElement[] Rows(string root, string path) =>
        File.ReadAllLines(Path.Combine(root, path))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();

    private static string EvidenceId(int index) => "source_sample:" + SourceUuids[index].ToString();

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
