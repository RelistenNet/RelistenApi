using System.Text.Json;
using FluentAssertions;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestSourceActorProducerCompatibility
{
    private const string CreditV4Envelope =
        "recording-source-actor-envelope-v2/recording-extraction-v4/"
        + "retained-recording-credits-v2/recording-credits-v4/recording-actor-evidence-v1/"
        + "retained-actor-facts-v1/track-title-v1/local-selected-account-v1/";
    private const string ResolutionComponents =
        "/people-candidate-facts-v5/people-actor-retrieval-v3/people-actor-projection-v1/"
        + "people-actor-context-linear-v3/people-actor-projection-v1/people-actor-context-features-v1/"
        + "source-actor-review-correspondence-v1/source-actor-resolution-decisions-v1/"
        + "people-source-actor-alias-projection-v1/";
    private const string FeatureSuffix = "/people-actor-context-features-v1";
    private const string ApplicationPrefix = "source-actor-alias-publication-v1/";
    private const string ApplicationSuffix = "/complete-person-map-v1/component-review-v1";

    private static IEnumerable<TestCaseData> HistoricalProfiles()
    {
        yield return Profile(
            "provider-observations-v6",
            "source-actor-aliases-v2",
            SourceActorAliasGrammar.LegacyV2
        );
        yield return Profile(
            "provider-observations-v7",
            "source-actor-aliases-v2",
            SourceActorAliasGrammar.LegacyV2
        );
        yield return Profile(
            "provider-observations-v7",
            "source-actor-aliases-v3",
            SourceActorAliasGrammar.LegacyV3
        );
        yield return Profile(
            "provider-observations-v8",
            "source-actor-aliases-v3",
            SourceActorAliasGrammar.LegacyV3
        );
    }

    private static TestCaseData Profile(
        string decoder,
        string alias,
        SourceActorAliasGrammar grammar
    ) =>
        new TestCaseData(
            "catalog-recording-processing-v4/legacy-recording-snapshot-v2/"
                + decoder
                + "/"
                + CreditV4Envelope
                + "retained-source-actor-aliases-v1/"
                + alias
                + "/recording-actor-evidence-v1/retained-source-actor-alias-mapping-v1",
            alias + "/recording-actor-evidence-v1",
            (int)grammar
        ).SetName("ExactHistoricalProducer_" + decoder + "_" + alias);

    [TestCaseSource(nameof(HistoricalProfiles))]
    public void ExactHistoricalProducersRetainReadAuthorityWithoutCurrentAdmission(
        string processingVersion,
        string aliasVersion,
        int grammar
    )
    {
        SourceActorResolutionStore
            .RequireOriginalAliasGrammar(processingVersion, true)
            .Should()
            .Be((SourceActorAliasGrammar)grammar);
        Action currentAdmission = () =>
            SourceActorResolutionStore.RequireOriginalAliasGrammar(processingVersion, false);
        currentAdmission.Should().Throw<ArgumentException>();

        var proposal =
            "source-actor-resolution-proposals-v1/"
            + processingVersion
            + ResolutionComponents
            + aliasVersion
            + FeatureSuffix;
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedProposal(proposal)
            .Should()
            .BeTrue();
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedApplication(ApplicationPrefix + proposal + ApplicationSuffix)
            .Should()
            .BeTrue();
    }

    [Test]
    public void AcceptedReadProfilesCoverExactlyTheKnownPublishedProducerTriples()
    {
        var expected = HistoricalProfiles()
            .Select(profile =>
                ApplicationPrefix
                + "source-actor-resolution-proposals-v1/"
                + (string)profile.Arguments[0]!
                + ResolutionComponents
                + (string)profile.Arguments[1]!
                + FeatureSuffix
                + ApplicationSuffix
            );
        SourceActorAliasApplicationService
            .HistoricalAcceptedApplicationReadVersions()
            .Should()
            .BeEquivalentTo(expected);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CurrentProducerStillUsesCurrentGrammar(bool historicalRead)
    {
        SourceActorResolutionStore
            .RequireOriginalAliasGrammar(NormalizationRecordingProcessor.Version, historicalRead)
            .Should()
            .Be(SourceActorAliasGrammar.Current);
    }

    [Test]
    public void GenuinePublishedV6ProposalRetainsItsHistoricalReadProfile()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "source-actor-v1-public-fixture.json"
        );
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var original = fixture
            .RootElement.GetProperty("Published")
            .GetProperty("Attempt")
            .GetProperty("ProcessingVersion")
            .GetString()!;

        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedProposal(original)
            .Should()
            .BeTrue();
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedApplication(ApplicationPrefix + original + ApplicationSuffix)
            .Should()
            .BeTrue();
        // A historical accepted-read grant does not make this an admissible current proposal.
        original.Should().NotBe(SourceActorResolutionService.Version);
    }

    [TestCase("decoder")]
    [TestCase("credit")]
    [TestCase("alias")]
    [TestCase("mapping")]
    [TestCase("capture")]
    public void UnknownProducerCombinationsCannotBorrowHistoricalProof(string changedComponent)
    {
        var version = SourceActorResolutionStore.LegacyAliasV3DecoderV8ProcessingVersion;
        var changed = changedComponent switch
        {
            "decoder" => version.Replace("provider-observations-v8", "provider-observations-v9"),
            "credit" => version.Replace("recording-credits-v4/", "recording-credits-v5/"),
            "alias" => version.Replace("source-actor-aliases-v3/", "source-actor-aliases-v2/"),
            "mapping" => version.Replace(
                "retained-source-actor-alias-mapping-v1",
                "retained-source-actor-alias-mapping-v2"
            ),
            "capture" => version.Replace(
                "legacy-recording-snapshot-v2",
                "legacy-recording-snapshot-v1"
            ),
            _ => throw new ArgumentException("Unknown producer mutation."),
        };
        Action replay = () => SourceActorResolutionStore.RequireOriginalAliasGrammar(changed, true);
        replay.Should().Throw<ArgumentException>();
        var proposal =
            "source-actor-resolution-proposals-v1/"
            + changed
            + ResolutionComponents
            + SourceActorAliasExtractor.LegacyV3Version
            + FeatureSuffix;
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedProposal(proposal)
            .Should()
            .BeFalse();
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedApplication(ApplicationPrefix + proposal + ApplicationSuffix)
            .Should()
            .BeFalse();
    }

    [TestCase("facts")]
    [TestCase("retrieval")]
    [TestCase("projection")]
    [TestCase("scorer")]
    [TestCase("correspondence")]
    [TestCase("decision")]
    [TestCase("feature")]
    public void HistoricalAcceptedReadsStillRequireTheirExactResolverAndApplicationComponents(
        string component
    )
    {
        var application = SourceActorAliasApplicationService
            .HistoricalAcceptedApplicationReadVersions()
            .Single(version =>
                version.Contains("provider-observations-v8/", StringComparison.Ordinal)
            );
        var token = component switch
        {
            "facts" => "people-candidate-facts-v5",
            "retrieval" => "people-actor-retrieval-v3",
            "projection" => "people-actor-projection-v1",
            "scorer" => "people-actor-context-linear-v3",
            "correspondence" => "source-actor-review-correspondence-v1",
            "decision" => "source-actor-resolution-decisions-v1",
            "feature" => "people-actor-context-features-v1",
            _ => throw new ArgumentException("Unknown resolver mutation."),
        };
        var changed = application.Replace(token, token + "-unknown");
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedApplication(changed)
            .Should()
            .BeFalse();
        var proposal = changed[ApplicationPrefix.Length..^ApplicationSuffix.Length];
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedProposal(proposal)
            .Should()
            .BeFalse();
        SourceActorAliasApplicationService
            .SupportsHistoricalAcceptedApplication(application + "/unknown")
            .Should()
            .BeFalse();
    }
}
