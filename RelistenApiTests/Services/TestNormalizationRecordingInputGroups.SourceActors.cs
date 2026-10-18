using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationRecordingInputGroups
{
    [Test]
    public async Task SourceActorCheckpointPreservesEveryOriginalInputAndPublishesUnchanged()
    {
        var batch = await Batch(includeLegacy: true, sourceActorText: true);
        batch
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Should()
            .HaveCount(3);
        batch
            .AdditionalExtractions!.Single()
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Should()
            .ContainSingle();

        await proposals.SaveExtractionAsync(batch);
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
        (
            await fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await proposals.GetAsync(batch.SourceUuid)).Should().BeEquivalentTo(batch);
    }

    [Test]
    public async Task HistoricalAbsenceAcrossAllGroupsRemainsAbsentAndNeverBecomesAClear()
    {
        var batch = await Batch(includeLegacy: true, sourceActorText: true);
        batch = batch with
        {
            Extraction = batch.Extraction with { SourceActorAliases = null },
            AdditionalExtractions = batch
                .AdditionalExtractions!.Select(group =>
                    group with
                    {
                        Extraction = group.Extraction with { SourceActorAliases = null },
                    }
                )
                .ToArray(),
        };

        await proposals.SaveExtractionAsync(batch);

        var saved = (await proposals.GetAttemptBatchAsync(batch.AttemptUuid))!;
        saved.Extraction.SourceActorAliases.Should().BeNull();
        saved
            .AdditionalExtractions!.Should()
            .OnlyContain(group => group.Extraction.SourceActorAliases == null);
        saved.Should().BeEquivalentTo(batch);
    }

    [TestCase("scope_revision")]
    [TestCase("scope_resource")]
    [TestCase("recording_origin")]
    [TestCase("foreign_file")]
    [TestCase("file_scope_removed")]
    [TestCase("foreign_evidence")]
    [TestCase("recording_evidence")]
    [TestCase("quote")]
    [TestCase("field_hash")]
    [TestCase("name")]
    [TestCase("assertion_uuid")]
    [TestCase("alias_reference")]
    [TestCase("roles")]
    [TestCase("relationship_evidence")]
    [TestCase("context")]
    [TestCase("unresolved")]
    [TestCase("empty_coverage")]
    [TestCase("false_complete")]
    [TestCase("producer")]
    [TestCase("assertion_version")]
    [TestCase("missing_scope")]
    [TestCase("duplicate_scope")]
    [TestCase("spurious_scope")]
    [TestCase("dropped_assertion")]
    [TestCase("missing_group")]
    public async Task SourceActorCheckpointRejectsUnsupportedContentAndRecoversWithoutAPartialWrite(
        string change
    )
    {
        var batch = await Batch(includeLegacy: true, sourceActorText: true);
        var node = JsonSerializer.SerializeToNode(batch)!;
        var scopes = node["Extraction"]!["SourceActorAliases"]!.AsArray();
        var recording = scopes.Single(scope => scope!["FileOrigin"] == null)!;
        var files = scopes.Where(scope => scope!["FileOrigin"] != null).ToArray();
        var file = files[0]!;
        var sibling = files[1]!;
        var assertion = file["Extraction"]!["Assertions"]!.AsArray().Single()!;
        var alias = assertion["Aliases"]!.AsArray().Single()!;
        var relationship = assertion["Relationships"]!.AsArray().Single()!;
        var recordAssertion = recording["Extraction"]!["Assertions"]!.AsArray().Single()!;
        var siblingAssertion = sibling["Extraction"]!["Assertions"]!.AsArray().Single()!;
        var clear = sibling["Extraction"]!["Scopes"]!
            .AsArray()
            .Single(scope =>
                scope!["Reason"]!.GetValue<string>() == "field_clear_semantics_unavailable"
            )!;
        switch (change)
        {
            case "scope_revision":
                file["InputRevisionUuid"] = Guid.NewGuid();
                break;
            case "scope_resource":
                file["Resource"]!["ProviderKey"] = "foreign";
                break;
            case "recording_origin":
                file["RecordingOrigin"] = sibling["FileOrigin"]!.DeepClone();
                break;
            case "foreign_file":
                file["FileOrigin"] = sibling["FileOrigin"]!.DeepClone();
                break;
            case "file_scope_removed":
                file["FileOrigin"] = null;
                break;
            case "foreign_evidence":
                var peer = node["AdditionalExtractions"]![0]!["Extraction"]!["SourceActorAliases"]!
                    .AsArray()
                    .SelectMany(scope => scope!["Extraction"]!["Assertions"]!.AsArray())
                    .Single()!;
                assertion["NameEvidence"] = peer["NameEvidence"]!.DeepClone();
                break;
            case "recording_evidence":
                assertion["NameEvidence"] = recordAssertion["NameEvidence"]!.DeepClone();
                break;
            case "quote":
                alias["Evidence"]![0]!["Quote"] = "Invented quote";
                break;
            case "field_hash":
                assertion["FieldSha256"] = new string('0', 64);
                break;
            case "name":
                assertion["Name"] = "Invented Person";
                break;
            case "assertion_uuid":
                assertion["AssertionUuid"] = Guid.NewGuid();
                break;
            case "alias_reference":
                alias["ReferenceUuid"] = Guid.NewGuid();
                break;
            case "roles":
                relationship["Roles"]!.AsArray().Add((int)RecordingCreditRole.Taper);
                break;
            case "relationship_evidence":
                relationship["Evidence"] = siblingAssertion["Aliases"]![0]![
                    "Evidence"
                ]!.DeepClone();
                break;
            case "context":
                assertion["Context"]!["Heading"] = siblingAssertion["Context"]![
                    "Heading"
                ]!.DeepClone();
                break;
            case "unresolved":
                sibling["Extraction"]!["Unresolved"]![0]!["Evidence"] = alias[
                    "Evidence"
                ]!.DeepClone();
                break;
            case "empty_coverage":
                clear["Evidence"] = alias["Evidence"]!.DeepClone();
                break;
            case "false_complete":
                clear["Complete"] = true;
                break;
            case "producer":
                file["ProducerVersion"] = "another-producer";
                break;
            case "assertion_version":
                assertion["Version"] = "another-assertion-version";
                break;
            case "missing_scope":
                scopes.Remove(file);
                break;
            case "duplicate_scope":
                scopes.Add(file.DeepClone());
                break;
            case "spurious_scope":
                var extra = file.DeepClone();
                extra["FileOrigin"]!["Item"]!["ProviderKey"] = "invented-file";
                scopes.Add(extra);
                break;
            case "dropped_assertion":
                file["Extraction"]!["Assertions"]!.AsArray().Clear();
                break;
            case "missing_group":
                node["AdditionalExtractions"]![0]!["Extraction"]!["SourceActorAliases"] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
        var invalid = node.Deserialize<NormalizationRecordingProposalBatch>()!;

        Func<Task> save = () => proposals.SaveExtractionAsync(invalid);

        await save.Should().ThrowAsync<ArgumentException>();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        (await proposals.GetAsync(batch.SourceUuid)).Should().BeNull();
        await proposals.SaveExtractionAsync(batch);
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
    }
}
