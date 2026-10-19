using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
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
    private const string CreditV5Envelope =
        "recording-source-actor-envelope-v2/recording-extraction-v4/"
        + "retained-recording-credits-v2/recording-credits-v5/recording-actor-evidence-v1/"
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
        yield return Profile(
            "provider-observations-v8",
            "source-actor-aliases-v4",
            SourceActorAliasGrammar.Current,
            CreditV5Envelope
        );
    }

    private static TestCaseData Profile(
        string decoder,
        string alias,
        SourceActorAliasGrammar grammar,
        string envelope = CreditV4Envelope
    ) =>
        new TestCaseData(
            "catalog-recording-processing-v4/legacy-recording-snapshot-v2/"
                + decoder
                + "/"
                + envelope
                + "retained-source-actor-aliases-v1/"
                + alias
                + "/recording-actor-evidence-v1/retained-source-actor-alias-mapping-v1",
            alias + "/recording-actor-evidence-v1",
            (int)grammar
        );

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

    [TestCaseSource(nameof(HistoricalProfiles))]
    public void HistoricalReadReplaysCompleteOriginalActorScopesButCannotAdmitThemAsCurrent(
        string processingVersion,
        string aliasVersion,
        int grammar
    )
    {
        var context = OriginalActorAccount((SourceActorAliasGrammar)grammar);
        var authenticator = new SourceActorResolutionStore.OriginalInputAuthenticator(
            processingVersion,
            new Dictionary<Guid, NormalizationInputRevision>
            {
                [context.Input.Uuid] = context.Input,
            },
            true
        );

        var actual = authenticator.Authenticate(context.Prepared, context.Output, default);

        actual
            .Scopes.Should()
            .HaveCount(2, "recording and file prose keep separate original scopes");
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(
                    actual.Scopes.Select(scope => scope.OriginalScope)
                ),
                JsonSerializer.SerializeToElement(context.Output.SourceActorAliases)
            )
            .Should()
            .BeTrue("the original assertion UUIDs, aliases, citations and coverage must survive");
        actual
            .Scopes.Should()
            .OnlyContain(scope =>
                scope.OriginalScope.Extraction.Assertions.Count > 0
                && scope.OriginalScope.ProducerVersion.Contains(
                    aliasVersion,
                    StringComparison.Ordinal
                )
            );

        var admission = new SourceActorResolutionStore.OriginalInputAuthenticator(
            processingVersion,
            new Dictionary<Guid, NormalizationInputRevision>
            {
                [context.Input.Uuid] = context.Input,
            },
            false
        );
        Action admit = () => admission.Authenticate(context.Prepared, context.Output, default);
        admit.Should().Throw<ArgumentException>();
    }

    [Test]
    public void TitleOnlyCompatibilityStillRefusesAnAlteredOriginalFileInventory()
    {
        var context = OriginalActorAccount(SourceActorAliasGrammar.Current);
        var authenticator = new SourceActorResolutionStore.OriginalInputAuthenticator(
            SourceActorResolutionStore.AliasV4TitleV1ProcessingVersion,
            new Dictionary<Guid, NormalizationInputRevision>
            {
                [context.Input.Uuid] = context.Input,
            },
            true
        );
        var missingFile = context.Output with
        {
            SourceActorAliases = context.Output.SourceActorAliases!.Take(1).ToArray(),
        };

        Action replay = () => authenticator.Authenticate(context.Prepared, missingFile, default);
        replay.Should().Throw<ArgumentException>();
    }

    [Test]
    public void TitleOnlyCompatibilityKeepsCurrentActorIdsAndOriginalScopes()
    {
        var context = OriginalActorAccount(SourceActorAliasGrammar.Current);
        var authenticator = new SourceActorResolutionStore.OriginalInputAuthenticator(
            NormalizationRecordingProcessor.Version,
            new Dictionary<Guid, NormalizationInputRevision>
            {
                [context.Input.Uuid] = context.Input,
            },
            false
        );

        var current = authenticator.Authenticate(context.Prepared, context.Output, default);

        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(
                    current.Scopes.Select(scope => scope.OriginalScope)
                ),
                JsonSerializer.SerializeToElement(context.Output.SourceActorAliases)
            )
            .Should()
            .BeTrue();
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
    [TestCase("title")]
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
            "title" => version.Replace("track-title-v1", "track-title-v0"),
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
                && version.Contains("source-actor-aliases-v3/", StringComparison.Ordinal)
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

    private static OriginalAccount OriginalActorAccount(SourceActorAliasGrammar grammar)
    {
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.Parse("7a4d282b-7b8c-4d77-ad91-d173fd16abef"),
            Resource = new("archive.org", "item_metadata", "original-title-profile"),
            Payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    metadata = new
                    {
                        identifier = "original-title-profile",
                        description = "<div>The band:\nZoë Vale AKA Lark - guitar</div>",
                    },
                    files = new[]
                    {
                        new
                        {
                            name = "original.flac",
                            format = "Flac",
                            title = "Song [1]",
                            description = "The band:\nPat Myers AKA DATPAT - guitar",
                        },
                    },
                }
            ),
            MediaType = "application/json",
            CaptureVersion = "archive-item-response-v1",
        };
        var decoded = new NormalizationProviderDecoder().Decode(input);
        var recording = decoded.Recordings.Single();
        var binding = new NormalizationRecordingBindingSnapshot(
            input.Uuid,
            input.Resource,
            new(
                NormalizationCatalogBindingStatus.Bound,
                new(Guid.NewGuid(), Guid.NewGuid(), null, null, ["missing_show"]),
                "bound"
            ),
            recording
                .Files.Select(file => new NormalizationFileBinding(
                    file,
                    new(NormalizationCatalogBindingStatus.Missing, null, "unbound_file")
                ))
                .ToArray(),
            [],
            []
        );
        using var document = NormalizationRetainedDocument.Open(input);
        var scopes = new NormalizationRecordingSourceActorAliasBuilder(grammar).Build(
            input,
            document,
            recording,
            default
        );
        return new(input, new(input.Resource, 0, binding), new(decoded.Issues, scopes));
    }

    private sealed record OriginalAccount(
        NormalizationInputRevision Input,
        NormalizationRecordingPreparedInput Prepared,
        SourceActorExtractionOutput Output
    );
}
