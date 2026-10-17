using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestRecordingExtraction
{
    private readonly RecordingCreditExtractor credits = new();

    [Test]
    public void StructuredCreditsPreserveMultipleNamesAndSeparateRoles()
    {
        // Retained production sources 2012667 and 2402885.
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["taper"] = "Col George and Chris Davis",
                ["transferrer"] = "Rich Steele, Max Dawson, Jon Goldberg",
            }
        );

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Col George", RecordingCreditRole.Taper),
                ("Chris Davis", RecordingCreditRole.Taper),
                ("Rich Steele", RecordingCreditRole.Transferrer),
                ("Max Dawson", RecordingCreditRole.Transferrer),
                ("Jon Goldberg", RecordingCreditRole.Transferrer)
            );
        result.Unresolved.Should().BeEmpty();
    }

    [TestCase("taperjeff", "taper", RecordingCreditRole.Taper)]
    [TestCase("taperjeff", "transferrer", RecordingCreditRole.Transferrer)]
    [TestCase("Nate Tapernate Evans", "taper", RecordingCreditRole.Taper)]
    [TestCase("Dr. John", "taper", RecordingCreditRole.Taper)]
    [TestCase("Alex Herd (AlexHerd@TapersSection.com)", "taper", RecordingCreditRole.Taper)]
    public void RoleWordsInsideNamesDoNotReplaceStructuredFieldRoles(
        string original,
        string field,
        RecordingCreditRole role
    )
    {
        // Production sources 2401261, 2742233 and 2798019.
        var result = credits.Extract(new Dictionary<string, string?> { [field] = original });
        var expectedName = original.StartsWith("Alex Herd") ? "Alex Herd" : original;
        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be(expectedName);
        credit.Role.Should().Be(role);
        credit.Evidence.Quote.Should().Be(original);
        AssertCitation(original, credit.Evidence);
    }

    [Test]
    public void CreditSentencesDoNotBecomeNamesOrOverrideRoleModifiers()
    {
        // Production sources 2778123 and 3706576.
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["description"] =
                    "Taped by Jamie Syrek. Transferred by Drew Skiff from Jamie Syrek's master. EAC (Secure) by Ernie Dodd.\n"
                    + "Recorded by Joel Berk and lightly mastered by Doug Kaplan. Live sound by Ralph Darski.",
            }
        );
        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Jamie Syrek", RecordingCreditRole.Taper),
                ("Drew Skiff", RecordingCreditRole.Transferrer),
                ("Ernie Dodd", RecordingCreditRole.Transferrer),
                ("Joel Berk", RecordingCreditRole.Taper),
                ("Doug Kaplan", RecordingCreditRole.MasteringEngineer)
            );
    }

    [Test]
    public void SentenceBoundariesPreserveInitialsInExplicitNames()
    {
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["description"] = "Mastered By: Shane E. Calhoon. Transferred by Avery.",
            }
        );
        result.Credits.Select(credit => credit.Name).Should().Equal("Shane E. Calhoon", "Avery");
    }

    [Test]
    public void AnEmailAddressAloneDoesNotBecomeAPerson()
    {
        var result = credits.Extract(
            new Dictionary<string, string?> { ["taper"] = "AlexHerd@TapersSection.com" }
        );
        result.Credits.Should().BeEmpty();
        result.Unresolved.Should().ContainSingle();
    }

    [Test]
    public void TransferFieldSeparatesOperationAndDateFromNamedCredit()
    {
        // Retained production source 2709083.
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["transferrer"] = "EAC transfer: Chris Davis [9/24/25]",
            }
        );

        result.Credits.Should().ContainSingle().Which.Name.Should().Be("Chris Davis");
    }

    [Test]
    public void AadamDescriptionUsesExplicitRolesRatherThanArchiveMembership()
    {
        // Retained Wire source 4283991; a project reference alone supplies no role.
        const string text =
            "<div>Recording generously loaned from the Aadam Jacobs Audio Archive</div>"
            + "<div>Recorded by:\u00a0 Aadam Jacobs</div><div>Transferred by:\u00a0 Avery</div>"
            + "<div>Mastered by:\u00a0 auto_pilot</div>";
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = text });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Aadam Jacobs", RecordingCreditRole.Taper),
                ("Avery", RecordingCreditRole.Transferrer),
                ("auto_pilot", RecordingCreditRole.MasteringEngineer)
            );
        foreach (var credit in result.Credits)
        {
            AssertCitation(text, credit.Evidence);
        }
    }

    [Test]
    public void DecodedNamesRetainExactHtmlEvidenceAndUnicodeCodePointOffsets()
    {
        const string text = "🎵<div>tApEr: <b>Ren&eacute; &amp; Zoë</b></div>";
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = text });

        result.Credits.Select(credit => credit.Name).Should().Equal("René", "Zoë");
        var evidence = result.Credits[0].Evidence;
        evidence.Quote.Should().Be("tApEr: <b>Ren&eacute; &amp; Zoë</b>");
        evidence.StartCodePoint.Should().Be(6);
        AssertCitation(text, evidence);
    }

    [Test]
    public void OneExplicitPersonCanHoldMoreThanOneRole()
    {
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["description"] = "Recorded and transferred by: Jane Roe; Mastered by: Pat Lee",
            }
        );

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Jane Roe", RecordingCreditRole.Taper),
                ("Jane Roe", RecordingCreditRole.Transferrer),
                ("Pat Lee", RecordingCreditRole.MasteringEngineer)
            );
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Unknown")]
    [TestCase("N/A")]
    [TestCase("Sony D8 > DAT")]
    [TestCase("Soundboard")]
    [TestCase("D8")]
    [TestCase("AKG460")]
    public void MissingOrEquipmentCreditDoesNotCreateAPerson(string? value)
    {
        var result = credits.Extract(new Dictionary<string, string?> { ["taper"] = value });
        result.Credits.Should().BeEmpty();
    }

    [Test]
    public void NegatedAndUncreditedProseDoesNotBecomeNamedRole()
    {
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["description"] =
                    "This was not recorded by John Doe. Thanks to Avery and the Aadam Jacobs Collection.",
            }
        );
        result.Credits.Should().BeEmpty();
    }

    [Test]
    public void NamedConversionAndTaggingOperationsKeepTheirDifferentRoles()
    {
        // Credit syntax retained in Legion of Mary 4283444 and Reconstruction 4283452.
        var result = credits.Extract(
            new Dictionary<string, string?>
            {
                ["description"] =
                    "Renamed and tagged by MikeA.\n"
                    + "DAE(EAC) and sbe-free SHN encoding via C.Ladner.\n"
                    + "Conversion: CDR>EAC>WAV>MKW>SHNv3 By Dan Carr 10/18/02\n"
                    + "A > CD by Mark Cohen, EAC > SHN by Joe Jupille",
            }
        );
        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("MikeA", RecordingCreditRole.Other),
                ("C.Ladner", RecordingCreditRole.Transferrer),
                ("Dan Carr", RecordingCreditRole.Transferrer),
                ("Mark Cohen", RecordingCreditRole.Transferrer),
                ("Joe Jupille", RecordingCreditRole.Transferrer)
            );
        result.Credits[0].OriginalRole.Should().Be("Renamed and tagged by");
    }

    private static void AssertCitation(string raw, NormalizationTextEvidence evidence)
    {
        var runes = raw.EnumerateRunes().ToArray();
        var quote = string.Concat(
            runes[evidence.StartCodePoint..evidence.EndCodePoint].Select(rune => rune.ToString())
        );
        quote.Should().Be(evidence.Quote);
    }
}
