using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Attribution regressions from the frozen cross-artist development audit. Fixtures use
/// synthetic actors; the provider's exact spelling and role structure are the contract.
/// </summary>
[TestFixture]
public sealed class TestLunaRecordingCreditsV3
{
    [TestCase("Renée", "transferrer", "transfered by Renée")]
    [TestCase("River Lane", "taper", "Taped, Transfered, and Seeded By: River Lane")]
    [TestCase("River Lane", "transferrer", "Taped, Transfered, and Seeded By: River Lane")]
    [TestCase("Rae", "taper", "Recording and Transfer by Rae")]
    [TestCase("Rae", "transferrer", "Recording and Transfer by Rae")]
    [TestCase("Rae", "transferrer", "DAT>CD by Rae")]
    [TestCase("Rae", "transferrer", "converted by : Rae from DAT to Standalone")]
    [TestCase("Rae", "transferrer", "CDR > FLAc done by Rae")]
    [TestCase(
        "Rae",
        "transferrer",
        "Extraction:\r\nCD>xACT>WAV>FLAC, all tracks extracted with no errors\r\nby Rae"
    )]
    [TestCase("Rae", "transferrer", "Extraction:\nCD>WAV\nby: Rae")]
    [TestCase("Rae", "transferrer", "Transfer: (By Rae) 1st Generation Cassette -> WAV")]
    [TestCase("Rae", "transferrer", "Rae handled transfer.")]
    [TestCase("Rae (@river_taper)", "taper", "Taped by Rae (@river_taper)")]
    [TestCase("@river_taper", "taper", "Recorded by @river_taper")]
    [TestCase("Bea", "transferrer", "Taped by Ari; transferred by Bea")]
    [TestCase("Bea", "transferrer", "Taped by Ari and transferred by Bea")]
    [TestCase("Rae", "taper", "Recorded, mixed, mastered, and uploaded by Rae")]
    [TestCase("Rae", "taper", "Taped, processed and tracked by Rae")]
    [TestCase("Rae", "taper", "taped and prpcessed by Rae")]
    [TestCase("Rae", "transferrer", "Transfer and encoding by Rae")]
    [TestCase("Rae", "transferrer", "Transfer and shn creation by Rae")]
    [TestCase("Rae", "transferrer", "Transferred and shortened by Rae")]
    [TestCase("Rae (Dawn at Dusk)", "taper", "Recorded by Rae (Dawn at Dusk)")]
    [TestCase("Rae (Letters from Home)", "taper", "Recorded by Rae (Letters from Home)")]
    [TestCase("Rae (Along with Rain)", "taper", "Recorded by Rae (Along with Rain)")]
    [TestCase(
        "Rae (Dawn at Dusk)",
        "taper",
        "Recorded by Rae (Dawn at Dusk) using a handheld recorder"
    )]
    [TestCase(
        "Rae (Letters from Home)",
        "transferrer",
        "converted by Rae (Letters from Home) from DAT to CD"
    )]
    [TestCase("Rae", "taper", "Recorded by:\r\nRae")]
    public void AttributedCreditsPreserveLiteralUnicodeEvidence(
        string name,
        string role,
        string quote
    )
    {
        var raw = "🎤 original prefix\n" + quote;
        var result = Validate(raw, name, role, quote);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be(name);
        credit
            .Role.Should()
            .Be(role == "taper" ? RecordingCreditRole.Taper : RecordingCreditRole.Transferrer);
        credit
            .Evidence.Should()
            .Be(
                new NormalizationTextEvidence(
                    "taper_notes",
                    "/taper_notes",
                    quote,
                    18,
                    18 + quote.EnumerateRunes().Count()
                )
            );
    }

    [TestCase("Bea", "taper", "Taped by Ari; transferred by Bea")]
    [TestCase("Ari", "transferrer", "Taped by Ari; transferred by Bea")]
    [TestCase("Al", "taper", "Recorded by Alice")]
    [TestCase("Rae", "taper", "Taped by Rae (@river_taper)")]
    [TestCase("Rae (Dawn", "taper", "Recorded by Rae (Dawn at Dusk)")]
    [TestCase("Rae (Letters", "taper", "Recorded by Rae (Letters from Home)")]
    [TestCase("Rae (Along", "taper", "Recorded by Rae (Along with Rain)")]
    [TestCase("Rae (Letters", "transferrer", "converted by Rae (Letters from DAT to CD)")]
    [TestCase(
        "Rae (Letters from DAT to CD)",
        "transferrer",
        "converted by Rae (Letters from DAT to CD)"
    )]
    [TestCase("Rae <rae@example.org>", "taper", "Taped by Rae <rae@example.org>")]
    [TestCase("Rae", "transferrer", "Shn Encoding by Rae")]
    [TestCase("Rae", "transferrer", "Conversion process (from Rae): DAT>WAV")]
    [TestCase("Rae", "transferrer", "DAT>CD sent to Rae")]
    [TestCase("Rae", "transferrer", "Converted by Rae; another source uses DAT>CD")]
    [TestCase("Rae", "transferrer", "Converted by Rae from Bea, DAT from cassette to WAV")]
    [TestCase("Bea", "transferrer", "converted by Ari from DAT to CD, Bea from WAV to FLAC")]
    [TestCase("Rae", "transferrer", "Mastered by Rae; DAT>CD by Ari")]
    [TestCase("Ari", "transferrer", "Recorded by Ari\n\nExtraction:\nCD>WAV>FLAC\nby Bea")]
    [TestCase("Bea", "transferrer", "Extraction:\nCD>WAV>FLAC\n\nby Bea")]
    [TestCase("Bea", "transferrer", "Extraction:\nCD>WAV>FLAC\nby Ari\nNotes:\nby Bea")]
    [TestCase("Bea", "transferrer", "Conversion: Bea\n\nSource: DAT>CD")]
    [TestCase("Bea", "transferrer", "Received by Bea; DAT>CD by Ari")]
    [TestCase("Bea", "transferrer", "DAT>CD\n\nby Bea")]
    [TestCase("Bea", "taper", "Taped by\n\nBea")]
    [TestCase("Bea", "transferrer", "Transfer:\n\nBea")]
    [TestCase("Bea", "taper", "Instructions for recording: Bea")]
    [TestCase("Bea", "transferrer", "Instructions for transfer: Bea")]
    [TestCase("Bea", "taper", "Recorded and uploaded by Ari; processed by Bea")]
    [TestCase("Bea", "transferrer", "Transferred and encoded by Ari; uploaded by Bea")]
    [TestCase("Bea", "transferrer", "Encoding and shn creation by Bea")]
    public void OtherActorsAndUnattributedOperationsDoNotSupplyTheRole(
        string name,
        string role,
        string quote
    )
    {
        var call = () => Validate(quote, name, role, quote);

        call.Should().Throw<LunaCreditResponseException>();
    }

    [Test]
    public void AnInvalidSiblingRefusesTheWholeResponseInEitherOrder()
    {
        const string raw = "Taped by Ari; transferred by Bea";
        using var valid = JsonDocument.Parse(
            TestLunaRecordingCredits.Credit("Ari", "taper", "taper_notes", "Taped by Ari")
        );
        using var invalid = JsonDocument.Parse(
            TestLunaRecordingCredits.Credit("Bea", "taper", "taper_notes", "transferred by Bea")
        );
        var good = valid.RootElement.GetProperty("credits")[0].Clone();
        var bad = invalid.RootElement.GetProperty("credits")[0].Clone();

        foreach (var credits in new[] { new[] { good, bad }, new[] { bad, good } })
        {
            var response = JsonSerializer.Serialize(new { credits });
            var call = () =>
                LunaRecordingCreditExtractor.ValidateResponse(
                    new Dictionary<string, string?> { ["taper_notes"] = raw },
                    response
                );

            call.Should().Throw<LunaCreditResponseException>();
        }
    }

    [Test]
    public void WrongExplicitCoordinatesAreNotReplacedByTheUniqueQuote()
    {
        const string raw = "🎤 Taped by Rae";
        var wrong = () =>
            LunaRecordingCreditExtractor.ValidateResponse(
                new Dictionary<string, string?> { ["taper_notes"] = raw },
                TestLunaRecordingCredits.Credit(
                    "Rae",
                    "taper",
                    "taper_notes",
                    "Taped by Rae",
                    3,
                    14
                )
            );

        wrong.Should().Throw<LunaCreditResponseException>();
        Validate(raw, "Rae", "taper", "Taped by Rae")
            .Credits.Single()
            .Evidence.StartCodePoint.Should()
            .Be(2);
    }

    private static RecordingCreditExtraction Validate(
        string raw,
        string name,
        string role,
        string quote
    ) =>
        LunaRecordingCreditExtractor.ValidateResponse(
            new Dictionary<string, string?> { ["taper_notes"] = raw },
            TestLunaRecordingCredits.Credit(name, role, "taper_notes", quote)
        );
}
