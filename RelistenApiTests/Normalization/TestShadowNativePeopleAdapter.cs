using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestShadowNativePeopleAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Artist = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Source = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Show = Guid.Parse("20000000-0000-0000-0000-000000000003");

    [Test]
    public void ArchiveArrayAndFileCreditsRetainNativeCoordinatesAndOptionalCatalogHomes()
    {
        var revision = Input(
            new("archive.org", "item_metadata", "native-item"),
            "archive-item-response-v1",
            """{"metadata":{"identifier":"native-item","taper":["Zoë Example (zoe@example.test)","Pat"]},"files":[{"name":"song.mp3","transferer":"File Lee"}]}"""
        );
        var input = HttpInput(revision);
        var results = new ShadowNativePeopleAdapter().Process(input).ToArray();
        var people = People(results);
        var credits = Credits(results).Credits;

        people
            .People.Select(person => person.Name)
            .Should()
            .Equal("Zoë Example", "Pat", "File Lee");
        credits
            .Select(credit => credit.Evidence.JsonPointer)
            .Should()
            .Equal("/metadata/taper/0", "/metadata/taper/1", "/files/0/transferer");
        credits
            .Should()
            .OnlyContain(credit =>
                credit.InputRevisionUuid == revision.Uuid
                && credit.CatalogSourceUuid == Source
                && credit.CatalogShowUuid == Show
                && !credit.AutomaticAcceptanceAuthorized
                && credit.CatalogTrackUuid == null
            );
        revision.Uuid.Should().NotBe(Source);
        credits.Take(2).Should().OnlyContain(credit => credit.FileOrigin == null);
        credits[2].FileOrigin!.Evidence.JsonPointer.Should().Be("/files/0");
        credits[2].RecordingOrigin.Evidence.JsonPointer.Should().Be("/metadata");
        people
            .People[0]
            .Projection.Record.ActorContext!.Contacts.Should()
            .ContainSingle()
            .Which.LookupKey.Should()
            .Be("zoe@example.test");
        results.Should().OnlyContain(result => result.Outcome == "proposed");
        results
            .Single(result => result.Domain == "people")
            .EntityKeys.Should()
            .Contain("source:" + Source.ToString("D"));
        using var document = NormalizationRetainedDocument.Open(revision);
        foreach (
            var evidence in people
                .People.SelectMany(person => person.Evidence)
                .Concat(credits.Select(credit => credit.Evidence))
        )
        {
            document.ValidateEvidence(evidence);
        }
    }

    [Test]
    public void NativeActorsRemainInspectableWithoutAnAuthenticatedCatalogBinding()
    {
        var revision = Input(
            new("archive.org", "item_metadata", "unbound-item"),
            "archive-item-response-v1",
            """{"metadata":{"identifier":"unbound-item","taper":"Alex Example"},"files":[]}"""
        );
        var input = PlainInput(revision);

        var results = new ShadowNativePeopleAdapter().Process(input).ToArray();

        var actor = People(results).People.Should().ContainSingle().Which;
        actor.Name.Should().Be("Alex Example");
        actor.InputRevisionUuid.Should().Be(revision.Uuid);
        actor.CatalogSourceUuid.Should().BeNull();
        actor.CatalogShowUuid.Should().BeNull();
        actor.CatalogBindingReason.Should().Be("no_retained_catalog_binding");
        results
            .SelectMany(result => result.EntityKeys)
            .Should()
            .NotContain("source:" + revision.Uuid.ToString("D"));
        Credits(results).Credits.Should().ContainSingle().Which.CatalogSourceUuid.Should().BeNull();
    }

    [TestCase("phishin-show-response-v1", "/data/taper_notes")]
    [TestCase("phishin-show-object-v1", "/taper_notes")]
    public void PhishRepresentationsPreserveTheirActualEnvelopeCoordinates(
        string version,
        string pointer
    )
    {
        const string show =
            """{"id":11,"date":"2020-01-02","taper_notes":"Taped by Native Pat","tracks":[]}""";
        var revision = Input(
            new("phish.in", "show", "11"),
            version,
            version == "phishin-show-response-v1"
                ? "{\"success\":true,\"data\":" + show + "}"
                : show
        );

        var credit = Credits(new ShadowNativePeopleAdapter().Process(PlainInput(revision)))
            .Credits.Should()
            .ContainSingle()
            .Which;

        credit.Name.Should().Be("Native Pat");
        credit.Evidence.JsonPointer.Should().Be(pointer);
        credit.Evidence.InputRevisionUuid.Should().Be(revision.Uuid);
        using var document = NormalizationRetainedDocument.Open(revision);
        document.ValidateEvidence(credit.Evidence);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LocalUsesOnlyItsExactRootAccountAndMissingRootLeavesDirectoryCreditsVisible(
        bool missingRoot
    )
    {
        var (root, input) = LocalInput();
        var shared = new ShadowProviderObservations();
        var adapter = new ShadowNativePeopleAdapter(shared);
        if (!missingRoot)
        {
            adapter.Observe(root);
            shared.Observe(root);
        }

        var results = adapter.Process(input).ToArray();
        var credits = Credits(results);

        credits
            .Credits.Select(credit => credit.Name)
            .Should()
            .Contain("Directory Sam")
            .And.NotContain("Adjacent Bob");
        var directoryCredit = credits.Credits.Single(credit => credit.Name == "Directory Sam");
        directoryCredit.Evidence.JsonPointer.Should().Be("/txts/0");
        directoryCredit.InputRevisionUuid.Should().Be(input.Uuid);
        if (missingRoot)
        {
            credits.Credits.Should().ContainSingle();
            credits
                .InputIssues.Should()
                .Contain(issue => issue.Code == "missing_retained_local_root");
            directoryCredit.CatalogSourceUuid.Should().BeNull();
        }
        else
        {
            var rootCredit = credits.Credits.Single(credit => credit.Name == "Root Alice");
            rootCredit
                .InputRevisionUuid.Should()
                .Be(root.GetProperty("row").GetProperty("uuid").GetGuid());
            rootCredit.RecordingOrigin.Scope.Should().Be(ProviderObservationScope.CatalogMember);
            rootCredit.Evidence.JsonPointer.Should().Be("/data/2020-01-02/0/taper_notes");
            rootCredit.CatalogSourceUuid.Should().Be(Source);
            directoryCredit.CatalogSourceUuid.Should().Be(Source);
            var rootRevision = root.GetProperty("input")
                .Deserialize<NormalizationInputRevision>(Json)!;
            using var document = NormalizationRetainedDocument.Open(rootRevision);
            document.ValidateEvidence(rootCredit.Evidence);
        }
    }

    [Test]
    public void UnknownFileCreditRetainsFileScopeWithoutBecomingARecordingWideAssertion()
    {
        var revision = Input(
            new("archive.org", "item_metadata", "native-item"),
            "archive-item-response-v1",
            """{"metadata":{"identifier":"native-item","taper":"Known Taper"},"files":[{"name":"song.mp3","taper":"unknown"}]}"""
        );

        var result = Credits(new ShadowNativePeopleAdapter().Process(PlainInput(revision)));

        result.Credits.Should().ContainSingle().Which.FileOrigin.Should().BeNull();
        var issue = result.Unresolved.Should().ContainSingle().Which;
        issue.FileOrigin!.Evidence.JsonPointer.Should().Be("/files/0");
        issue.Evidence.JsonPointer.Should().Be("/files/0/taper");
        issue.Evidence.InputRevisionUuid.Should().Be(revision.Uuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SelectedRootDecodeIssuesStayCitedAndAdjacentAccountIssuesStaySeparate(
        bool selectedMalformed
    )
    {
        var (root, directory) = LocalInput(
            selectedNotes: selectedMalformed ? 42 : null,
            adjacentNotes: 43
        );
        var adapter = new ShadowNativePeopleAdapter();
        adapter.Observe(root);

        var proposal = People(adapter.Process(directory));

        proposal.People.Select(person => person.Name).Should().Contain("Directory Sam");
        if (selectedMalformed)
        {
            var issue = proposal.InputIssues.Should().ContainSingle().Which;
            issue.Code.Should().Be("expected_text");
            issue.Evidence!.JsonPointer.Should().Be("/data/2020-01-02/0/taper_notes");
            var revision = root.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)!;
            issue.Evidence.InputRevisionUuid.Should().Be(revision.Uuid);
            using var document = NormalizationRetainedDocument.Open(revision);
            document.ValidateEvidence(issue.Evidence);
        }
        else
        {
            proposal.InputIssues.Should().BeEmpty();
        }
    }

    [Test]
    public void MalformedSupportingRootDoesNotHideUnrelatedNativeActorsAndFreshReplayRecovers()
    {
        var (root, directory) = LocalInput();
        var broken = JsonNode.Parse(root.GetRawText())!;
        broken["input"]!["sha256"] = new string('0', 64);
        var adapter = new ShadowNativePeopleAdapter();
        adapter.Observe(JsonSerializer.SerializeToElement(broken));

        var partial = Credits(adapter.Process(directory));
        partial.Credits.Should().ContainSingle().Which.Name.Should().Be("Directory Sam");
        partial.InputIssues.Should().Contain(issue => issue.Code == "invalid_retained_local_root");
        var archive = Input(
            new("archive.org", "item_metadata", "independent"),
            "archive-item-response-v1",
            """{"metadata":{"identifier":"independent","taper":"Independent Pat"},"files":[]}"""
        );
        People(adapter.Process(PlainInput(archive)))
            .People.Should()
            .ContainSingle()
            .Which.Name.Should()
            .Be("Independent Pat");

        var replay = new ShadowNativePeopleAdapter();
        replay.Observe(root);
        Credits(replay.Process(directory))
            .Credits.Select(credit => credit.Name)
            .Should()
            .Contain("Root Alice");
    }

    [TestCase("hash")]
    [TestCase("json")]
    [TestCase("version")]
    public void MalformedOrUnsupportedCapturesProduceExplicitFailedDomainResults(string fault)
    {
        var revision = Input(
            new("archive.org", "item_metadata", "native-item"),
            fault == "version" ? "unsupported-version" : "archive-item-response-v1",
            fault == "json"
                ? "{"
                : """{"metadata":{"identifier":"native-item","taper":"Native Pat"},"files":[]}""",
            sha256: fault == "hash" ? new string('0', 64) : null
        );

        var results = new ShadowNativePeopleAdapter().Process(PlainInput(revision)).ToArray();

        results
            .Select(result => result.Domain)
            .Should()
            .BeEquivalentTo(new[] { "people", "credits" });
        results
            .Should()
            .OnlyContain(result => result.Outcome == "failed" && result.Proposal == null);
        results
            .Should()
            .OnlyContain(result =>
                result.Reason
                == (
                    fault == "version"
                        ? "unsupported_provider_capture"
                        : "invalid_retained_provider_input"
                )
            );
    }

    [Test]
    public void SupportedNonRecordingRepresentationsDoNotTurnVenueOrRootAccountsIntoRecordingCredits()
    {
        var venue = Input(
            new("phish.in", "catalog", "venues"),
            "raw-http:1",
            """{"success":true,"data":[{"id":1,"name":"Taped by Venue Name"}]}"""
        );
        var (root, _) = LocalInput();
        var rootRevision = root.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)!;
        foreach (var input in new[] { PlainInput(venue), PlainInput(rootRevision) })
        {
            var results = new ShadowNativePeopleAdapter().Process(input).ToArray();
            results.Should().OnlyContain(result => result.Outcome == "not_applicable");
            People(results).People.Should().BeEmpty();
            Credits(results).Credits.Should().BeEmpty();
        }
    }

    private static ShadowNativePeopleProposal People(IEnumerable<ShadowCatalogResult> results) =>
        (ShadowNativePeopleProposal)results.Single(result => result.Domain == "people").Proposal!;

    private static ShadowNativeCreditsProposal Credits(IEnumerable<ShadowCatalogResult> results) =>
        (ShadowNativeCreditsProposal)results.Single(result => result.Domain == "credits").Proposal!;

    private static NormalizationInputRevision Input(
        NormalizationResourceKey resource,
        string version,
        string json,
        string? sha256 = null
    )
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = bytes,
            Sha256 = sha256 ?? Hash(bytes),
            CaptureVersion = version,
            MediaType = "application/json",
            CapturedAt = DateTime.UtcNow,
        };
    }

    private static ShadowCatalogInput PlainInput(NormalizationInputRevision revision) =>
        Project(
            new
            {
                kind = "provider_input",
                provider = revision.Resource.Provider,
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = 1,
                    artist_uuid = Artist,
                },
                input = revision,
            }
        );

    private static ShadowCatalogInput HttpInput(NormalizationInputRevision revision)
    {
        var artifact = new RetainedHttpArtifact(
            "retained-artifact.json",
            revision.Payload.LongLength,
            revision.Sha256
        );
        var binding = new RetainedHttpCatalogBinding(
            "bound",
            null,
            Source,
            Source,
            Show,
            null,
            artifact,
            "sources.jsonl.gz",
            2
        );
        var provenance = new RetainedHttpProviderProvenance(
            revision.Uuid,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "https://archive.org/metadata/native-item",
            200,
            artifact,
            artifact,
            artifact,
            0,
            artifact,
            binding
        );
        return Project(
            new
            {
                kind = "provider_input",
                provider = revision.Resource.Provider,
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = 1,
                    artist_uuid = Artist,
                },
                input = revision,
                retained_http = provenance,
                catalog_binding = binding,
                source_context = new
                {
                    id = 10,
                    uuid = Source,
                    artist_id = 1,
                    show_id = 20,
                    upstream_identifier = revision.Resource.ProviderKey,
                },
                show_context = new
                {
                    id = 20,
                    uuid = Show,
                    artist_id = 1,
                },
            }
        );
    }

    private static (JsonElement Root, ShadowCatalogInput Directory) LocalInput(
        object? selectedNotes = null,
        object? adjacentNotes = null
    )
    {
        var root = Input(
            new("local", "metadata_root", "trey"),
            "local-http-json-v1",
            JsonSerializer.Serialize(
                new
                {
                    data = new Dictionary<string, object>
                    {
                        ["2020-01-02"] = new object[]
                        {
                            new
                            {
                                dir = "target",
                                taper_notes = selectedNotes ?? "Taped by Root Alice",
                            },
                            new
                            {
                                dir = "neighbor",
                                taper_notes = adjacentNotes ?? "Taped by Adjacent Bob",
                            },
                        },
                    },
                }
            )
        );
        var directory = Input(
            NormalizationProviderResources.LocalDirectory("trey", "target"),
            "local-http-json-v1",
            """{"tracks":[{"mp3":"song.mp3"}],"txts":["Transferred by Directory Sam"]}"""
        );
        var rootArtifact = new RetainedHttpArtifact(
            "root.json",
            root.Payload.LongLength,
            root.Sha256
        );
        var body = new RetainedHttpArtifact(
            "directory.json",
            directory.Payload.LongLength,
            directory.Sha256
        );
        var binding = new RetainedLocalCatalogBinding(
            "bound",
            null,
            Source,
            false,
            Source,
            Show,
            null,
            [Source],
            [new(1, Artist, "trey")],
            [
                new(
                    0,
                    "https://audio.relisten.net/trey/target/song.mp3",
                    "bound",
                    null,
                    Guid.NewGuid(),
                    Source,
                    Guid.NewGuid()
                ),
            ],
            body,
            "sources.jsonl.gz",
            2
        );
        var provenance = new RetainedLocalProviderProvenance(
            directory.Uuid,
            root.Uuid,
            DateTimeOffset.UtcNow,
            "trey",
            "target",
            "https://audio.relisten.net/trey/target/metadata.json",
            rootArtifact,
            rootArtifact,
            body,
            body,
            body,
            0,
            body,
            binding
        );
        var rootRow = JsonSerializer.SerializeToElement(
            new
            {
                kind = "local_root_input",
                row = new { uuid = root.Uuid },
                input = root,
            },
            Json
        );
        return (
            rootRow,
            Project(
                new
                {
                    kind = "provider_input",
                    provider = "local",
                    row = new
                    {
                        uuid = directory.Uuid,
                        artist_id = 1,
                        artist_uuid = Artist,
                    },
                    input = directory,
                    retained_local = provenance,
                    source_context = new
                    {
                        id = 10,
                        uuid = Source,
                        artist_id = 1,
                        show_id = 20,
                    },
                    show_context = new
                    {
                        id = 20,
                        uuid = Show,
                        artist_id = 1,
                    },
                }
            )
        );
    }

    private static ShadowCatalogInput Project(object value) =>
        new(
            JsonSerializer.SerializeToElement(value, Json),
            null,
            new(1, Artist, "Fixture artist", []),
            "native"
        );

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
