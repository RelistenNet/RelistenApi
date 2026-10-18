using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Synthetic credits exercise actor attribution and exact source citations. Contact and
/// profile facts describe the credited wording; they do not establish canonical identities.
/// </summary>
[TestFixture]
public sealed class TestRecordingActorEvidence
{
    private readonly RecordingCreditExtractor extractor = new();

    [TestCase("aka")]
    [TestCase("a.k.a.")]
    [TestCase("also known as")]
    public void ExplicitAliasPreservesDisjointNamesAndTheirSourceRelationship(string aliasMarker)
    {
        var raw = $"Recorded by Alice Vale {aliasMarker} Night Owl";

        var result = Extract("description", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Anchor.Quote.Should().Be("Alice Vale");
        var alias = actor.Aliases.Should().ContainSingle().Which;
        alias.Wording.Should().Be("Night Owl");
        alias.Evidence.Should().Contain(evidence => evidence.Quote == "Night Owl");
        actor
            .Relationships.Should()
            .Contain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
                && relationship.ReferenceUuids.Contains(actor.ReferenceUuid)
                && relationship.ReferenceUuids.Contains(alias.ReferenceUuid)
                && relationship.Evidence.Any(evidence => evidence.Quote.Contains(aliasMarker))
            );
        actor.Kind.Should().BeNull();
        AssertCitations(raw, actor);
    }

    [TestCase("Alice Vale aka \"Night & Day\"")]
    [TestCase("Alice Vale (aka Night & Day)")]
    public void BoundedCompoundAliasDoesNotSplitIntoAnotherCreditedActor(string raw)
    {
        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Night & Day");
        actor
            .Relationships.Should()
            .NotContain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.DistinctActors
            );
        AssertCitations(raw, actor);
    }

    [Test]
    public void UnboundedCompoundAliasKeepsTheWholeAmbiguousWordingWithoutInventingAnotherActor()
    {
        const string raw = "Alice Vale aka Night & Day";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Aliases.Should().BeEmpty();
        actor
            .Unresolved.Should()
            .Contain(issue =>
                issue.Reason == "ambiguous_alias_boundary"
                && issue.Evidence.Any(evidence => evidence.Quote.Contains("aka Night & Day"))
            );
        AssertCitations(raw, actor);
    }

    [Test]
    public void ParentheticalAndTrailingAliasesKeepSeparateExactReferences()
    {
        const string raw = "Alice Vale (aka Alpha) aka Bravo";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor
            .Aliases.Select(alias => alias.Wording)
            .Should()
            .BeEquivalentTo(new[] { "Alpha", "Bravo" });
        actor.Aliases.Select(alias => alias.ReferenceUuid).Distinct().Should().HaveCount(2);
        actor
            .Aliases.Should()
            .OnlyContain(alias => alias.Evidence.Any(evidence => evidence.Quote == alias.Wording));
        AssertCitations(raw, actor);
    }

    [TestCase("Recorded and transferred by Alice Vale")]
    [TestCase("<div>Taped &amp; Transferred by Alice Vale</div>")]
    public void OneExplicitJointRoleClauseReferencesTheSameActorForBothRoles(string raw)
    {
        var result = Extract("description", raw);

        result
            .Credits.Select(credit => credit.Role)
            .Should()
            .BeEquivalentTo(new[] { RecordingCreditRole.Taper, RecordingCreditRole.Transferrer });
        result
            .Credits.Select(credit => credit.Name)
            .Should()
            .OnlyContain(name => name == "Alice Vale");
        var actors = result.Credits.Select(Facts).ToArray();
        actors.Select(actor => actor.ActorUuid).Distinct().Should().ContainSingle();
        actors.Select(actor => actor.ReferenceUuid).Distinct().Should().ContainSingle();
        actors
            .SelectMany(actor => actor.Relationships)
            .Should()
            .Contain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.JointRoles
                && relationship.Roles.Contains(RecordingCreditRole.Taper)
                && relationship.Roles.Contains(RecordingCreditRole.Transferrer)
            );
        if (raw.Contains("&amp;", StringComparison.Ordinal))
        {
            actors
                .SelectMany(actor => actor.Relationships)
                .Should()
                .Contain(relationship =>
                    relationship.Relationship == ActorReferenceRelationship.JointRoles
                    && relationship.Evidence.Any(evidence => evidence.Quote.Contains("&amp;"))
                );
        }
        foreach (var actor in actors)
        {
            AssertCitations(raw, actor);
        }
    }

    [Test]
    public void CoordinatedNamesPreserveDistinctSourceActors()
    {
        const string raw = "Recorded by Alice Vale and Bob Reed";

        var result = Extract("description", raw);

        result.Credits.Select(credit => credit.Name).Should().Equal("Alice Vale", "Bob Reed");
        var actors = result.Credits.Select(Facts).ToArray();
        actors.Select(actor => actor.ActorUuid).Distinct().Should().HaveCount(2);
        var references = actors.Select(actor => actor.ReferenceUuid).ToHashSet();
        actors
            .SelectMany(actor => actor.Relationships)
            .Should()
            .Contain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.DistinctActors
                && relationship.ReferenceUuids.ToHashSet().SetEquals(references)
            );
        foreach (var actor in actors)
        {
            AssertCitations(raw, actor);
        }
    }

    [TestCase("alice@example.test")]
    [TestCase("alice at example dot test")]
    [TestCase("alice (at) example (dot) test")]
    public void AttachedContactPreservesItsLiteralAndNormalizesOnlyTheLookupKey(string literal)
    {
        var raw = $"Alice Vale ({literal})";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        var contact = actor.Contacts.Should().ContainSingle().Which;
        contact.Literal.Should().Be(literal);
        contact.ContactType.Should().Be("email");
        contact.LookupKey.Should().Be("alice@example.test");
        contact.Evidence.Should().Contain(evidence => evidence.Quote == literal);
        actor.Kind.Should().BeNull();
        AssertCitations(raw, actor);
    }

    [TestCase("contact Bob Reed: bob@example.test", "unattributed_contact", "bob@example.test")]
    [TestCase(
        "Bob Reed profile: https://archive.org/details/@bob",
        "unattributed_profile",
        "https://archive.org/details/@bob"
    )]
    public void ParentheticalHintNamingAnotherActorIsNotAssignedToTheCreditedActor(
        string attachment,
        string reason,
        string literal
    )
    {
        var raw = $"Alice Vale ({attachment})";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Contacts.Should().BeEmpty();
        actor.Profiles.Should().BeEmpty();
        actor.Aliases.Should().BeEmpty();
        actor.Kind.Should().BeNull();
        actor
            .Unresolved.Should()
            .Contain(issue =>
                issue.Reason == reason
                && issue.Evidence.Any(evidence => evidence.Quote.Contains(literal))
            );
        actor
            .Unresolved.Should()
            .Contain(issue =>
                issue.Reason == "ambiguous_actor_attachment"
                && issue.Evidence.Any(evidence => evidence.Quote.Contains(attachment))
            );
        AssertCitations(raw, actor);
    }

    [Test]
    public void OrdinaryPublishedAndDownloadAtPhrasesDoNotInventEmailContacts()
    {
        const string raw =
            "Recorded by Alice Vale. Published at example.test; download at mirror.example.test";

        var result = Extract("description", raw);

        var actor = Facts(result.Credits.Should().ContainSingle().Which);
        actor.Contacts.Should().BeEmpty();
        actor.Unresolved.Should().NotContain(issue => issue.Reason == "unattributed_contact");
        AssertCitations(raw, actor);
    }

    [TestCase("Alice Vale (https://archive.org/details/@night_owl)", "archive.org", "night_owl")]
    [TestCase("<a href=\"https://github.com/night-owl\">Alice Vale</a>", "github.com", "night-owl")]
    public void AttachedProfileRemainsAQualifiedSourceAssertionWithoutPersonKind(
        string raw,
        string provider,
        string lookupKey
    )
    {
        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        var profile = actor.Profiles.Should().ContainSingle().Which;
        profile.Provider.Should().Be(provider);
        profile.Namespace.Should().Be("account");
        profile.LookupKey.Should().Be(lookupKey);
        profile.Relationship.Should().Be(ActorProfileRelationship.AttributedProfile);
        actor.Kind.Should().BeNull();
        AssertCitations(raw, actor);
    }

    [Test]
    public void BareTrailingProfileUrlRemainsUnattributed()
    {
        const string raw = "Alice Vale https://github.com/bob";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Profiles.Should().BeEmpty();
        actor
            .Unresolved.Should()
            .Contain(issue =>
                issue.Reason == "unattributed_profile"
                && issue.Evidence.Any(evidence => evidence.Quote == "https://github.com/bob")
            );
        AssertCitations(raw, actor);
    }

    [Test]
    public void EmailShapedUrlQueryDoesNotBecomeAnActorContact()
    {
        const string raw = "Alice Vale (https://example.test/?ref=admin@example.test)";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Contacts.Should().BeEmpty();
        actor.Unresolved.Should().NotContain(issue => issue.Reason == "unattributed_contact");
        actor
            .Unresolved.Should()
            .Contain(issue =>
                issue.Reason == "unknown_profile_namespace"
                && issue.Evidence.Any(evidence =>
                    evidence.Quote.Contains("https://example.test/?ref=admin@example.test")
                )
            );
        AssertCitations(raw, actor);
    }

    [Test]
    public void SharedContactAndProfileAfterTwoActorsRemainUnattributed()
    {
        const string raw =
            "Recorded by Alice Vale and Bob Reed; contact: shared@example.test; profile: https://archive.org/details/@shared";

        var result = Extract("description", raw);

        result.Credits.Select(credit => credit.Name).Should().Equal("Alice Vale", "Bob Reed");
        var actors = result.Credits.Select(Facts).ToArray();
        var references = actors.Select(actor => actor.ReferenceUuid).ToHashSet();
        foreach (var actor in actors)
        {
            actor
                .Contacts.Should()
                .BeEmpty("the source does not attribute the shared address to either actor");
            actor
                .Profiles.Should()
                .BeEmpty("the source does not attribute the shared profile to either actor");
            AssertCitations(raw, actor);
        }
        var unresolved = actors.SelectMany(actor => actor.Unresolved).ToArray();
        unresolved
            .Should()
            .Contain(issue =>
                issue.Reason == "unattributed_contact"
                && issue.CandidateReferenceUuids.ToHashSet().SetEquals(references)
            );
        unresolved
            .Should()
            .Contain(issue =>
                issue.Reason == "unattributed_profile"
                && issue.CandidateReferenceUuids.ToHashSet().SetEquals(references)
            );
    }

    [Test]
    public void CrewWebsiteDoesNotTurnACreditedProjectIntoAnIndividual()
    {
        const string raw = "Lantern Crew (https://lantern.example.test)";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Lantern Crew");
        var actor = Facts(credit);
        actor.Kind.Should().BeNull();
        actor
            .Profiles.Should()
            .Contain(profile =>
                profile.Relationship == ActorProfileRelationship.CreditedProjectProfile
            );
        actor
            .Context.Should()
            .NotContain(context =>
                context.ContextKind == ActorContextKind.ExplicitPersonDescription
            );
        AssertCitations(raw, actor);
    }

    [TestCase("an individual", "person", ActorContextKind.ExplicitPersonDescription)]
    [TestCase("a person", "person", ActorContextKind.ExplicitPersonDescription)]
    [TestCase("a taping collective", "group", ActorContextKind.ExplicitGroupDescription)]
    [TestCase("a recording group", "group", ActorContextKind.ExplicitGroupDescription)]
    public void OnlyAnExplicitActorDescriptionSuppliesKind(
        string statement,
        string kind,
        ActorContextKind contextKind
    )
    {
        var raw = $"Alice Vale ({statement})";

        var result = Extract("taper", raw);

        var actor = Facts(result.Credits.Should().ContainSingle().Which);
        actor.Kind.Should().NotBeNull();
        actor.Kind!.Kind.Should().Be(kind);
        actor.Kind.Evidence.Should().Contain(evidence => evidence.Quote.Contains(statement));
        actor.Context.Should().Contain(context => context.ContextKind == contextKind);
        AssertCitations(raw, actor);
    }

    [Test]
    public void ConflictingExplicitKindsRemainUnresolvedInsteadOfChoosingTheLastDescription()
    {
        const string raw = "Alice Vale (a person) (a recording group)";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Alice Vale");
        var actor = Facts(credit);
        actor.Kind.Should().BeNull();
        actor
            .Context.Should()
            .Contain(context => context.ContextKind == ActorContextKind.ExplicitPersonDescription);
        actor
            .Context.Should()
            .Contain(context => context.ContextKind == ActorContextKind.ExplicitGroupDescription);
        var conflictEvidence = actor
            .Unresolved.Where(issue => issue.Reason == "conflicting_actor_kind")
            .SelectMany(issue => issue.Evidence)
            .ToArray();
        conflictEvidence.Should().Contain(evidence => evidence.Quote.Contains("a person"));
        conflictEvidence.Should().Contain(evidence => evidence.Quote.Contains("a recording group"));
        AssertCitations(raw, actor);
    }

    [Test]
    public void LiteralEmptyHandleIsPreservedAsACreditRatherThanMissingData()
    {
        const string raw = "empty";

        var result = Extract("taper", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("empty");
        var actor = Facts(credit);
        actor.Anchor.Quote.Should().Be("empty");
        actor.Kind.Should().BeNull();
        result.Unresolved.Should().BeEmpty();
        AssertCitations(raw, actor);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RepeatedSpellingInSeparateClausesOrFieldsDoesNotProveCoReference(
        bool separateFields
    )
    {
        var fields = separateFields
            ? new Dictionary<string, string?>
            {
                ["taper"] = "Alice Vale",
                ["transferrer"] = "Alice Vale",
            }
            : new Dictionary<string, string?>
            {
                ["description"] = "Recorded by Alice Vale. Transferred by Alice Vale",
            };

        var result = extractor.Extract(fields);

        result.Credits.Should().HaveCount(2);
        var actors = result.Credits.Select(Facts).ToArray();
        actors.Select(actor => actor.ActorUuid).Distinct().Should().HaveCount(2);
        actors.Select(actor => actor.ReferenceUuid).Distinct().Should().HaveCount(2);
        actors
            .SelectMany(actor => actor.Relationships)
            .Should()
            .NotContain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.JointRoles
                || relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
            );
        foreach (var actor in actors)
        {
            foreach (var evidence in Evidence(actor))
            {
                AssertCitation(fields[evidence.Field]!, evidence);
            }
        }
    }

    [Test]
    public void RepeatedSameRoleClausesRetainTheirIndependentSourceReferences()
    {
        const string raw = "Recorded by Alice Vale. Recorded by Alice Vale";

        var result = Extract("description", raw);

        result.Credits.Should().HaveCount(2);
        result
            .Credits.Should()
            .OnlyContain(credit =>
                credit.Name == "Alice Vale" && credit.Role == RecordingCreditRole.Taper
            );
        var actors = result.Credits.Select(Facts).ToArray();
        actors.Select(actor => actor.ReferenceUuid).Distinct().Should().HaveCount(2);
        actors.Select(actor => actor.Anchor.StartCodePoint).Distinct().Should().HaveCount(2);
        actors
            .SelectMany(actor => actor.Relationships)
            .Should()
            .NotContain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.JointRoles
                || relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
            );
        foreach (var actor in actors)
        {
            AssertCitations(raw, actor);
        }
    }

    [Test]
    public void HtmlAndEntitiesKeepExactRawCodePointCitationsAfterAnEmoji()
    {
        const string raw =
            "🎵<div>Recorded by Léon&nbsp;March aka <b>Night Owl</b> (leon&#64;example.test)</div>";

        var result = Extract("description", raw);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Léon March");
        var actor = Facts(credit);
        actor.Anchor.Quote.Should().Be("Léon&nbsp;March");
        actor.Aliases.Should().Contain(alias => alias.Wording == "Night Owl");
        actor
            .Contacts.Should()
            .Contain(contact =>
                contact.LookupKey == "leon@example.test"
                && contact.Evidence.Any(evidence => evidence.Quote.Contains("&#64;"))
            );
        var rawStart = raw.IndexOf("Léon", StringComparison.Ordinal);
        actor.Anchor.StartCodePoint.Should().Be(raw[..rawStart].EnumerateRunes().Count());
        AssertCitations(raw, actor);
        AssertCitation(raw, credit.Evidence);
    }

    [Test]
    public void OlderSerializedMentionsKeepAbsentActorFactsAbsentOnRoundTrip()
    {
        const string json = """
            {"Name":"empty","Role":0,"Evidence":{"Field":"taper","JsonPointer":"/taper",
            "Quote":"empty","StartCodePoint":0,"EndCodePoint":5},"OriginalRole":null}
            """;

        var mention = JsonSerializer.Deserialize<CreditMention>(json)!;

        mention.Name.Should().Be("empty");
        mention.ActorFacts.Should().BeNull();
        using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(mention));
        roundTrip
            .RootElement.TryGetProperty(nameof(CreditMention.ActorFacts), out _)
            .Should()
            .BeFalse();
    }

    private RecordingCreditExtraction Extract(string field, string raw) =>
        extractor.Extract(new Dictionary<string, string?> { [field] = raw });

    private static CreditedActorFacts<NormalizationTextEvidence> Facts(CreditMention mention)
    {
        mention
            .ActorFacts.Should()
            .NotBeNull("an extracted actor must retain its source-scoped evidence");
        return mention.ActorFacts!;
    }

    private static void AssertCitations(
        string raw,
        CreditedActorFacts<NormalizationTextEvidence> actor
    )
    {
        foreach (var evidence in Evidence(actor))
        {
            AssertCitation(raw, evidence);
        }
    }

    private static void AssertCitation(string raw, NormalizationTextEvidence evidence)
    {
        var runes = raw.EnumerateRunes().ToArray();
        evidence.StartCodePoint.Should().BeGreaterThanOrEqualTo(0);
        evidence.EndCodePoint.Should().BeInRange(evidence.StartCodePoint, runes.Length);
        string.Concat(
                runes[evidence.StartCodePoint..evidence.EndCodePoint]
                    .Select(rune => rune.ToString())
            )
            .Should()
            .Be(evidence.Quote, "citations must select the original raw text");
        evidence
            .JsonPointer.Should()
            .Be("/" + evidence.Field.Replace("~", "~0").Replace("/", "~1"));
    }

    private static IEnumerable<NormalizationTextEvidence> Evidence(
        CreditedActorFacts<NormalizationTextEvidence> actor
    )
    {
        yield return actor.Anchor;
        var assertions = actor
            .Aliases.Select(alias => alias.Evidence)
            .Concat(actor.Profiles.Select(profile => profile.Evidence))
            .Concat(actor.Contacts.Select(contact => contact.Evidence))
            .Concat(actor.Context.Select(context => context.Evidence))
            .Concat(actor.Relationships.Select(relationship => relationship.Evidence))
            .Concat(actor.Unresolved.Select(issue => issue.Evidence));
        if (actor.Kind is { } kind)
        {
            assertions = assertions.Append(kind.Evidence);
        }
        foreach (var assertion in assertions)
        {
            assertion.Should().NotBeEmpty("actor facts need supporting source text");
            foreach (var evidence in assertion)
            {
                yield return evidence;
            }
        }
        foreach (var evidence in actor.Coverage.SelectMany(coverage => coverage.Fields))
        {
            yield return evidence;
        }
    }
}
