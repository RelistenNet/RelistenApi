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

/// <summary>Retained model interpretations add quoted claims without replacing local facts.</summary>
[TestFixture]
public sealed class TestShadowLunaCreditOverlay
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private string directory = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-shadow-luna-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public void Model_credit_preserves_local_contacts_aliases_and_separate_source_identity()
    {
        var input = Input(
            "Alice Vale (alice@example.test)",
            "The Band:\nPat Smith aka Night Owl - keyboards"
        );
        var model = ValidatedCredit(input, "Alice Vale", "Alice Vale");
        var packet = Packet(new ModelCase(input, model));
        var local = Observe(new ShadowPeopleAdapter(), input).Process(input).ToArray();
        var results = Observe(new ShadowPeopleAdapter(packet.Directory, packet.InputHash), input)
            .Process(input)
            .ToArray();
        var people = People(results);
        var credits = Credits(results);

        people
            .People.Where(person => person.InterpretationBasis == null)
            .Should()
            .BeEquivalentTo(People(local).People);
        credits
            .Credits.Where(credit => credit.InterpretationBasis == null)
            .Should()
            .BeEquivalentTo(Credits(local).Credits);
        var modelPerson = people.People.Single(person =>
            person.InterpretationBasis == "retained_luna_extraction"
        );
        modelPerson.Aliases.Should().BeEmpty();
        modelPerson.Kind.Should().BeNull();
        var modelCredit = credits.Credits.Single(credit =>
            credit.InterpretationBasis == "retained_luna_extraction"
        );
        modelCredit.PersonEntityKey.Should().Be(modelPerson.EntityKey);
        modelCredit.SourceUuid.Should().Be(input.Uuid);
        modelCredit.ArtistUuid.Should().Be(input.Artist.Uuid);
        modelCredit.Evidence.Should().Be(model.Credits.Single().Evidence);
        credits.ModelInterpretation!.State.Should().Be("extracted");
        credits
            .ModelInterpretation.Basis.ExtractorVersion.Should()
            .Be(LunaRecordingCreditExtractor.Version);
        credits.ModelInterpretation.Basis.CacheKey.Should().HaveLength(64);
        var evidence = JsonSerializer.SerializeToElement(
            results.Single(result => result.Domain == "people").Evidence,
            Json
        );
        evidence.GetProperty("creditActors").GetArrayLength().Should().BeGreaterThan(0);
        evidence
            .GetProperty("sourceActorAliases")
            .GetProperty("assertions")
            .GetArrayLength()
            .Should()
            .BeGreaterThan(0);
        evidence
            .GetProperty("model")
            .GetProperty("originalLunaResult")
            .GetProperty("state")
            .GetString()
            .Should()
            .Be("extracted");
        people
            .People.Single(person =>
                person.InterpretationBasis == null && person.Name == "Alice Vale"
            )
            .EntityKey.Should()
            .NotBe(modelPerson.EntityKey);
    }

    [Test]
    public void Attached_profile_in_a_validated_literal_name_stays_quoted_without_identity_facts()
    {
        const string name = "Daniel Barrero (http://www.myspace.com/musickapes)";
        var first = Input(name);
        var second = Input(name);
        var firstCredit = ValidatedCredit(first, name, name);
        var packet = Packet(
            new ModelCase(first, firstCredit),
            new ModelCase(second, ValidatedCredit(second, name, name))
        );
        var adapter = Observe(
            new ShadowPeopleAdapter(packet.Directory, packet.InputHash),
            first,
            second
        );
        var results = adapter.Process(first).ToArray();
        var modelPerson = People(results)
            .People.Single(person => person.InterpretationBasis == "retained_luna_extraction");
        var modelCredit = Credits(results)
            .Credits.Single(credit => credit.InterpretationBasis == "retained_luna_extraction");

        modelPerson.Name.Should().Be(name);
        modelPerson.Aliases.Should().BeEmpty();
        modelPerson.Kind.Should().BeNull();
        modelCredit.Name.Should().Be(name);
        modelCredit.Role.Should().Be(RecordingCreditRole.Taper);
        modelCredit.Evidence.Should().Be(firstCredit.Credits.Single().Evidence);
        modelCredit.Evidence.Quote.Should().Be(name);
        modelCredit.Evidence.EndCodePoint.Should().Be(name.EnumerateRunes().Count());
        modelCredit.SourceUuid.Should().Be(first.Uuid);
        modelCredit.PersonEntityKey.Should().Be(modelPerson.EntityKey);
        People(results)
            .Relationships.Where(link => link.FromEntityKey == modelPerson.EntityKey)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(link => link.Kind == "unresolved_name_match");
    }

    [Test]
    public void Empty_and_unavailable_model_results_never_retire_local_credits()
    {
        foreach (var state in new[] { "extracted", "budget_exhausted", "unresolved" })
        {
            var input = Input("Alice Vale");
            var model =
                state == "extracted"
                    ? new RecordingCreditExtraction([], [])
                    : Extraction(input).CreditExtraction!;
            var packet = Packet(
                new ModelCase(
                    input,
                    model,
                    state,
                    state == "extracted" ? null : "fixture_unavailable"
                )
            );
            var results = Observe(
                    new ShadowPeopleAdapter(packet.Directory, packet.InputHash),
                    input
                )
                .Process(input)
                .ToArray();

            Credits(results)
                .Credits.Should()
                .ContainSingle()
                .Which.InterpretationBasis.Should()
                .BeNull();
            Credits(results).ModelInterpretation!.State.Should().Be(state);
            Credits(results).ModelInterpretation!.CreditEntityKeys.Should().BeEmpty();
            results.Should().OnlyContain(result => result.Outcome == "proposed");
        }
    }

    [Test]
    public void Model_only_credit_names_retrieve_cross_artist_alternatives_without_identity_merge()
    {
        var first = Input("Alice Vale", missingLocalCredits: true);
        var second = Input("Alice Vale", missingLocalCredits: true);
        var packet = Packet(
            new ModelCase(first, ValidatedCredit(first, "Alice Vale", "Alice Vale")),
            new ModelCase(second, ValidatedCredit(second, "Alice Vale", "Alice Vale"))
        );
        var adapter = Observe(
            new ShadowPeopleAdapter(packet.Directory, packet.InputHash),
            first,
            second
        );
        var firstResults = adapter.Process(first).ToArray();
        var secondResults = adapter.Process(second).ToArray();

        firstResults.Should().OnlyContain(result => result.Outcome == "proposed");
        var firstPerson = People(firstResults).People.Should().ContainSingle().Which;
        var secondPerson = People(secondResults).People.Should().ContainSingle().Which;
        firstPerson.EntityKey.Should().NotBe(secondPerson.EntityKey);
        var link = People(firstResults).Relationships.Should().ContainSingle().Which;
        link.Kind.Should().Be("unresolved_name_match");
        link.ToEntityKey.Should().Be(secondPerson.EntityKey);
        link.TargetArtistUuid.Should().Be(second.Artist.Uuid);
        firstResults
            .Single(result => result.Domain == "people")
            .EntityKeys.Should()
            .Contain(secondPerson.EntityKey);
        Credits(firstResults)
            .Credits.Should()
            .ContainSingle()
            .Which.PersonEntityKey.Should()
            .Be(firstPerson.EntityKey);
    }

    [Test]
    public void Changed_original_fields_disable_only_the_model_interpretation()
    {
        var selected = Input("Alice Vale");
        var packet = Packet(
            new ModelCase(selected, ValidatedCredit(selected, "Alice Vale", "Alice Vale"))
        );
        var currentFields = new Dictionary<string, string?>(Extraction(selected).Inputs)
        {
            ["taper"] = "Bob Reed",
        };
        var currentExtraction = Extraction(selected) with
        {
            Inputs = currentFields,
            CreditExtraction = new RecordingCreditExtractor().Extract(currentFields),
        };
        var currentRow = JsonNode.Parse(selected.Record.GetRawText())!;
        currentRow["row"]!["taper"] = "Bob Reed";
        var current = selected with
        {
            Record = JsonSerializer.SerializeToElement(currentRow, Json),
            Extraction = JsonSerializer.SerializeToElement(currentExtraction, Json),
        };
        var results = Observe(new ShadowPeopleAdapter(packet.Directory, packet.InputHash), current)
            .Process(current)
            .ToArray();

        Credits(results).Credits.Should().ContainSingle().Which.Name.Should().Be("Bob Reed");
        Credits(results).ModelInterpretation!.State.Should().Be("input_mismatch");
        Credits(results).ModelInterpretation!.CreditEntityKeys.Should().BeEmpty();
        results.Should().OnlyContain(result => result.Outcome == "proposed");
    }

    [Test]
    public void Changed_raw_row_cannot_reuse_a_stale_saved_extraction_as_model_evidence()
    {
        var input = Input("Alice Vale");
        var packet = Packet(
            new ModelCase(input, ValidatedCredit(input, "Alice Vale", "Alice Vale"))
        );
        var record = JsonNode.Parse(input.Record.GetRawText())!;
        record["row"]!["taper"] = "Bob Reed";
        var changed = input with { Record = JsonSerializer.SerializeToElement(record, Json) };
        var adapter = Observe(new ShadowPeopleAdapter(packet.Directory, packet.InputHash), changed);
        var results = adapter.Process(changed).ToArray();

        Credits(results).ModelInterpretation!.State.Should().Be("input_mismatch");
        Credits(results).ModelInterpretation!.CreditEntityKeys.Should().BeEmpty();
        Credits(results).Credits.Should().OnlyContain(credit => credit.InterpretationBasis == null);
        People(results).People.Should().OnlyContain(person => person.InterpretationBasis == null);
    }

    [Test]
    public void A_different_artist_cannot_reuse_selected_model_credits()
    {
        var selected = Input("Alice Vale");
        var packet = Packet(
            new ModelCase(selected, ValidatedCredit(selected, "Alice Vale", "Alice Vale"))
        );
        var artist = selected.Artist with { Uuid = Guid.NewGuid() };
        var current = selected with
        {
            Artist = artist,
            Extraction = JsonSerializer.SerializeToElement(
                Extraction(selected) with
                {
                    ArtistUuid = artist.Uuid,
                },
                Json
            ),
        };
        var results = Observe(new ShadowPeopleAdapter(packet.Directory, packet.InputHash), current)
            .Process(current)
            .ToArray();

        Credits(results).ModelInterpretation!.State.Should().Be("input_mismatch");
        Credits(results)
            .Credits.Should()
            .ContainSingle()
            .Which.InterpretationBasis.Should()
            .BeNull();
    }

    [Test]
    public void Retained_unicode_quote_survives_with_exact_code_point_coordinates()
    {
        var input = Input("Renée 🌿 River");
        var model = ValidatedCredit(input, "Renée 🌿 River", "Renée 🌿 River");
        var packet = Packet(new ModelCase(input, model));
        var results = Observe(new ShadowPeopleAdapter(packet.Directory, packet.InputHash), input)
            .Process(input)
            .ToArray();
        var evidence = Credits(results)
            .Credits.Single(credit => credit.InterpretationBasis is not null)
            .Evidence;

        evidence.Quote.Should().Be("Renée 🌿 River");
        evidence.EndCodePoint.Should().Be("Renée 🌿 River".EnumerateRunes().Count());
    }

    [TestCase("selection.json")]
    [TestCase("results.jsonl")]
    public void Changed_retained_bytes_are_rejected_before_interpretation(string file)
    {
        var input = Input("Alice Vale");
        var packet = Packet(
            new ModelCase(input, ValidatedCredit(input, "Alice Vale", "Alice Vale"))
        );
        File.AppendAllText(Path.Combine(packet.Directory, file), " ");

        var load = () => new ShadowPeopleAdapter(packet.Directory, packet.InputHash);
        load.Should().Throw<Exception>();
    }

    [Test]
    public void Refreshed_file_hashes_cannot_justify_a_false_evidence_span()
    {
        var input = Input("Renée 🌿 River");
        var packet = Packet(
            new ModelCase(input, ValidatedCredit(input, "Renée 🌿 River", "Renée 🌿 River"))
        );
        var resultsPath = Path.Combine(packet.Directory, "results.jsonl");
        var row = JsonNode.Parse(File.ReadAllText(resultsPath))!;
        row["luna"]!["extraction"]!["credits"]![0]!["evidence"]!["endCodePoint"] = 1;
        File.WriteAllText(resultsPath, row.ToJsonString() + "\n");
        RefreshResultsHash(packet);

        var load = () => new ShadowPeopleAdapter(packet.Directory, packet.InputHash);
        load.Should().Throw<InvalidDataException>().WithMessage("*Unicode span*");
    }

    [Test]
    public void Changed_packet_or_wrong_extraction_cannot_reuse_a_shadow_run_version()
    {
        var input = Input("Alice Vale");
        var first = Packet(
            new ModelCase(input, ValidatedCredit(input, "Alice Vale", "Alice Vale"))
        );
        var empty = Packet(new ModelCase(input, new([], [])));
        new ShadowPeopleAdapter(first.Directory, first.InputHash)
            .Version.Should()
            .NotBe(new ShadowPeopleAdapter(empty.Directory, empty.InputHash).Version);
        var wrongInput = () => new ShadowPeopleAdapter(first.Directory, new string('f', 64));
        wrongInput.Should().Throw<InvalidDataException>();
        var unbound = () => new ShadowPeopleAdapter(first.Directory, null);
        unbound.Should().Throw<ArgumentException>();
    }

    private static ShadowPeopleAdapter Observe(
        ShadowPeopleAdapter adapter,
        params ShadowCatalogInput[] inputs
    )
    {
        foreach (var input in inputs)
        {
            adapter.Observe(input.Record);
            adapter.Observe(input.Extraction!.Value);
        }
        return adapter;
    }

    private static ShadowPeopleProposal People(ShadowCatalogResult[] results) =>
        (ShadowPeopleProposal)results.Single(result => result.Domain == "people").Proposal!;

    private static ShadowCreditsProposal Credits(ShadowCatalogResult[] results) =>
        (ShadowCreditsProposal)results.Single(result => result.Domain == "credits").Proposal!;

    private static NormalizationBatchExtraction Extraction(ShadowCatalogInput input) =>
        input.Extraction!.Value.Deserialize<NormalizationBatchExtraction>(Json)!;

    private static RecordingCreditExtraction ValidatedCredit(
        ShadowCatalogInput input,
        string name,
        string quote
    ) =>
        LunaRecordingCreditExtractor.ValidateResponse(
            Extraction(input).Inputs,
            JsonSerializer.Serialize(
                new
                {
                    credits = new[]
                    {
                        new
                        {
                            name,
                            role = "taper",
                            field = "taper",
                            quote,
                            start = (int?)null,
                            end = (int?)null,
                        },
                    },
                }
            )
        );

    private PacketReference Packet(params ModelCase[] cases)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var inputs = cases.Select(item => Extraction(item.Input)).ToArray();
        var inputHash = Hash(
            Encoding.UTF8.GetBytes(
                string.Join("\n", inputs.Select(item => JsonSerializer.Serialize(item, Json)))
                    + "\n"
            )
        );
        var selection = new LunaCreditEvaluationSelection(
            inputHash,
            new string('b', 64),
            JsonSerializer.SerializeToElement(new { fixture = "completed extraction" }),
            inputs
                .Select(item => new LunaCreditEvaluationInput("local_credit_present", item))
                .ToArray()
        );
        File.WriteAllText(
            Path.Combine(path, "selection.json"),
            JsonSerializer.Serialize(selection, Json)
        );
        File.WriteAllLines(
            Path.Combine(path, "results.jsonl"),
            cases.Select(item =>
                JsonSerializer.Serialize(
                    new
                    {
                        cohort = "local_credit_present",
                        resourceUuid = item.Input.Uuid,
                        artistUuid = item.Input.Artist.Uuid,
                        family = Extraction(item.Input).Family,
                        local = Extraction(item.Input).CreditExtraction,
                        luna = new LunaCreditExtractionResult(
                            item.State,
                            item.Extraction,
                            new string('c', 64),
                            false,
                            item.Reason
                        ),
                    },
                    Json
                )
            )
        );
        var states = cases
            .GroupBy(item => item.State)
            .ToDictionary(group => group.Key, group => group.Count());
        var report = new LunaCreditEvaluationReport(
            cases.All(item => item.State == "extracted") ? "completed" : "completed_partial",
            Guid.NewGuid(),
            inputHash,
            LunaRecordingCreditExtractor.Version,
            cases.Length,
            0,
            states,
            25,
            0,
            SelectionSha256: Hash(File.ReadAllBytes(Path.Combine(path, "selection.json"))),
            ResultsSha256: Hash(File.ReadAllBytes(Path.Combine(path, "results.jsonl")))
        );
        File.WriteAllText(
            Path.Combine(path, "manifest.json"),
            JsonSerializer.Serialize(report, Json)
        );
        return new(path, inputHash);
    }

    private static void RefreshResultsHash(PacketReference packet)
    {
        var path = Path.Combine(packet.Directory, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["resultsSha256"] = Hash(
            File.ReadAllBytes(Path.Combine(packet.Directory, "results.jsonl"))
        );
        File.WriteAllText(path, manifest.ToJsonString());
    }

    private static ShadowCatalogInput Input(
        string taper,
        string? description = null,
        bool missingLocalCredits = false
    )
    {
        var uuid = Guid.NewGuid();
        var artist = new ShadowCatalogArtist(1, Guid.NewGuid(), "Fixture artist", ["archive.org"]);
        var fields = new Dictionary<string, string?>
        {
            ["taper"] = taper,
            ["description"] = description,
        };
        var row = new Dictionary<string, object?>
        {
            ["id"] = 42,
            ["uuid"] = uuid,
            ["artist_id"] = artist.Id,
            ["taper"] = taper,
            ["description"] = description,
        };
        var record = JsonSerializer.SerializeToElement(new { kind = "source_sample", row }, Json);
        var extraction = new NormalizationBatchExtraction(
            "source_sample",
            42,
            uuid,
            artist.Id,
            artist.Uuid,
            "fixture-family",
            fields,
            null,
            missingLocalCredits ? new([], []) : new RecordingCreditExtractor().Extract(fields),
            null,
            new SourceActorAliasExtractor().Extract(fields)
        );
        return new(
            record,
            JsonSerializer.SerializeToElement(extraction, Json),
            artist,
            "archive.org"
        );
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed record ModelCase(
        ShadowCatalogInput Input,
        RecordingCreditExtraction Extraction,
        string State = "extracted",
        string? Reason = null
    );

    private sealed record PacketReference(string Directory, string InputHash);
}
