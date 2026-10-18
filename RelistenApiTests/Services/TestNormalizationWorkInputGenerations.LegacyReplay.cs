using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationWorkInputGenerations
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task PreGenerationRequestWireStillMatchesDurableHashAndPublicReplay(
        bool contributed
    )
    {
        var input = await Capture("Historical generation-free request");
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var ticket = await BeginWithExpectations(
            contributed,
            subject,
            workUuid,
            contributor,
            input,
            [new(input.Revision, input.Resource)]
        );
        // Build the old request from independent two-field wire records. Serializing today's
        // binding-reference type here would hide an accidental new null Generation property.
        var oldWire = LegacyRequestWire(contributed, subject, contributor, input);
        var legacyHash = LegacyCanonicalHash(oldWire);
        var storedHash = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT request_hash FROM normalization.work_runs WHERE uuid=@workUuid",
                new { workUuid }
            )
        );
        storedHash.Should().Be(legacyHash);
        await fixture.Work.TryPublishAsync(workUuid, (_, _, _) => Task.CompletedTask);
        await Capture("A later head after old work committed", input);

        var replay = await BeginWithExpectations(
            contributed,
            subject,
            workUuid,
            contributor,
            input,
            [new(input.Revision, input.Resource, null)]
        );

        replay.Should().BeEquivalentTo(ticket);
        (
            await fixture.Work.TryPublishAsync(
                workUuid,
                (_, _, _) =>
                    throw new AssertionException("Legacy committed publication must replay")
            )
        )
            .Replayed.Should()
            .BeTrue();
    }

    private static JsonElement LegacyRequestWire(
        bool contributed,
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        Input input
    )
    {
        var oldSubject = new LegacySubject(subject.Domain, subject.Kind, subject.Uuid);
        var resource = new LegacyResource(
            input.Resource.Provider,
            input.Resource.ResourceType,
            input.Resource.ProviderKey
        );
        var oldContributor = new LegacyResource(
            contributor.Provider,
            contributor.ResourceType,
            contributor.ProviderKey
        );
        var expected = new[] { new LegacyInputReference(input.Revision, resource) };
        if (!contributed)
        {
            var configuration = JsonSerializer.SerializeToElement(
                new
                {
                    policyVersion = Policy,
                    resources = new[] { resource },
                    dependencies = Array.Empty<Guid>(),
                }
            );
            return JsonSerializer.SerializeToElement(
                new
                {
                    subject = oldSubject,
                    configurationHash = LegacyCanonicalHash(configuration),
                    expectedConfigurationGeneration = (long?)null,
                    expectedInputRevisions = expected,
                }
            );
        }
        // These names and empty fields are the pre-generation contributed batch protocol.
        // They are intentionally independent of private Work replacement/model classes.
        var prepared = new
        {
            Replacement = new
            {
                Contributor = oldContributor,
                ExpectedRevision = 0L,
                Bindings = new
                {
                    Resources = new[] { resource },
                    Dependencies = Array.Empty<Guid>(),
                    CatalogArtists = Array.Empty<Guid>(),
                    SourceIdentifiers = Array.Empty<string>(),
                },
            },
            ExpectedInputRevisions = expected,
            CatalogInputs = Array.Empty<object>(),
            SourceIdentities = Array.Empty<object>(),
            MappingRegistry = (object?)null,
            ImportedVenues = Array.Empty<object>(),
            CandidateUniverses = Array.Empty<object>(),
            OwnerObservations = Array.Empty<object>(),
            SourceUuids = Array.Empty<object>(),
        };
        return JsonSerializer.SerializeToElement(
            new
            {
                mode = "contributed_inputs_batch_v1",
                subject = oldSubject,
                policyVersion = Policy,
                contributions = new[] { prepared },
                expectedConfigurationGeneration = (long?)null,
                expectedContributionRevisions = (object?)null,
            }
        );
    }

    // The historical protocol hashes JSON with ordinal object keys and ordered arrays.
    // This oracle accepts only the independently declared old wire, never today's DTO JSON.
    private static string LegacyCanonicalHash(JsonElement wire)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            WriteLegacyValue(writer, wire);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    private static void WriteLegacyValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    var property in value
                        .EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                )
                {
                    writer.WritePropertyName(property.Name);
                    WriteLegacyValue(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteLegacyValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private sealed record LegacySubject(string Domain, string Kind, Guid Uuid);

    private sealed record LegacyResource(string Provider, string ResourceType, string ProviderKey);

    private sealed record LegacyInputReference(Guid InputRevisionUuid, LegacyResource Resource);
}
