using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationExpertKnowledgeResolver
{
    private static readonly Guid Artist = Guid.Parse("dc265a12-e8ea-4f82-9656-66ea00000001");
    private static readonly ExpertKnowledgeTarget Site = new("venues", "catalogue", "venue:one");
    private static readonly ExpertKnowledgeTarget Room = new("venues", "catalogue", "venue:two");

    [Test]
    public void ExactAccountCorrectionOverridesBroadAliasIncludingItsNegativeException()
    {
        var broad = Rule(new("catalogue", Literal: "MSG", Locality: "New York"), Site);
        var exact = Rule(new("catalogue", AccountStableKey: "account:room"), Room);
        var negativeAlias = Rule(broad.Definition.Selector, Room, "rejected");
        var result = Resolver(broad, exact, negativeAlias).Evaluate(Observation("account:room"));

        result.Outcome.Should().Be("reviewed_correspondence");
        result.Target.Should().Be(Room);
        result
            .Target!.CanonicalUuid.Should()
            .BeNull(
                "a frozen venue key is a reviewed correspondence, not a minted canonical identity"
            );
        result.AppliedRules.Select(rule => rule.RuleUuid).Should().Equal(exact.RuleUuid);
    }

    [Test]
    public void NamedTargetRejectionLeavesAnotherPositiveCandidateUsable()
    {
        var selector = new ExpertKnowledgeSelector(
            "catalogue",
            Literal: "MSG",
            Locality: "New York"
        );
        var result = Resolver(
                Rule(selector, Site),
                Rule(selector, Room),
                Rule(selector, Site, "rejected")
            )
            .Evaluate(Observation("account:one"));

        result.Target.Should().Be(Room);
        result.ExcludedTargets.Should().Equal(Site);
        result.Outcome.Should().Be("reviewed_correspondence");
    }

    [Test]
    public void OptionalCanonicalMetadataCannotBypassFrozenTargetRejectionOrCreateFalseConflict()
    {
        var selector = new ExpertKnowledgeSelector(
            "catalogue",
            Literal: "MSG",
            Locality: "New York"
        );
        var attached = Site with { CanonicalUuid = Guid.NewGuid() };
        var result = Resolver(Rule(selector, attached), Rule(selector, Site, "rejected"))
            .Evaluate(Observation("account:one"));
        result.Outcome.Should().Be("excluded_targets");
        result.Target.Should().BeNull();
        Resolver(Rule(selector, Site), Rule(selector, attached))
            .Evaluate(Observation("account:one"))
            .Target.Should()
            .Be(attached);
        var contradictory = attached with { CanonicalUuid = Guid.NewGuid() };
        Resolver(Rule(selector, attached), Rule(selector, contradictory))
            .Evaluate(Observation("account:one"))
            .Outcome.Should()
            .Be("conflict");
    }

    [Test]
    public void ConflictingAliasesDoNotChooseTheNewestRule()
    {
        var selector = new ExpertKnowledgeSelector(
            "catalogue",
            Literal: "MSG",
            Locality: "New York"
        );
        var older = Rule(selector, Site) with { Revision = 7 };
        var newer = Rule(selector, Room) with { Revision = 99 };
        var result = Resolver(older, newer).Evaluate(Observation("account:one"));

        result.Outcome.Should().Be("conflict");
        result.Target.Should().BeNull();
        result.ConflictingTargets.Should().BeEquivalentTo(new[] { Site, Room });
    }

    [TestCase("artist")]
    [TestCase("provider")]
    [TestCase("locality")]
    [TestCase("missing_date")]
    [TestCase("outside_date")]
    [TestCase("literal")]
    [TestCase("namespace")]
    public void ReusableAliasesRequireEveryExactContextAndInclusiveDate(string missing)
    {
        var selector = new ExpertKnowledgeSelector(
            "catalogue",
            Literal: "MSG",
            ArtistUuid: Artist,
            Provider: "archive.org",
            Locality: "New York",
            From: new(1968, 2, 11),
            Through: new(2026, 1, 1)
        );
        var resolver = Resolver(Rule(selector, Site));
        var input = Observation("account:one") with
        {
            ArtistUuid = Artist,
            Provider = "archive.org",
            Date = new(1968, 2, 11),
        };
        resolver.Evaluate(input).Target.Should().Be(Site);
        input = missing switch
        {
            "artist" => input with { ArtistUuid = null },
            "provider" => input with { Provider = "local" },
            "locality" => input with { Locality = null },
            "missing_date" => input with { Date = null },
            "outside_date" => input with { Date = new(1968, 2, 10) },
            "literal" => input with { Literal = "msg" },
            _ => input with { Namespace = "native" },
        };
        resolver.Evaluate(input).Outcome.Should().Be("no_match");
    }

    [Test]
    public void WithdrawnRuleAndAccountFromAnotherNamespaceSupplyNoAuthority()
    {
        var exact = Rule(new("catalogue", AccountStableKey: "account:one"), Site);
        Resolver(exact)
            .Evaluate(Observation("account:one") with { Namespace = "native" })
            .Outcome.Should()
            .Be("no_match");
        Resolver(exact with { State = "retracted", Revision = 2 })
            .Evaluate(Observation("account:one"))
            .Outcome.Should()
            .Be("no_match");
    }

    [Test]
    public void SerializedSnapshotRebuildPreservesFutureInputEvaluationAndCannotBeRelabeled()
    {
        var rule = Rule(new("catalogue", Literal: "MSG", Locality: "New York"), Site);
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot([rule]);
        var restored = JsonSerializer.Deserialize<ExpertKnowledgeSnapshot>(
            JsonSerializer.Serialize(snapshot)
        )!;
        var future = new ExpertKnowledgeResolver(restored).Evaluate(Observation("new:recording"));
        future.Target.Should().Be(Site);
        future.SnapshotSha256.Should().Be(snapshot.Sha256);
        var changed = restored with
        {
            Rules = [rule with { Definition = rule.Definition with { Target = Room } }],
        };
        var load = () => new ExpertKnowledgeResolver(changed);
        load.Should().Throw<ArgumentException>();
    }

    [Test]
    public void RepertoireSourceCannotBecomeAGlobalCompositionAliasWithoutArtistScope()
    {
        var target = new ExpertKnowledgeTarget(
            "compositions",
            "composition_work",
            "musicbrainz.org/work/one"
        );
        var definition = Definition(
            new("repertoire", Literal: "Such a Night", Provider: "archive.org"),
            target
        );
        var validate = () => ExpertKnowledgeResolver.ValidateDefinition(definition);
        validate.Should().Throw<ArgumentException>();
        ExpertKnowledgeResolver.ValidateDefinition(
            definition with
            {
                Selector = definition.Selector with { ArtistUuid = Artist },
            }
        );
    }

    [Test]
    public void AliasTargetsNeverBecomeAdditionalSourceAliasesByTransitiveClosure()
    {
        var first = Rule(new("catalogue", Literal: "Original", Locality: "New York"), Site);
        var second = Rule(new("catalogue", Literal: "Other", Locality: "New York"), Room);
        Resolver(first, second)
            .Evaluate(Observation("account:one") with { Literal = Site.StableKey })
            .Outcome.Should()
            .Be("no_match");
    }

    private static ExpertKnowledgeObservation Observation(string key) =>
        new("venues", "catalogue", key, "MSG", Locality: "New York");

    private static ExpertKnowledgeResolver Resolver(params ExpertKnowledgeRule[] rules) =>
        new(ExpertKnowledgeResolver.CreateSnapshot(rules));

    private static ExpertKnowledgeRule Rule(
        ExpertKnowledgeSelector selector,
        ExpertKnowledgeTarget target,
        string state = "accepted"
    ) =>
        new(
            Guid.NewGuid(),
            1,
            state,
            Definition(selector, target),
            Guid.NewGuid(),
            DateTime.UtcNow
        );

    private static ExpertKnowledgeDefinition Definition(
        ExpertKnowledgeSelector selector,
        ExpertKnowledgeTarget target
    ) =>
        new(
            target.Domain,
            selector,
            target,
            [new("/retained/expert-input.json", 100, new string('a', 64))],
            "Exact retained source observation reviewed for this scoped correspondence."
        );
}
