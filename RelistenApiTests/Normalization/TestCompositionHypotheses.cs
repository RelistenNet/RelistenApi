using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Synthetic retained provider assertions exercise retrieval, never identity labels.</summary>
[TestFixture]
public sealed class TestCompositionHypotheses
{
    // These full fixture UUIDs extend the audited prefixes; their synthetic
    // payloads are test assertions, not captured MusicBrainz ground truth.
    private const string GarciaHunterWork = "8973193c-0000-4000-8000-000000000001";
    private const string HawkshawWork = "a54232ce-0000-4000-8000-000000000002";
    private const string FirstTomorrowWork = "47f9b6cc-0000-4000-8000-000000000003";
    private const string SecondTomorrowWork = "87c30f64-0000-4000-8000-000000000004";
    private const string Garcia = "11111111-0000-4000-8000-000000000001";
    private const string Hunter = "11111111-0000-4000-8000-000000000002";
    private const string Hawkshaw = "11111111-0000-4000-8000-000000000003";
    private static readonly Guid CatalogArtist = Guid.Parse("e9742fc4-ad7b-4a3f-8ac9-2c5631c5f39d");

    [Test]
    public void SameTitleCrazyFingersWorksKeepTheirSeparateQualifiedIdsAndWriterAssertions()
    {
        var first = WorkInput(
            GarciaHunterWork,
            "Crazy Fingers",
            [
                Writer("Jerry Garcia", Garcia, "Person", "composer"),
                Writer("Robert Hunter", Hunter, "Person", "lyricist"),
            ]
        );
        var second = WorkInput(
            HawkshawWork,
            "Crazy Fingers",
            [Writer("Hawkshaw Hawkins", Hawkshaw, "Person", "writer")]
        );
        var index = Index(first, second);
        var result = new CompositionHypothesisProjector(index).Project(Song("Crazy Fingers"));

        result.Outcome.Should().Be("hypotheses");
        result
            .Candidates.Select(candidate => candidate.WorkIdentity.Identity.ProviderKey)
            .Should()
            .BeEquivalentTo(GarciaHunterWork, HawkshawWork);
        var garciaHunter = index.Works.Single(work =>
            work.Identity.Identity.ProviderKey == GarciaHunterWork
        );
        garciaHunter
            .WriterRoles.Select(writer => writer.Artist!.Name!.Value)
            .Should()
            .Equal("Jerry Garcia", "Robert Hunter");
        garciaHunter
            .WriterRoles.Select(writer => writer.Role!.Value)
            .Should()
            .Equal("composer", "lyricist");
        index
            .Works.Single(work => work.Identity.Identity.ProviderKey == HawkshawWork)
            .WriterRoles.Single()
            .Artist!.Name!.Value.Should()
            .Be("Hawkshaw Hawkins");
        index
            .Works.Should()
            .OnlyContain(work => work.CompleteAuthorship == null && !work.ExternalUniverseComplete);
        AssertHypothesisOnly(result);
        AssertCitations(index.Works, first, second);
    }

    [Test]
    public void SameTitleSearchRivalsRemainSeparateEvenWhenBothScoresArePerfect()
    {
        var input = SearchInput(
            Work(
                FirstTomorrowWork,
                "Tomorrow Never Knows",
                [Writer("First retained writer", Garcia, "Person", "writer")]
            ),
            Work(
                SecondTomorrowWork,
                "Tomorrow Never Knows",
                [Writer("Other retained writer", Hunter, "Group", "writer")]
            )
        );
        var index = Index(input);
        var result = new CompositionHypothesisProjector(index).Project(
            Song("Tomorrow Never Knows")
        );

        result
            .Candidates.Select(candidate => candidate.WorkIdentity.Identity.ProviderKey)
            .Should()
            .BeEquivalentTo(FirstTomorrowWork, SecondTomorrowWork);
        index
            .Works.Should()
            .OnlyContain(work =>
                work.SearchAssertions.Count == 1 && work.LookupAssertions.Count == 0
            );
        index
            .Works.SelectMany(work => work.WriterRoles)
            .Should()
            .HaveCount(2)
            .And.OnlyContain(writer => writer.SourceForm == "work_search");
        AssertHypothesisOnly(result);
        AssertCitations(index.Works, input);
    }

    [Test]
    public void IndividualAndGroupWriterAssertionsDoNotCollapseDistinctHowSweetItIsWorks()
    {
        const string individualWork = "f031f100-0000-4000-8000-000000000001";
        const string groupWork = "f031f100-0000-4000-8000-000000000002";
        var individual = WorkInput(
            individualWork,
            "How Sweet It Is",
            [Writer("Retained individual", Garcia, "Person", "writer")]
        );
        var group = WorkInput(
            groupWork,
            "How Sweet It Is",
            [Writer("Retained writing group", Hunter, "Group", "writer")]
        );
        var index = Index(individual, group);
        var result = new CompositionHypothesisProjector(index).Project(Song("How Sweet It Is"));

        result.Candidates.Should().HaveCount(2);
        index
            .Works.SelectMany(work => work.WriterRoles)
            .Select(writer => writer.Artist!.Type!.Value)
            .Should()
            .BeEquivalentTo("Person", "Group");
        index
            .Works.SelectMany(work => work.WriterRoles)
            .Should()
            .OnlyContain(writer =>
                writer.Artist!.Identity.Identity.Provider == "musicbrainz.org"
                && writer.Artist.Identity.Identity.ResourceType == "artist"
            );
        AssertHypothesisOnly(result);
    }

    [Test]
    public void ExplicitAliasesRetrieveTheSameWorkButPartialAndFuzzyTitlesDoNot()
    {
        var input = WorkInput(GarciaHunterWork, "Crazy Fingers", aliases: ["Étranges doigts 🌙"]);
        var index = Index(input);

        index
            .Find("Étranges doigts 🌙")
            .Should()
            .ContainSingle()
            .Which.Identity.Identity.ProviderKey.Should()
            .Be(GarciaHunterWork);
        index.Find("Crazy").Should().BeEmpty();
        index.Find("Crazy Finger").Should().BeEmpty();
        index.Find("Etranges doigts 🌙").Should().BeEmpty();
        var result = new CompositionHypothesisProjector(index).Project(Song("Étranges doigts 🌙"));
        result
            .Candidates.Should()
            .ContainSingle()
            .Which.RetrievalBasis.Should()
            .Be("retained_explicit_work_alias");
        result.Candidates.Single().MatchingTitles.Single().Value.Should().Be("Étranges doigts 🌙");
        AssertCitations(index.Works, input);
    }

    [Test]
    public void DifferentCatalogArtistsRetrieveTheSameGlobalExternalWorkWithoutChangingSourceOwnership()
    {
        var index = Index(WorkInput(GarciaHunterWork, "Crazy Fingers"));
        var first = Song("Crazy Fingers");
        var secondArtist = Guid.Parse("0cfb793e-0660-4056-ab0e-44ca01f70cb1");
        var second = first with
        {
            Scope = "catalogue:other",
            ArtistUuid = secondArtist,
            Song = first.Song with
            {
                ArtistUuid = secondArtist,
                EntityKey = ShadowRepertoireAdapter.ProvisionalEntityKey(
                    secondArtist,
                    "Crazy Fingers"
                ),
            },
        };
        var projector = new CompositionHypothesisProjector(index);
        var firstResult = projector.Project(first);
        var secondResult = projector.Project(second);

        firstResult
            .Candidates.Single()
            .WorkIdentity.Should()
            .Be(secondResult.Candidates.Single().WorkIdentity);
        firstResult.Song.Should().Be(first);
        secondResult.Song.Should().Be(second);
        secondResult.Song.ArtistUuid.Should().Be(secondArtist);
        AssertHypothesisOnly(firstResult);
        AssertHypothesisOnly(secondResult);
    }

    [Test]
    public void MissingRetainedCandidatesAreExplicitWithoutClaimingExternalAbsence()
    {
        var input = SearchInput();
        var index = Index(input);
        var result = new CompositionHypothesisProjector(index).Project(Song("Uncaptured work"));

        result.Outcome.Should().Be("missing_retained_candidates");
        result.Candidates.Should().BeEmpty();
        var page = index.SearchPages.Should().ContainSingle().Subject;
        page.Origin.Item.Should().Be(input.Resource);
        page.Origin.Evidence.Should().Be(new NormalizedEvidenceReference(input.Uuid, ""));
        page.Count!.Value.Should().Be(0);
        page.Offset!.Value.Should().Be(0);
        page.Hits.Should().BeEmpty();
        page.Issues.Should().BeEmpty();
        AssertHypothesisOnly(result);
    }

    [TestCase("[]")]
    [TestCase("{\"error\":\"MusicBrainz is currently busy\"}")]
    public void MalformedOrErrorSearchResponseRemainsAnUnknownPageWithItsOriginalDiagnostics(
        string json
    )
    {
        var input = Input(
            "work-search",
            new string('c', 64),
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        var decoded = new NormalizationProviderDecoder().Decode(input);
        var index = Index(input);
        var page = index.SearchPages.Should().ContainSingle().Subject;

        index.Works.Should().BeEmpty();
        page.Origin.Item.Should().Be(input.Resource);
        page.Origin.Evidence.Should().Be(new NormalizedEvidenceReference(input.Uuid, ""));
        page.Origin.Scope.Should().Be(ProviderObservationScope.ResourceItem);
        page.Count.Should().BeNull();
        page.Offset.Should().BeNull();
        page.Hits.Should().BeNull();
        page.Issues.Should().NotBeEmpty().And.BeEquivalentTo(decoded.Issues);
        page.ExternalUniverseComplete.Should().BeFalse();
        using var retained = NormalizationRetainedDocument.Open(input);
        retained.ValidateEvidence(page.Origin.Evidence);
        foreach (var issue in page.Issues)
        {
            retained.ValidateEvidence(issue.Evidence);
        }
    }

    [Test]
    public void SingletonWithExternalWritersStillHasMissingCanonicalAuthorshipAndNoAcceptance()
    {
        var input = WorkInput(
            GarciaHunterWork,
            "Crazy Fingers",
            [Writer("Jerry Garcia", Garcia, "Person", "composer")]
        );
        var index = Index(input);
        var song = Song("Crazy Fingers", Guid.Parse(Garcia));
        var result = new CompositionHypothesisProjector(index).Project(song);

        result.Outcome.Should().Be("hypotheses");
        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate
            .Comparison.Features.Single(feature => feature.Name == "authorship")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Missing);
        index.Works.Single().WriterRoles.Should().ContainSingle();
        index.Works.Single().CompleteAuthorship.Should().BeNull();
        AssertHypothesisOnly(result);
    }

    [Test]
    public void UnicodeTitlesAliasesAndWriterNamesKeepTheirOriginalInputCoordinates()
    {
        var input = WorkInput(
            GarciaHunterWork,
            "Été 🌙",
            [Writer("Zoë 🎹", Garcia, "Person", "composer")],
            ["Été alternatif 🌙"]
        );
        var index = Index(input);
        var work = index.Find("Été 🌙").Should().ContainSingle().Subject;

        work.Titles.Single()
            .Evidence.Should()
            .Be(new NormalizedEvidenceReference(input.Uuid, "/title"));
        work.Aliases.Single().Name!.Evidence.JsonPointer.Should().Be("/aliases/0/name");
        work.WriterRoles.Single()
            .Artist!.Name!.Evidence.JsonPointer.Should()
            .Be("/relations/0/artist/name");
        AssertCitations(index.Works, input);
    }

    [Test]
    public void ExactDuplicateInputIsANoopButConflictingBytesForTheSameRevisionRefuseCompletion()
    {
        var input = WorkInput(GarciaHunterWork, "Crazy Fingers");
        var replay = Input("work", GarciaHunterWork, input.Payload, input.Uuid);
        var index = new RetainedCompositionCandidateIndex();
        index.Observe(input);
        index.Observe(replay);
        index.CompleteObservation();
        index.Works.Should().ContainSingle().Which.LookupAssertions.Should().ContainSingle();

        var conflicting = new RetainedCompositionCandidateIndex();
        conflicting.Observe(input);
        var changed = Input(
            "work",
            GarciaHunterWork,
            JsonSerializer.SerializeToUtf8Bytes(Work(GarciaHunterWork, "Different title")),
            input.Uuid
        );
        var observe = () => conflicting.Observe(changed);
        observe.Should().Throw<InvalidDataException>();
        var complete = () => conflicting.CompleteObservation();
        complete.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RecordingPerformanceEdgesRetainExternalIdsAndDoNotTurnPerformersIntoComposers()
    {
        const string recordingId = "76d91022-0000-4000-8000-000000000001";
        var recording = Input(
            "recording",
            recordingId,
            JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object?>
                {
                    ["id"] = recordingId,
                    ["title"] = "Crazy Fingers",
                    ["artist-credit"] = new[]
                    {
                        new
                        {
                            name = "Retained performer",
                            artist = new
                            {
                                id = Garcia,
                                name = "Retained performer",
                                type = "Person",
                            },
                        },
                    },
                    ["relations"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "performance",
                            ["direction"] = "forward",
                            ["target-type"] = "work",
                            ["work"] = new { id = GarciaHunterWork, title = "Crazy Fingers" },
                        },
                    },
                }
            )
        );
        var lookup = WorkInput(GarciaHunterWork, "Crazy Fingers");
        var index = Index(recording, lookup);
        var work = index.Find("Crazy Fingers").Should().ContainSingle().Subject;

        var edge = work.RecordingWorkRelationships.Should().ContainSingle().Subject;
        edge.Recording.Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "recording", recordingId));
        edge.Work.Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "work", GarciaHunterWork));
        edge.Relationship.Evidence.Should()
            .Be(new NormalizedEvidenceReference(recording.Uuid, "/relations/0"));
        var originalRecording = work.RecordingAssertions.Should().ContainSingle().Subject;
        originalRecording.Recording.Identity.Identity.Should().Be(recording.Resource);
        originalRecording
            .Origin.Evidence.Should()
            .Be(new NormalizedEvidenceReference(recording.Uuid, ""));
        var performer = originalRecording.ArtistCredits.Should().ContainSingle().Subject;
        performer.Name!.Value.Should().Be("Retained performer");
        performer
            .Artist!.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "artist", Garcia));
        performer.Artist.Type!.Value.Should().Be("Person");
        performer
            .Artist.Name!.Evidence.Should()
            .Be(new NormalizedEvidenceReference(recording.Uuid, "/artist-credit/0/artist/name"));
        work.WriterRoles.Should().BeEmpty();
        work.CompleteAuthorship.Should().BeNull();
        AssertCitations(index.Works, recording, lookup);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InferenceSelectionCannotIntroduceAWorkOutsideTheSuppliedCandidates(
        bool asAlternative
    )
    {
        var index = Index(WorkInput(GarciaHunterWork, "Crazy Fingers"));
        var result = new CompositionHypothesisProjector(index).Project(Song("Crazy Fingers"));
        var foreign = Guid.Parse(HawkshawWork);
        var selection = new CompositionHypothesisSelection(
            asAlternative ? Guid.Parse(GarciaHunterWork) : foreign,
            asAlternative ? [foreign] : [],
            "hypothesis",
            "fixture-selection",
            "Synthetic selection only"
        );
        var validate = () => CompositionHypothesisProjector.ValidateSelection(result, selection);

        validate.Should().Throw<InvalidDataException>();
        var supplied = selection with
        {
            SelectedWorkMbid = Guid.Parse(GarciaHunterWork),
            AlternativeWorkMbids = [],
        };
        var validateSupplied = () =>
            CompositionHypothesisProjector.ValidateSelection(result, supplied);
        validateSupplied.Should().NotThrow();
        AssertHypothesisOnly(result);
    }

    [Test]
    public void RecordingSearchReleaseMembershipsAttachThroughActualEdgesInEitherObservationOrder()
    {
        const string recordingId = "b8cc2bc8-ac3d-4b9b-81c3-b973006cac70";
        var search = Input(
            "recording-search",
            new string('d', 64),
            File.ReadAllBytes(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures",
                    "normalization",
                    "musicbrainz",
                    "program-search-first-recording.json"
                )
            )
        );
        var recording = RecordingInput(
            recordingId,
            "Such a Night",
            Performance(GarciaHunterWork, "Such a Night")
        );
        var lookup = WorkInput(GarciaHunterWork, "Such a Night");
        var workSearch = SearchInput(Work(GarciaHunterWork, "Such a Night"));
        var before = Index(search, recording, lookup, workSearch);
        var after = Index(workSearch, lookup, recording, search);
        var first = before.Works.Should().ContainSingle().Subject;
        var second = after.Works.Should().ContainSingle().Subject;
        var attached = first.SearchAssertions.Single(assertion =>
            assertion.Page.Item == search.Resource
        );
        var decoded = new NormalizationProviderDecoder()
            .Decode(search)
            .MusicBrainzSearchPages!.Single();

        JsonSerializer
            .Serialize(first.SearchAssertions)
            .Should()
            .Be(JsonSerializer.Serialize(second.SearchAssertions));
        JsonSerializer
            .Serialize(attached.Hit)
            .Should()
            .Be(JsonSerializer.Serialize(decoded.Hits!.Single()));
        attached.Page.Should().Be(decoded.Origin);
        attached.Hit.Releases.Should().HaveCount(10);
        attached
            .Hit.Releases!.Where(release =>
                release.Release.Identity.Identity.ProviderKey
                == "c1a2fd94-35b4-4e46-83a0-f7e1ad5b7db4"
            )
            .Select(release => release.Media!.Single().Position!.Value)
            .Should()
            .Equal(2, 1);
        var medium = attached.Hit.Releases[0].Media!.Single();
        medium.TrackCount!.Value.Should().Be(13);
        medium.TrackOffset!.Value.Should().Be(8);
        medium.Tracks!.Single().Number!.Value.Should().Be("9");
        medium.Tracks!.Single().Position.Should().BeNull();
        attached
            .Hit.ArtistCredits!.Select(credit => credit.Name!.Value)
            .Should()
            .Equal("The Band", "Dr. John");
        first.WriterRoles.Should().BeEmpty();
        before
            .SearchPages.Should()
            .ContainSingle()
            .Which.Origin.Item.Should()
            .Be(workSearch.Resource);
        after
            .SearchPages.Should()
            .ContainSingle()
            .Which.Origin.Item.Should()
            .Be(workSearch.Resource);
        RetainedCompositionCandidateIndex.Version.Should().Be("retained-composition-candidates-v2");
        AssertCitations(before.Works, search, recording, lookup, workSearch);
        AssertCitations(after.Works, search, recording, lookup, workSearch);
    }

    [Test]
    public void RecordingSearchHitsJoinEachQualifiedWorkOnceWithoutDroppingRawEdgeOrHitVariants()
    {
        const string firstRecording = "76d91022-0000-4000-8000-000000000001";
        const string secondRecording = "76d91022-0000-4000-8000-000000000002";
        var first = RecordingInput(
            firstRecording,
            "Same retained title",
            Performance(GarciaHunterWork, "Same retained title", ["live"]),
            Performance(GarciaHunterWork, "Same retained title", ["partial"]),
            Performance(HawkshawWork, "Same retained title")
        );
        var second = RecordingInput(
            secondRecording,
            "Same retained title",
            Performance(HawkshawWork, "Same retained title")
        );
        var search = RecordingSearchInput(
            new
            {
                id = firstRecording,
                title = "Same retained title",
                score = 100,
            },
            new
            {
                id = secondRecording,
                title = "Same retained title",
                score = 100,
            },
            new
            {
                id = firstRecording,
                title = "Conflicting retained variant",
                score = 42,
            }
        );
        var index = Index(search, first, second);
        var firstWork = index.Works.Single(work =>
            work.Identity.Identity.ProviderKey == GarciaHunterWork
        );
        var secondWork = index.Works.Single(work =>
            work.Identity.Identity.ProviderKey == HawkshawWork
        );

        firstWork
            .SearchAssertions.Select(assertion => assertion.Hit.SourceIndex)
            .Should()
            .Equal(0, 2);
        secondWork
            .SearchAssertions.Select(assertion => assertion.Hit.SourceIndex)
            .Should()
            .BeEquivalentTo(new[] { 0, 1, 2 });
        firstWork
            .SearchAssertions.Select(assertion =>
                assertion.Hit.Entity.Identity.Identity.ProviderKey
            )
            .Should()
            .OnlyContain(id => id == firstRecording);
        firstWork
            .SearchAssertions.Select(assertion => assertion.Hit.Score!.Value)
            .Should()
            .Equal(100, 42);
        firstWork.RecordingWorkRelationships.Should().HaveCount(2);
        firstWork
            .RecordingWorkRelationships.SelectMany(edge => edge.Relationship.Attributes!)
            .Select(attribute => attribute.Value)
            .Should()
            .Equal("live", "partial");
        firstWork.RecordingAssertions.Should().ContainSingle();
        secondWork.RecordingWorkRelationships.Should().HaveCount(2);
        secondWork.RecordingAssertions.Should().HaveCount(2);
        index.SearchPages.Should().BeEmpty();
        AssertCitations(index.Works, search, first, second);
    }

    [Test]
    public void MatchingNamesScoresAndBareUuidCollisionsCannotReplaceAQualifiedRecordingWorkEdge()
    {
        const string recordingId = "76d91022-0000-4000-8000-000000000001";
        const string unlinkedRecordingId = "76d91022-0000-4000-8000-000000000002";
        var recording = RecordingInput(
            recordingId,
            "Same retained title",
            Performance(GarciaHunterWork, "Same retained title")
        );
        var collisionWork = WorkInput(recordingId, "Same retained title");
        var unlinkedWork = WorkInput(HawkshawWork, "Same retained title");
        var search = RecordingSearchInput(
            new
            {
                id = unlinkedRecordingId,
                title = "Same retained title",
                score = 100,
            }
        );
        var wrongKind = Input(
            "artist-search",
            new string('e', 64),
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    count = 1,
                    offset = 0,
                    artists = new[]
                    {
                        new
                        {
                            id = recordingId,
                            name = "Same retained title",
                            score = 100,
                        },
                    },
                }
            )
        );
        var wrongProvider = RecordingSearchInput(
            new
            {
                id = recordingId,
                title = "Same retained title",
                score = 100,
            }
        );
        wrongProvider = Input(
            "recording-search",
            wrongProvider.Resource.ProviderKey,
            wrongProvider.Payload,
            provider: "fixture.invalid"
        );
        var index = Index(search, collisionWork, unlinkedWork, wrongKind, wrongProvider, recording);

        index.Works.Should().HaveCount(3).And.OnlyContain(work => work.SearchAssertions.Count == 0);
        index
            .Works.Single(work => work.Identity.Identity.ProviderKey == recordingId)
            .RecordingWorkRelationships.Should()
            .BeEmpty();
        index
            .Works.Single(work => work.Identity.Identity.ProviderKey == HawkshawWork)
            .RecordingWorkRelationships.Should()
            .BeEmpty();
        index
            .Works.Single(work => work.Identity.Identity.ProviderKey == GarciaHunterWork)
            .RecordingWorkRelationships.Should()
            .ContainSingle();
        index.SearchPages.Should().BeEmpty();
        AssertCitations(
            index.Works,
            search,
            collisionWork,
            unlinkedWork,
            wrongKind,
            wrongProvider,
            recording
        );
    }

    [Test]
    public void InferenceAlternativesCannotRepeatTheSelectedSuppliedWork()
    {
        var index = Index(WorkInput(GarciaHunterWork, "Crazy Fingers"));
        var result = new CompositionHypothesisProjector(index).Project(Song("Crazy Fingers"));
        var selected = Guid.Parse(GarciaHunterWork);
        var selection = new CompositionHypothesisSelection(
            selected,
            [selected],
            "hypothesis",
            "fixture-selection",
            "Repeated supplied candidate"
        );
        var validate = () => CompositionHypothesisProjector.ValidateSelection(result, selection);

        validate.Should().Throw<InvalidDataException>();
        AssertHypothesisOnly(result);
    }

    private static RetainedCompositionCandidateIndex Index(
        params NormalizationInputRevision[] inputs
    )
    {
        var index = new RetainedCompositionCandidateIndex();
        foreach (var input in inputs)
        {
            index.Observe(input);
        }
        index.CompleteObservation();
        return index;
    }

    private static CompositionHypothesisSong Song(string title, Guid? artist = null)
    {
        var artistUuid = artist ?? CatalogArtist;
        return new(
            "catalogue:fixture",
            artistUuid,
            new(
                ShadowRepertoireAdapter.ProvisionalEntityKey(artistUuid, title),
                artistUuid,
                null,
                title,
                "title_derived"
            ),
            [],
            [MusicRepertoireRelation.IdentityAlias],
            new(
                "/synthetic/music",
                "repertoire/000001.jsonl.gz",
                1,
                1,
                "/proposal/song/name",
                new string('a', 64)
            ),
            1,
            "Retained performing artist"
        );
    }

    private static object Writer(string name, string id, string type, string role) =>
        new Dictionary<string, object?>
        {
            ["type"] = role,
            ["direction"] = "backward",
            ["target-type"] = "artist",
            ["artist"] = new
            {
                id,
                name,
                type,
            },
        };

    private static object Work(
        string id,
        string title,
        object[]? writers = null,
        string[]? aliases = null
    ) =>
        new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = title,
            ["score"] = 100,
            ["relations"] = writers ?? [],
            ["aliases"] = (aliases ?? []).Select(name => new { name }).ToArray(),
        };

    private static NormalizationInputRevision WorkInput(
        string id,
        string title,
        object[]? writers = null,
        string[]? aliases = null
    ) => Input("work", id, JsonSerializer.SerializeToUtf8Bytes(Work(id, title, writers, aliases)));

    private static NormalizationInputRevision SearchInput(params object[] works) =>
        Input(
            "work-search",
            new string('b', 64),
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    count = works.Length,
                    offset = 0,
                    works,
                }
            )
        );

    private static NormalizationInputRevision RecordingSearchInput(params object[] recordings) =>
        Input(
            "recording-search",
            new string('d', 64),
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    count = recordings.Length,
                    offset = 0,
                    recordings,
                }
            )
        );

    private static NormalizationInputRevision RecordingInput(
        string id,
        string title,
        params object[] relationships
    ) =>
        Input(
            "recording",
            id,
            JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["title"] = title,
                    ["relations"] = relationships,
                }
            )
        );

    private static object Performance(string workId, string title, string[]? attributes = null) =>
        new Dictionary<string, object?>
        {
            ["type"] = "performance",
            ["direction"] = "forward",
            ["target-type"] = "work",
            ["work"] = new { id = workId, title },
            ["attributes"] = attributes ?? [],
        };

    private static NormalizationInputRevision Input(
        string type,
        string key,
        byte[] bytes,
        Guid? revision = null,
        string provider = "musicbrainz.org"
    ) =>
        new()
        {
            Uuid = revision ?? Guid.NewGuid(),
            Resource = new(provider, type, key),
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            MediaType = "application/json",
            CaptureVersion = type switch
            {
                "work" => "musicbrainz-work-json-v1",
                "work-search" => "musicbrainz-work-search-json-v1",
                "recording" => "musicbrainz-recording-work-json-v1",
                "recording-search" => "musicbrainz-recording-search-json-v1",
                "artist-search" => "musicbrainz-artist-search-json-v1",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            },
            CapturedAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
        };

    private static void AssertHypothesisOnly(CompositionHypothesisResult result)
    {
        result.PublicationPermitted.Should().BeFalse();
        result.ExternalUniverseComplete.Should().BeFalse();
        result
            .Candidates.Should()
            .OnlyContain(candidate =>
                candidate.WorkIdentity.Identity.Provider == "musicbrainz.org"
                && candidate.WorkIdentity.Identity.ResourceType == "work"
            );
    }

    private static void AssertCitations(
        IReadOnlyList<RetainedCompositionWork> works,
        params NormalizationInputRevision[] inputs
    )
    {
        var revisions = inputs.ToDictionary(input => input.Uuid);
        var citations = 0;
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (
                    value.TryGetProperty("InputRevisionUuid", out var revision)
                    && value.TryGetProperty("JsonPointer", out var pointer)
                )
                {
                    using var document = NormalizationRetainedDocument.Open(
                        revisions[revision.GetGuid()]
                    );
                    document.ValidateEvidence(new(revision.GetGuid(), pointer.GetString()!));
                    citations++;
                }
                foreach (var property in value.EnumerateObject())
                {
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
        Visit(JsonSerializer.SerializeToElement(works));
        citations.Should().BeGreaterThan(0);
    }
}
