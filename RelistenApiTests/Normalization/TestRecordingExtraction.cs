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
    public void CoordinatedActionsShareTheActorWithoutBecomingPartOfItsName()
    {
        // String Cheese Incident: original structured taper wording, including its explicit alias.
        const string raw = "Recorded, Mastered and Posted By Pat Myers Aka Datpat";
        var result = credits.Extract(new Dictionary<string, string?> { ["taper"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Pat Myers", RecordingCreditRole.Taper),
                ("Pat Myers", RecordingCreditRole.MasteringEngineer),
                ("Pat Myers", RecordingCreditRole.Other)
            );
        result
            .Credits.Should()
            .OnlyContain(credit => credit.ActorFacts!.Aliases.Single().Wording == "Datpat");
        result.Credits.Last().OriginalRole.Should().Be("Recorded, Mastered and Posted By");
        foreach (var credit in result.Credits)
        {
            AssertCitation(raw, credit.ActorFacts!.Anchor);
        }
    }

    [TestCase("taper_notes", "Master Audience Recorded By Fitzy, Transfer By Keo;  ")]
    [TestCase("description", " <b>Master Audience Recorded By </b>Fitzy, <b>Transfer By </b>Keo")]
    public void NounFormTransferStartsItsOwnCreditAcrossStructuredAndHtmlFields(
        string field,
        string raw
    )
    {
        // Los Lobos taper notes and Ratdog HTML description use the same byline grammar.
        var result = credits.Extract(new Dictionary<string, string?> { [field] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(("Fitzy", RecordingCreditRole.Taper), ("Keo", RecordingCreditRole.Transferrer));
        foreach (var credit in result.Credits)
        {
            AssertCitation(raw, credit.ActorFacts!.Anchor);
        }
    }

    [Test]
    public void EditingAndRemasteringKeepTheirOwnActorAndLiteralUnsupportedAction()
    {
        // Grateful Dead: both the software qualifier and the action date are metadata, not names.
        const string raw =
            "Transfer by Charlie Miller Edited & reMastered using Adobe Audition by Joe Noel March 23, 2020";
        var result = credits.Extract(new Dictionary<string, string?> { ["transferrer"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Charlie Miller", RecordingCreditRole.Transferrer),
                ("Joe Noel", RecordingCreditRole.Other),
                ("Joe Noel", RecordingCreditRole.MasteringEngineer)
            );
        result.Credits[1].OriginalRole.Should().Be("Edited & reMastered using Adobe Audition by");
    }

    [Test]
    public void UnsupportedEngineeringDoesNotInheritThePreviousTaperRole()
    {
        // Archive.org Uncle John's Band metadata; engineering alone does not identify a taper.
        const string raw =
            "Multi-track recording, Uncle John’s Band at Ringside Cafe St Pete FL, mixed and mastered by Steve Connelly, recorded by JB Whiteley, engineered by Fil Pate.";
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Steve Connelly", RecordingCreditRole.Other),
                ("Steve Connelly", RecordingCreditRole.MasteringEngineer),
                ("JB Whiteley", RecordingCreditRole.Taper),
                ("Fil Pate", RecordingCreditRole.Other)
            );
        result.Credits.Last().OriginalRole.Should().Be("engineered by");
    }

    [TestCase("Recorded using microphones supplied by Pat", "supplied by")]
    [TestCase("Mastered using a recording provided by Pat", "provided by")]
    [TestCase("Recording supplied by Pat", "supplied by")]
    public void NestedSupplierBylinesCannotAttributeTheOuterRecordingAction(
        string raw,
        string originalRole
    )
    {
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = raw });

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Pat");
        credit.Role.Should().Be(RecordingCreditRole.Other);
        credit.OriginalRole.Should().Be(originalRole);
    }

    [Test]
    public void EquipmentQualifiersBeforeRecordingDoNotBecomeTheFirstActorsName()
    {
        // Grateful Dead structured taper field uses a noun-form recording byline.
        const string raw = "Master Digital FOB Recording by Dave Brotman & Jeff Metchick";
        var result = credits.Extract(new Dictionary<string, string?> { ["taper"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Dave Brotman", RecordingCreditRole.Taper),
                ("Jeff Metchick", RecordingCreditRole.Taper)
            );
    }

    [TestCase(
        "taper_notes",
        "Minor Edits: Fades (in/out at beginning/end of sets) Transferred by Dan Marquardt <danmarq@msn.com>, please contact me with questions/comments. There are also 48KHz shn's of this seed for archival.",
        "Dan Marquardt"
    )]
    [TestCase(
        "description",
        "Note: The RRPH set was recorded by Peter Costello & included Schoeps cmc64's -> ART Tube MP Studio Pre Amps -> Zoom H6N as source 2 and the Zoom H6N as the record deck instead of 788T.",
        "Peter Costello"
    )]
    [TestCase(
        "taper",
        "Recorded, edited, mixed, and mastered by Eric McRoberts 08/12/2017 - FOH mix by Ryan Breen - Lights by Victor Cornette - Thanks to Val Foster",
        "Eric McRoberts"
    )]
    [TestCase("source", "Editing and Mastering by Jamie Waddell in Wavelab 7.1", "Jamie Waddell")]
    public void ActionDatesEquipmentAndFollowupProseDoNotBecomeActors(
        string field,
        string raw,
        string expected
    )
    {
        // Retained Phish, JRAD/Rock and Roll Playhouse, Michelangelo Carubba/Cris Jacobs, and GD text.
        var result = credits.Extract(new Dictionary<string, string?> { [field] = raw });

        result.Credits.Should().NotBeEmpty().And.OnlyContain(credit => credit.Name == expected);
        foreach (var credit in result.Credits)
        {
            AssertCitation(raw, credit.ActorFacts!.Anchor);
        }
    }

    [TestCase("Taper Chris")]
    [TestCase("Jay in KC")]
    [TestCase("Dr. FOB")]
    [TestCase("sleepypedro (pwking)")]
    [TestCase("Mike D (VT)")]
    [TestCase("Top Hat Crew ( Big Jerry )")]
    [TestCase("Alice Vale (Mike at MSG)")]
    public void RoleWordsLocationsAndAmbiguousParenthesesStayLiteralNames(string name)
    {
        var result = credits.Extract(new Dictionary<string, string?> { ["taper"] = name });

        result.Credits.Should().ContainSingle().Which.Name.Should().Be(name);
        result.Credits.Single().ActorFacts!.Aliases.Should().BeEmpty();
    }

    [Test]
    public void AdjacentActionWordsStillPermitALiteralTaperHandle()
    {
        // Retained Twiddle byline; the role word in the handle is not an operation boundary.
        const string raw = "Recorded transferred and mastered by Taper Chris";
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Taper Chris", RecordingCreditRole.Taper),
                ("Taper Chris", RecordingCreditRole.Transferrer),
                ("Taper Chris", RecordingCreditRole.MasteringEngineer)
            );
    }

    [TestCase("Transfer")]
    [TestCase("DFC, FOB, bottom of hill on the grass, stand 12' high")]
    public void BareOperationsAndMicrophonePositionsDoNotCreatePeople(string raw)
    {
        var result = credits.Extract(new Dictionary<string, string?> { ["taper"] = raw });

        result.Credits.Should().BeEmpty();
        result.Unresolved.Should().NotBeEmpty();
    }

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

    [TestCase("René Diaz, Jr., Morgan Vale", "René Diaz, Jr.", "Morgan Vale")]
    [TestCase("Avery Reed, Sr. and Zoë (Night Owl)", "Avery Reed, Sr.", "Zoë (Night Owl)")]
    [TestCase("Jamie King, III; Pat Noor", "Jamie King, III", "Pat Noor")]
    [TestCase("Kai Moss,Jr., Pat Vale", "Kai Moss,Jr.", "Pat Vale")]
    [TestCase("Robin Marsh, Julian Vale", "Robin Marsh", "Julian Vale")]
    public void NameListsKeepPersonalSuffixesWithoutCombiningDifferentPeople(
        string original,
        string first,
        string second
    )
    {
        var result = credits.Extract(new Dictionary<string, string?> { ["taper"] = original });

        result.Credits.Select(credit => credit.Name).Should().Equal(first, second);
        result.Credits.Should().OnlyContain(credit => credit.Role == RecordingCreditRole.Taper);
        result.Unresolved.Should().BeEmpty();
        foreach (var credit in result.Credits)
        {
            AssertCitation(original, credit.Evidence);
        }
    }

    [Test]
    public void ExplicitNamesRetainSuffixPunctuationAndOriginalUnicodeHtmlEvidence()
    {
        const string text =
            "🎵<div>Recorded by Léon March, Jr.; Transferred by Zoë Pine, Sr.</div>";
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = text });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Léon March, Jr.", RecordingCreditRole.Taper),
                ("Zoë Pine, Sr.", RecordingCreditRole.Transferrer)
            );
        foreach (var credit in result.Credits)
        {
            credit
                .Evidence.Quote.Should()
                .Be("Recorded by Léon March, Jr.; Transferred by Zoë Pine, Sr.");
            AssertCitation(text, credit.Evidence);
        }
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

    [TestCase("Recorded", RecordingCreditRole.Taper)]
    [TestCase("Taped", RecordingCreditRole.Taper)]
    [TestCase("Mastered", RecordingCreditRole.MasteringEngineer)]
    public void EarlierChainDoesNotAssignTransferToAnExplicitRoleActor(
        string operation,
        RecordingCreditRole role
    )
    {
        var text = $"🎤<div>CD > WAV > FLAC {operation} By Mira Sol.</div>";
        var result = credits.Extract(new Dictionary<string, string?> { ["lineage"] = text });

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Mira Sol");
        credit.Role.Should().Be(role);
        AssertCitation(text, credit.Evidence);
    }

    [Test]
    public void ConversionActorsKeepTheirOwnRolesBesideExplicitAndNegatedCredits()
    {
        const string text =
            "CD > WAV Recorded by Mira Sol; EAC > FLAC by Jules Nor.\n"
            + "CD > WAV not recorded by Lena Moss. EAC > FLAC by Noor Hale.\n"
            + "EAC > FLAC Renamed and tagged by Avery Ray.";
        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = text });

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Mira Sol", RecordingCreditRole.Taper),
                ("Jules Nor", RecordingCreditRole.Transferrer),
                ("Noor Hale", RecordingCreditRole.Transferrer),
                ("Avery Ray", RecordingCreditRole.Other)
            );
        foreach (var credit in result.Credits)
        {
            AssertCitation(text, credit.Evidence);
        }
    }

    [Test]
    public void MixedLineAndHtmlBoundariesKeepOriginalUnicodeCreditCoordinates()
    {
        const string first = "  Recorded by <b>Ren&eacute; Vale</b>  ";
        const string second = "Transferred by Zoe\u0308 Hart";
        const string third = "Mastered by Jo Reed";
        const string raw =
            "🎵\r\n\r<div data-note='🌙'>"
            + first
            + "</div>\n\n<p>"
            + second
            + "</p>\r<br />\r\n"
            + third;

        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role, credit.Evidence))
            .Should()
            .Equal(
                (
                    "René Vale",
                    RecordingCreditRole.Taper,
                    new NormalizationTextEvidence("description", "/description", first, 23, 62)
                ),
                (
                    "Zoe\u0308 Hart",
                    RecordingCreditRole.Transferrer,
                    new NormalizationTextEvidence("description", "/description", second, 73, 97)
                ),
                (
                    "Jo Reed",
                    RecordingCreditRole.MasteringEngineer,
                    new NormalizationTextEvidence("description", "/description", third, 110, 129)
                )
            );
        result.Unresolved.Should().BeEmpty();
        foreach (var credit in result.Credits)
        {
            AssertCitation(raw, credit.Evidence);
        }
    }

    [Test]
    public void AngleContactsStayInTheirRawLineBeforeLaterCreditOffsets()
    {
        const string first =
            "Recorded by Alex Vale <br@example.test>; Transferred by Pat Lane <p@example.test>";
        const string last = "Mastered by Rae Stone";
        const string raw = "🌙<div>" + first + "</div>\r\n\r\n" + last;

        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role, credit.Evidence))
            .Should()
            .Equal(
                (
                    "Alex Vale",
                    RecordingCreditRole.Taper,
                    new NormalizationTextEvidence("description", "/description", first, 6, 87)
                ),
                (
                    "Pat Lane",
                    RecordingCreditRole.Transferrer,
                    new NormalizationTextEvidence("description", "/description", first, 6, 87)
                ),
                (
                    "Rae Stone",
                    RecordingCreditRole.MasteringEngineer,
                    new NormalizationTextEvidence("description", "/description", last, 97, 118)
                )
            );
        result
            .Credits[0]
            .ActorFacts!.Contacts.Should()
            .ContainSingle()
            .Which.Literal.Should()
            .Be("br@example.test");
        result
            .Credits[1]
            .ActorFacts!.Contacts.Should()
            .ContainSingle()
            .Which.Literal.Should()
            .Be("p@example.test");
        result.Credits[2].ActorFacts!.Contacts.Should().BeEmpty();
        result.Unresolved.Should().BeEmpty();
        foreach (var credit in result.Credits)
        {
            AssertCitation(raw, credit.Evidence);
        }
    }

    [Test]
    public void InvalidSurrogatePrefixesKeepReplacementRuneCreditOffsetsAcrossBoundaries()
    {
        const string first = "Recorded by Mina Vale";
        const string second = "Transferred by Bo Reed";
        const string raw =
            "\uD800\r\n\uDC00<div data-note='🧭'>\r<br />\n" + first + "\r\n\r" + second + "\n";

        var result = credits.Extract(new Dictionary<string, string?> { ["description"] = raw });

        result
            .Credits.Select(credit => (credit.Name, credit.Role, credit.Evidence))
            .Should()
            .Equal(
                (
                    "Mina Vale",
                    RecordingCreditRole.Taper,
                    new NormalizationTextEvidence("description", "/description", first, 31, 52)
                ),
                (
                    "Bo Reed",
                    RecordingCreditRole.Transferrer,
                    new NormalizationTextEvidence("description", "/description", second, 55, 77)
                )
            );
        result.Unresolved.Should().BeEmpty();
        foreach (var credit in result.Credits)
        {
            AssertCitation(raw, credit.Evidence);
        }
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
