using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Source aliases describe wording, not recording work or attendance. Real development
/// examples exercise that boundary; synthetic cases cover citation and parsing boundaries.
/// </summary>
[TestFixture]
public sealed class TestSourceActorAliasExtractor
{
    private readonly SourceActorAliasExtractor extractor = new();

    [Test]
    public void ExplicitRosterAliasRetainsItsStatementWithoutInventingRecordingRoles()
    {
        const string raw = "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Andy Moresi");
        assertion.NameEvidence.Quote.Should().Be("Andy Moresi");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Hot Dog");
        assertion.Context.Heading.Quote.Should().Be("The Band:");
        assertion.Context.Statement.Quote.Should().Be("Andy Moresi aka Hot Dog - Keyboard/Vocals");
        var relationship = assertion.Relationships.Should().ContainSingle().Which;
        relationship.Relationship.Should().Be(ActorReferenceRelationship.ExplicitAlias);
        relationship.Roles.Should().BeEmpty();
        relationship
            .ReferenceUuids.Should()
            .BeEquivalentTo(
                new[] { assertion.ActorReferenceUuid, assertion.Aliases.Single().ReferenceUuid }
            );
        new RecordingCreditExtractor().Extract(Fields(raw)).Credits.Should().BeEmpty();
        result.Scopes.Single().Complete.Should().BeTrue();
        AssertCitations(raw, result);
    }

    [Test]
    public void ConditionalCommaRosterAttachesAliasOnlyToTheNamedActor()
    {
        const string qualifier = "-- and some or all of the following --";
        const string raw =
            "Band line-up:\n"
            + qualifier
            + "\n"
            + "Jennifer Jobe, Jennie Kelley, Jessica Jordan, Julie Duncanville, Kelly Repka, "
            + "Michael Turner, Ron Ivy, Stephen Shelton Kirkham, Christy Stewart, "
            + "Jon Cunningham (aka Corn Mo) - lead, backing and choir vocals";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Jon Cunningham");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Corn Mo");
        assertion.Context.Qualifiers.Should().ContainSingle().Which.Quote.Should().Be(qualifier);
        assertion
            .Relationships.Should()
            .OnlyContain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
                && relationship.Roles.Count == 0
            );
        new RecordingCreditExtractor().Extract(Fields(raw)).Credits.Should().BeEmpty();
        AssertCitations(raw, result);
    }

    [TestCase("aka")]
    [TestCase("a.k.a.")]
    [TestCase("also known as")]
    public void ExplicitAliasMarkersUseTheSharedActorGrammar(string marker)
    {
        var raw = $"Personnel:\nAlice Vale {marker} \"Night & Day\" - keyboards";
        var result = Extract(raw);

        result
            .Assertions.Should()
            .ContainSingle()
            .Which.Aliases.Should()
            .ContainSingle()
            .Which.Wording.Should()
            .Be("Night & Day");
        result.Unresolved.Should().BeEmpty();
        AssertCitations(raw, result);
    }

    [Test]
    public void ExplicitRecordingBylineKeepsItsPrefixAndRoleFreeAlias()
    {
        const string prefix = "Recorded, Mastered and Posted by";
        const string raw = prefix + " Pat Myers AKA DATPAT";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Pat Myers");
        assertion.NameEvidence.Quote.Should().Be("Pat Myers");
        assertion.Context.Heading.Quote.Should().Be(prefix);
        assertion.Context.Qualifiers.Should().BeEmpty();
        assertion.Context.Statement.Quote.Should().Be(raw);
        var alias = assertion.Aliases.Should().ContainSingle().Which;
        alias.Wording.Should().Be("DATPAT");
        alias.Evidence[0].Quote.Should().Be("DATPAT");
        var relationship = assertion.Relationships.Should().ContainSingle().Which;
        relationship.Relationship.Should().Be(ActorReferenceRelationship.ExplicitAlias);
        relationship.Roles.Should().BeEmpty();
        relationship
            .ReferenceUuids.Should()
            .BeEquivalentTo(new[] { assertion.ActorReferenceUuid, alias.ReferenceUuid });
        var projection = PeopleActorProjection.Project(assertion, Guid.NewGuid());
        projection.Record.Kind.Should().BeNull();
        projection.Record.ActorContext.Should().BeNull();
        projection.Record.ExternalIdentities.Should().BeEmpty();
        result.Unresolved.Should().BeEmpty();
        result.Scopes.Single().Complete.Should().BeTrue();
        AssertCitations(raw, result);
    }

    [TestCase("Recorded and transferred by", "aka")]
    [TestCase("Taped, tracked, seeded by", "a.k.a.")]
    [TestCase("Recorded, mixed & posted by", "also known as")]
    [TestCase("Transfer by", "aka")]
    [TestCase("Engineered by", "aka")]
    [TestCase("Recorded transferred and mastered by", "aka")]
    public void BoundedBylinesReuseQuotedAliasParsing(string prefix, string marker)
    {
        var raw = $"{prefix} Alice Vale {marker} \"Night & Day\"";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Alice Vale");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Night & Day");
        assertion.Context.Heading.Quote.Should().Be(prefix);
        assertion.Relationships.Should().OnlyContain(relationship => relationship.Roles.Count == 0);
        AssertCitations(raw, result);
    }

    [Test]
    public void BylinePrefixNameAndAliasRemainInOriginalHtmlAndUnicodeCoordinates()
    {
        const string raw =
            "<p>🎵</p><p><b>Recorded, Mastered and Posted by</b> Zoë AKA <b>Lark</b></p>";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.NameEvidence.Quote.Should().Be("Zoë");
        assertion.Context.Heading.Quote.Should().Be("<b>Recorded, Mastered and Posted by</b>");
        assertion
            .Context.Statement.Quote.Should()
            .Be("<b>Recorded, Mastered and Posted by</b> Zoë AKA <b>Lark</b>");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Lark");
        AssertCitations(raw, result);
    }

    [TestCase("Not recorded by Pat Myers AKA DATPAT")]
    [TestCase("01. Recorded by Pat Myers AKA DATPAT")]
    [TestCase("Pat Myers AKA DATPAT")]
    [TestCase("Recorded by Pat Myers and Bob Reed AKA DATPAT")]
    [TestCase("Recorded by Pat Myers, Bob Reed AKA DATPAT")]
    [TestCase("Recorded by not Pat Myers AKA DATPAT")]
    [TestCase("Recorded by The band played AKA DATPAT")]
    [TestCase("Recorded by Pat Myers AKA DATPAT AKA Lark")]
    [TestCase("Recorded by Pat Myers AKA DATPAT with help from Bob Reed")]
    [TestCase("Recorded by Pat Myers AKA DATPAT. Notes follow.")]
    public void UnsupportedOrAmbiguousBylineAliasesRemainUnresolved(string raw)
    {
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().NotBeEmpty();
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [TestCase("Taped, tracked, seeded by Adam Downs (@Big_Perm_taper)")]
    [TestCase("Recorded by Alice Vale (Lark)")]
    public void BylineParenthesesWithoutAnAliasMarkerRemainLiteral(string raw)
    {
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().BeEmpty();
        AssertCitations(raw, result);
    }

    [Test]
    public void ABylineEndsRosterContextAndDoesNotBorrowItsQualifiers()
    {
        const string raw =
            "Personnel:\n-- and some or all of the following --\n"
            + "Recorded by Pat Myers AKA DATPAT\nAlice Vale aka Lark - vocals";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Pat Myers");
        assertion.Context.Heading.Quote.Should().Be("Recorded by");
        assertion.Context.Qualifiers.Should().BeEmpty();
        result.Unresolved.Should().ContainSingle();
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [Test]
    public void AnUnboundedCompoundAliasStaysUnresolved()
    {
        const string raw = "Personnel:\nAlice Vale aka Night & Day - keyboards";
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().NotBeEmpty();
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [Test]
    public void HtmlAndUnicodeKeepAllCitationsInTheOriginalSource()
    {
        const string raw = "<p>🎵</p><p>Personnel:</p><p>Zoë aka <b>Lark</b> - vocals</p>";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Zoë");
        assertion.NameEvidence.Quote.Should().Be("Zoë");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Lark");
        assertion.Context.Statement.Quote.Should().Be("Zoë aka <b>Lark</b> - vocals");
        AssertCitations(raw, result);
    }

    [Test]
    public void RepeatedNamesHaveSeparateSourceReferencesAndDeterministicReplay()
    {
        const string raw = "Personnel:\nAlice Vale aka Lark - vocals\nAlice Vale aka Lark - piano";
        var result = Extract(raw);
        var replay = Extract(raw);

        result.Assertions.Should().HaveCount(2);
        result
            .Assertions.Select(assertion => assertion.ActorReferenceUuid)
            .Should()
            .OnlyHaveUniqueItems();
        result
            .Assertions.Select(assertion => assertion.AssertionUuid)
            .Should()
            .Equal(replay.Assertions.Select(assertion => assertion.AssertionUuid));
        result
            .Assertions.SelectMany(assertion => assertion.Aliases)
            .Select(alias => alias.ReferenceUuid)
            .Should()
            .OnlyHaveUniqueItems();
        AssertCitations(raw, result);
    }

    [TestCase("Hot Dog & Hamburger", false)]
    [TestCase("01. Espanike' (aka Spank Me)", true)]
    [TestCase("\"Dingwalls Dance Hall\" at the time was aka \"Dingwalls Dancehall\"", true)]
    [TestCase("The Chimney Tops (aka Pilotdrift)", true)]
    [TestCase("1997-04-05 aka 04/05/1997", true)]
    [TestCase("Personnel:\nPhil Harmonix (Phil Knight) - keyboards", false)]
    [TestCase("Lineup:\nAdam Labeaux - Guitar, Harp, Vocals, Songwriting", false)]
    public void SongVenueDateEnsembleAndUnmarkedWordingDoNotAssertActorAliases(
        string raw,
        bool unresolvedAlias
    )
    {
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        if (unresolvedAlias)
        {
            result.Unresolved.Should().NotBeEmpty();
            result.Scopes.Single().Complete.Should().BeFalse();
        }

        AssertCitations(raw, result);
    }

    [Test]
    public void NumberedSetlistAndOrdinaryProseCloseTheRoster()
    {
        const string raw =
            "Personnel:\n01. Song aka Another Song - vocals\n"
            + "Alice Vale aka Lark - vocals\nPersonnel:\nThe band played an encore.\n"
            + "Bob Reed aka Finch - guitar";
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().HaveCount(3);
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [Test]
    public void APartialFieldKeepsTheClearAssertionAndTheUnsupportedStatement()
    {
        const string raw =
            "Personnel:\nAlice Vale aka Lark - vocals\n"
            + "Bonus material:\nThe Chimney Tops (aka Pilotdrift)";
        var result = Extract(raw);

        result.Assertions.Should().ContainSingle().Which.Name.Should().Be("Alice Vale");
        result
            .Unresolved.Should()
            .ContainSingle()
            .Which.Evidence.Single()
            .Quote.Should()
            .Be("The Chimney Tops (aka Pilotdrift)");
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" \r\n\t")]
    public void AbsentOrEmptyStringsDoNotAuthorizeSupportWithdrawal(string? raw)
    {
        var result = extractor.Extract(Fields(raw));

        result.Assertions.Should().BeEmpty();
        result.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
        if (raw != null)
        {
            AssertCitations(raw, result);
        }
    }

    [Test]
    public void AnOversizedFieldIsUnresolvedInsteadOfACompleteEmptyExtraction()
    {
        var raw = "Personnel:\n" + new string('x', 300_000) + " aka Lark - vocals";
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().ContainSingle().Which.Evidence.Single().Quote.Should().Be(raw);
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [Test]
    public void AnOversizedLineKeepsEarlierAssertionsAndClosesItsRoster()
    {
        var raw =
            "Personnel:\nAlice Vale aka Lark - vocals\n"
            + new string('x', 5_000)
            + " aka Finch - guitar\nBob Reed aka Jay - bass";
        var result = Extract(raw);

        result.Assertions.Should().ContainSingle().Which.Name.Should().Be("Alice Vale");
        result.Unresolved.Should().HaveCount(2);
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [Test]
    public void ExistingRecordingCreditAliasesKeepTheirActualRolesAndUnknownKind()
    {
        const string raw = "Recorded and transferred by John Hermsen (aka down2earthlandscaper)";
        var credits = new RecordingCreditExtractor().Extract(Fields(raw)).Credits;

        credits
            .Select(credit => credit.Role)
            .Should()
            .BeEquivalentTo(new[] { RecordingCreditRole.Taper, RecordingCreditRole.Transferrer });
        credits.Should().OnlyContain(credit => credit.Name == "John Hermsen");
        credits.Should().OnlyContain(credit => credit.ActorFacts!.Kind == null);
        foreach (var credit in credits)
        {
            credit
                .ActorFacts!.Aliases.Should()
                .ContainSingle()
                .Which.Wording.Should()
                .Be("down2earthlandscaper");
        }
        var assertion = Extract(raw).Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("John Hermsen");
        assertion.Relationships.Should().OnlyContain(relationship => relationship.Roles.Count == 0);

        var handle = new RecordingCreditExtractor()
            .Extract(new Dictionary<string, string?> { ["taper"] = "BIGT aka IAMHAMMERED" })
            .Credits.Should()
            .ContainSingle()
            .Which;
        handle.Name.Should().Be("BIGT");
        handle.ActorFacts!.Kind.Should().BeNull();
        handle.ActorFacts.Aliases.Should().ContainSingle().Which.Wording.Should().Be("IAMHAMMERED");
    }

    private SourceActorAliasExtraction<NormalizationTextEvidence> Extract(string raw) =>
        extractor.Extract(Fields(raw));

    private static Dictionary<string, string?> Fields(string? raw) =>
        new() { ["description"] = raw };

    private static void AssertCitations(
        string raw,
        SourceActorAliasExtraction<NormalizationTextEvidence> result
    )
    {
        var citations = result
            .Assertions.SelectMany(assertion =>
                new[]
                {
                    assertion.NameEvidence,
                    assertion.Context.Heading,
                    assertion.Context.Statement,
                }
                    .Concat(assertion.Context.Qualifiers)
                    .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
                    .Concat(
                        assertion.Relationships.SelectMany(relationship => relationship.Evidence)
                    )
                    .Concat(assertion.Unresolved.SelectMany(issue => issue.Evidence))
            )
            .Concat(result.Unresolved.SelectMany(issue => issue.Evidence))
            .Concat(result.Scopes.SelectMany(scope => scope.Evidence));
        var codePoints = raw.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        foreach (var evidence in citations)
        {
            evidence.Field.Should().Be("description");
            evidence.JsonPointer.Should().Be("/description");
            evidence.StartCodePoint.Should().BeGreaterThanOrEqualTo(0);
            evidence.EndCodePoint.Should().BeInRange(evidence.StartCodePoint, codePoints.Length);
            string.Concat(codePoints[evidence.StartCodePoint..evidence.EndCodePoint])
                .Should()
                .Be(evidence.Quote);
        }
    }
}
