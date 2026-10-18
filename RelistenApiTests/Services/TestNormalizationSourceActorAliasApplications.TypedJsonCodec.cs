using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase("source_correspondences")]
    [TestCase("extraction_owners")]
    public async Task StageCodecAcceptsSupportedOptionalNullsAndTransportWhitespace(string optional)
    {
        var stage = await ReviewedCodecStage();
        var canonical = SourceActorAliasApplicationCheckpointCodec.WriteStage(stage);
        var transport = JsonNode.Parse(canonical)!;
        var facts = transport["Value"]!["Value"]!["Facts"]!;
        var target =
            optional == "source_correspondences"
                ? facts["SourceReviews"]!.AsObject()
                : facts["OriginalExtractionTicket"]!.AsObject();
        var property =
            optional == "source_correspondences" ? "Correspondences" : "OwnerObservations";
        target
            .ContainsKey(property)
            .Should()
            .BeFalse("the canonical typed value omits this optional null");
        target[property] = null;
        var stored = transport.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var restored = SourceActorAliasApplicationCheckpointCodec.ReadStage(stored);

        // Both transports represent the same typed proof. Canonical comparison must not
        // mistake an explicit optional null or JSONB formatting for hidden proof blocks.
        SourceActorAliasApplicationCheckpointCodec.SameStage(stage, restored).Should().BeTrue();
        SourceActorAliasApplicationCheckpointCodec.WriteStage(restored).Should().Be(canonical);
    }

    [Test]
    public async Task StageCodecStillBoundsTheCompleteTypedEnvelopeBeforeDecoding()
    {
        var stage = await ReviewedCodecStage();
        var stored = JsonNode.Parse(SourceActorAliasApplicationCheckpointCodec.WriteStage(stage))!;
        stored["Value"]!["Value"]!["Facts"]!["OriginalExtractionTicket"]!["PolicyVersion"] =
            new string('x', SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes);

        Action read = () =>
            SourceActorAliasApplicationCheckpointCodec.ReadStage(stored.ToJsonString());

        read.Should().Throw<ArgumentException>().WithMessage("*32 MiB*no*truncated*");
    }
}
