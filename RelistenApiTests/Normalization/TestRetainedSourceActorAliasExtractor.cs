using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestRetainedSourceActorAliasExtractor
{
    private readonly RetainedSourceActorAliasExtractor extractor = new();

    [Test]
    public void AssertedRosterAliasRetainsOriginalInputAndEveryNestedCitation()
    {
        const string prose = "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals";
        var input = JsonInput(new { metadata = new { taper_notes = prose } });
        var result = extractor.Extract(
            input,
            [Field(input, "taper_notes", "/metadata/taper_notes", prose)]
        );

        var assertion = result.Assertions.Should().ContainSingle().Subject;
        assertion.Name.Should().Be("Andy Moresi");
        assertion.NameEvidence.Quote.Should().Be("Andy Moresi");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Hot Dog");
        assertion.Context.Heading.Quote.Should().Be("The Band:");
        assertion.Context.Statement.Quote.Should().Be("Andy Moresi aka Hot Dog - Keyboard/Vocals");
        assertion.Context.Qualifiers.Should().BeEmpty();
        assertion.Relationships.Should().ContainSingle().Which.Roles.Should().BeEmpty();
        result.Scopes.Should().ContainSingle().Which.Complete.Should().BeTrue();
        result.Unresolved.Should().BeEmpty();
        AssertCitations(input, result, "/metadata/taper_notes");
    }

    [Test]
    public void ConditionalRosterRetainsQualifierWithoutCreatingParticipation()
    {
        const string qualifier = "-- and some or all of the following --";
        const string prose =
            "Band line-up:\n"
            + qualifier
            + "\nJon Cunningham (aka Corn Mo) - lead, backing and choir vocals";
        var input = JsonInput(new { description = prose });
        var result = extractor.Extract(input, [Field(input, "description", "/description", prose)]);

        var assertion = result.Assertions.Should().ContainSingle().Subject;
        assertion.Name.Should().Be("Jon Cunningham");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Corn Mo");
        assertion.Context.Qualifiers.Should().ContainSingle().Which.Quote.Should().Be(qualifier);
        assertion.Relationships.Should().OnlyContain(relation => relation.Roles.Count == 0);
        AssertCitations(input, result, "/description");
    }

    [Test]
    public void NarrowHtmlFieldMapsCodePointsIntoOriginalDocumentAndIgnoresDisplayText()
    {
        const string prefix = "<header>🎵</header><section>";
        const string prose = "The Band:<br>Zoë aka <b>Lark</b> - vocals";
        var input = Input(prefix + prose + "</section>", "text/html");
        var start = prefix.EnumerateRunes().Count();
        var field = new ProviderTextObservation(
            "description",
            new(prose, new(input.Uuid, "", start, start + prose.EnumerateRunes().Count(), prose)),
            PlainText: "A different display derivation",
            Format: ProviderTextFormat.SourceMarkup
        );
        var result = extractor.Extract(input, [field]);

        var assertion = result.Assertions.Should().ContainSingle().Subject;
        assertion.Name.Should().Be("Zoë");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Lark");
        assertion.Context.Heading.StartCodePoint.Should().Be(start);
        assertion.Context.Statement.StartCodePoint.Should().Be(start + "The Band:<br>".Length);
        assertion.Context.Statement.Quote.Should().Be("Zoë aka <b>Lark</b> - vocals");
        AssertCitations(input, result, "");
    }

    [Test]
    public void RepeatedArrayFieldsKeepSeparateLocalActorsAndDeterministicReplay()
    {
        const string prose = "Personnel:\nZoë aka Lark - vocals";
        var input = JsonInput(new { description = new[] { prose, prose } });
        ProviderTextObservation[] fields =
        [
            Field(input, "description", "/description/0", prose),
            Field(input, "description", "/description/1", prose),
        ];
        var result = extractor.Extract(input, fields);
        var replay = extractor.Extract(input, fields);

        result.Assertions.Should().HaveCount(2);
        result
            .Assertions.Select(assertion => assertion.NameEvidence.JsonPointer)
            .Should()
            .Equal("/description/0", "/description/1");
        result
            .Assertions.Select(assertion => assertion.FieldSha256)
            .Distinct()
            .Should()
            .ContainSingle();
        var first = LocalIds(result.Assertions[0]);
        var second = LocalIds(result.Assertions[1]);
        first.Should().NotIntersectWith(second);
        result
            .Assertions.SelectMany(LocalIds)
            .Should()
            .Equal(replay.Assertions.SelectMany(LocalIds));
        foreach (var assertion in result.Assertions)
        {
            var references = assertion
                .Aliases.Select(alias => alias.ReferenceUuid)
                .Prepend(assertion.ActorReferenceUuid)
                .ToArray();
            assertion
                .Relationships.Should()
                .OnlyContain(relationship => relationship.ReferenceUuids.All(references.Contains));
        }
        AssertCitations(input, result);
    }

    [Test]
    public void ANewInputRevisionCannotReuseAnotherRevisionsActorReferences()
    {
        const string prose = "Personnel:\nZoë aka Lark - vocals";
        var first = JsonInput(new { description = prose });
        var second = JsonInput(new { description = prose });
        var firstResult = extractor.Extract(
            first,
            [Field(first, "description", "/description", prose)]
        );
        var secondResult = extractor.Extract(
            second,
            [Field(second, "description", "/description", prose)]
        );

        LocalIds(firstResult.Assertions.Single())
            .Should()
            .NotIntersectWith(LocalIds(secondResult.Assertions.Single()));
        AssertCitations(first, firstResult, "/description");
        AssertCitations(second, secondResult, "/description");
    }

    [Test]
    public void IdenticalHtmlSubfieldsAtDifferentOffsetsRemainSeparateActors()
    {
        const string prose = "Personnel:<br>Zoë aka Lark - vocals";
        const string firstPrefix = "<header>🎵</header><section>";
        var secondPrefix = firstPrefix + prose + "</section><section>";
        var input = Input(secondPrefix + prose + "</section>", "text/html");
        ProviderTextObservation Observe(string prefix)
        {
            var start = prefix.EnumerateRunes().Count();
            return new(
                "description",
                new(
                    prose,
                    new(input.Uuid, "", start, start + prose.EnumerateRunes().Count(), prose)
                )
            );
        }
        var result = extractor.Extract(input, [Observe(firstPrefix), Observe(secondPrefix)]);

        result.Assertions.Should().HaveCount(2);
        LocalIds(result.Assertions[0]).Should().NotIntersectWith(LocalIds(result.Assertions[1]));
        result
            .Assertions.Select(assertion => assertion.NameEvidence.StartCodePoint)
            .Should()
            .OnlyHaveUniqueItems();
        AssertCitations(input, result, "");
    }

    [Test]
    public void PureAndRetainedPathsPreserveTheSameProducerMeaningAndCoverage()
    {
        const string prose = "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals";
        var input = JsonInput(new { description = prose });
        var pure = new SourceActorAliasExtractor().Extract(
            new Dictionary<string, string?> { ["description"] = prose }
        );
        var retained = extractor.Extract(
            input,
            [Field(input, "description", "/description", prose)]
        );

        retained
            .Assertions.Select(assertion =>
                (assertion.Version, assertion.Name, assertion.FieldSha256)
            )
            .Should()
            .Equal(
                pure.Assertions.Select(assertion =>
                    (assertion.Version, assertion.Name, assertion.FieldSha256)
                )
            );
        retained
            .Assertions.Single()
            .Aliases.Select(alias => alias.Wording)
            .Should()
            .Equal(pure.Assertions.Single().Aliases.Select(alias => alias.Wording));
        retained
            .Scopes.Select(scope => (scope.Field, scope.Complete, scope.Reason))
            .Should()
            .Equal(pure.Scopes.Select(scope => (scope.Field, scope.Complete, scope.Reason)));
        retained
            .Assertions.Single()
            .ActorReferenceUuid.Should()
            .NotBe(pure.Assertions.Single().ActorReferenceUuid);
        AssertCitations(input, retained, "/description");
    }

    [Test]
    public void UnsupportedAliasStatementKeepsItsOriginalUnresolvedEvidence()
    {
        const string prose = "Zoë aka Lark might be around.";
        var input = JsonInput(new { notes = prose });
        var result = extractor.Extract(input, [Field(input, "notes", "/notes", prose)]);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().NotBeEmpty();
        result.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
        AssertCitations(input, result, "/notes");
    }

    [Test]
    public void ChangedTextForeignRevisionAndIncorrectSpanCannotProduceAssertions()
    {
        const string prose = "The Band:\nZoë aka Lark - vocals";
        var input = JsonInput(new { description = prose });
        Action changed = () =>
            extractor.Extract(
                input,
                [Field(input, "description", "/description", prose.Replace("Lark", "Pat"))]
            );
        Action foreign = () =>
            extractor.Extract(
                input,
                [new("description", new(prose, new(Guid.NewGuid(), "/description")))]
            );
        Action wrongSpan = () =>
            extractor.Extract(
                input,
                [new("description", new(prose, new(input.Uuid, "/description", 0, 3, prose)))]
            );

        changed.Should().Throw<ArgumentException>();
        foreign.Should().Throw<ArgumentException>();
        wrongSpan.Should().Throw<ArgumentException>();
    }

    [Test]
    public void NullProviderValueIsNotUpgradedToACompleteClear()
    {
        var input = JsonInput(new { description = (string?)null });
        Action extract = () =>
            extractor.Extract(
                input,
                [new("description", new("", new(input.Uuid, "/description")))]
            );

        extract.Should().Throw<ArgumentException>();
    }

    [TestCase("")]
    [TestCase(" \r\n\t")]
    public void AnEmptyRetainedFieldKeepsEvidenceButDoesNotAuthorizeWithdrawal(string prose)
    {
        var input = JsonInput(new { description = prose });
        var result = extractor.Extract(input, [Field(input, "description", "/description", prose)]);

        result.Assertions.Should().BeEmpty();
        result.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
        var citation = result.Scopes.Single().Evidence.Should().ContainSingle().Subject;
        citation.Quote.Should().Be(prose);
        AssertCitations(input, result, "/description");
    }

    private static IEnumerable<Guid> LocalIds(
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) =>
        new[] { assertion.AssertionUuid, assertion.ActorReferenceUuid }
            .Concat(
                assertion.Aliases.SelectMany(alias => new[] { alias.Uuid, alias.ReferenceUuid })
            )
            .Concat(assertion.Relationships.Select(relationship => relationship.Uuid))
            .Distinct();

    private static void AssertCitations(
        NormalizationInputRevision input,
        SourceActorAliasExtraction<NormalizedEvidenceReference> result,
        string? pointer = null
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
            .Concat(result.Scopes.SelectMany(scope => scope.Evidence))
            .ToArray();

        citations.Should().NotBeEmpty();
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var citation in citations)
        {
            citation.InputRevisionUuid.Should().Be(input.Uuid);
            if (pointer != null)
            {
                citation.JsonPointer.Should().Be(pointer);
            }
            document.ValidateEvidence(citation);
        }
    }

    private static ProviderTextObservation Field(
        NormalizationInputRevision input,
        string semanticField,
        string pointer,
        string value
    ) => new(semanticField, new(value, new(input.Uuid, pointer)));

    private static NormalizationInputRevision JsonInput(object payload) =>
        Input(JsonSerializer.Serialize(payload), "application/json");

    private static NormalizationInputRevision Input(string payload, string mediaType) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "role-free-roster"),
            Payload = Encoding.UTF8.GetBytes(payload),
            MediaType = mediaType,
            CaptureVersion = "archive-item-response-v1",
        };
}
