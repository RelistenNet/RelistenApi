using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestShadowCompositionAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private string directory = null!;

    [SetUp]
    public void Start() =>
        directory = Path.Combine(Path.GetTempPath(), "shadow-composition-" + Guid.NewGuid());

    [TearDown]
    public void Finish()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void IndependentlySupportedCrossArtistWorksJoinWhileNamesakesRemainSeparate()
    {
        var first = Artist(1, "First performer");
        var cover = Artist(2, "Cover performer");
        var namesake = Artist(3, "Namesake performer");
        var other = Artist(4, "Other writer");
        var work = Guid.NewGuid();
        var otherWork = Guid.NewGuid();
        var songs = new[]
        {
            Song(first, "Shared Song"),
            Song(cover, "Different local title"),
            Song(namesake, "Shared Song"),
            Song(other, "Shared Song"),
        };
        var adapter = new ShadowCompositionAdapter(
            Packet(
                Proof(songs[0], work, "Authored Song"),
                Proof(songs[1], work, "Authored Song"),
                Proof(songs[3], otherWork, "Shared Song")
            )
        );
        foreach (var song in songs)
        {
            adapter.Observe(song.Record);
        }

        var results = songs.Select(song => adapter.Process(song).Single()).ToArray();
        results
            .Select(result => result.Outcome)
            .Should()
            .Equal("proposed", "proposed", "unresolved", "proposed");
        results[0].EntityKeys.Should().Contain(ShadowCompositionAdapter.WorkKey(work));
        results[1].EntityKeys.Should().Contain(ShadowCompositionAdapter.WorkKey(work));
        results[2]
            .EntityKeys.Should()
            .NotContain(key => key.StartsWith("composition:", StringComparison.Ordinal));
        results[3].EntityKeys.Should().Contain(ShadowCompositionAdapter.WorkKey(otherWork));
        results[3].EntityKeys.Should().NotContain(ShadowCompositionAdapter.WorkKey(work));
        var link = Proposal(results[0]).Mentions.Single().Links.Single();
        link.PrimaryWriterClaims[0]
            .GetProperty("literalName")
            .GetString()
            .Should()
            .Be("Source writer");
        link.MatchingDiagnostics.PolicyVersion.Should().BeNull();
        link.MatchingDiagnostics.Features.Single(feature => feature.Name == "authorship")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Missing);
        Proposal(results[0]).PublicationPermitted.Should().BeFalse();
    }

    [Test]
    public void LiveTrackAndUnorderedSetlistUseSupportedRepertoireWithoutEquatingRecordings()
    {
        var artist = Artist(1, "Performer");
        var song = Song(artist, "Shared Song");
        var work = Guid.NewGuid();
        var adapter = new ShadowCompositionAdapter(Packet(Proof(song, work, "Shared Song")));
        var track = Track(artist, "Shared Song", duration: 900);
        var setlist = Setlist(artist, song);
        adapter.Observe(song.Record);
        adapter.Observe(track.Record);
        adapter.Observe(setlist.Record);

        var trackResult = adapter.Process(track).Single();
        var setlistResult = adapter.Process(setlist).Single();

        trackResult.Outcome.Should().Be("proposed");
        setlistResult.Outcome.Should().Be("proposed");
        Proposal(trackResult)
            .Mentions.Single()
            .Links.Single()
            .CorrespondenceBasis.Should()
            .Be("supported_artist_repertoire_context");
        Proposal(setlistResult)
            .Mentions.Single()
            .RepertoireEntityKeys.Should()
            .Equal(ShadowRepertoireAdapter.KnownEntityKey(song.Uuid));
        Proposal(setlistResult).ExternalUniverseComplete.Should().BeFalse();
    }

    [Test]
    public void DifferentSupportedWorksInOneArtistContextRemainCompeting()
    {
        var artist = Artist(1, "Performer");
        var song = Song(artist, "Shared Song");
        var adapter = new ShadowCompositionAdapter(
            Packet(
                Proof(song, Guid.NewGuid(), "Shared Song"),
                Proof(song, Guid.NewGuid(), "Shared Song")
            )
        );
        adapter.Observe(song.Record);

        var result = adapter.Process(song).Single();
        var mention = Proposal(result).Mentions.Single();

        result.Outcome.Should().Be("unresolved");
        mention.CompetingWorkKeys.Should().HaveCount(2);
        mention.NextEvidencePartition.Should().Be("competing_supported_work_correspondences");
        result
            .EntityKeys.Should()
            .NotContain(key => key.StartsWith("composition:", StringComparison.Ordinal));
    }

    [Test]
    public void ExactSongProofDoesNotEraseAmbiguousSameArtistDictionaryRows()
    {
        var artist = Artist(1, "Performer");
        var first = Song(artist, "Shared Song");
        var second = Song(artist, "Shared Song");
        var adapter = new ShadowCompositionAdapter(
            Packet(Proof(first, Guid.NewGuid(), "Shared Song"))
        );
        adapter.Observe(first.Record);
        adapter.Observe(second.Record);

        adapter.Process(first).Single().Outcome.Should().Be("proposed");
        var mention = Proposal(adapter.Process(Track(artist, "Shared Song")).Single())
            .Mentions.Single();
        mention.Outcome.Should().Be("unresolved");
        mention.RepertoireEntityKeys.Should().HaveCount(2);
        mention.NextEvidencePartition.Should().Be("ambiguous_artist_repertoire");
    }

    [Test]
    public void MissingWorkFactsAndMixedTrackSegmentsRetainTheirOwnOutcomes()
    {
        var artist = Artist(1, "Performer");
        var song = Song(artist, "Shared Song");
        var adapter = new ShadowCompositionAdapter(
            Packet(Proof(song, Guid.NewGuid(), "Shared Song"))
        );
        adapter.Observe(song.Record);
        var track = Track(artist, "Shared Song > Unknown Song");

        var result = adapter.Process(track).Single();
        var proposal = Proposal(result);

        result.Outcome.Should().Be("proposed");
        result.Reason.Should().Be("partial_supported_composition_links");
        proposal
            .Mentions.Select(mention => mention.Outcome)
            .Should()
            .Equal("proposed", "unresolved");
        proposal
            .NextEvidencePartitions.Should()
            .ContainKey("named_content_needs_repertoire_and_work");
        new ShadowCompositionAdapter().Process(song).Single().Outcome.Should().Be("unresolved");
    }

    [Test]
    public void ManifestDigestAndOriginalCitationMutationsAreRejected()
    {
        var artist = Artist(1, "Performer");
        var pin = Packet(Proof(Song(artist, "Shared Song"), Guid.NewGuid(), "Shared Song"));
        var manifestPath = pin[..pin.LastIndexOf("#sha256=", StringComparison.Ordinal)];
        Assert.Throws<ArgumentException>(() => new ShadowCompositionAdapter(manifestPath));
        Assert.Throws<InvalidDataException>(() =>
            new ShadowCompositionAdapter(manifestPath + "#sha256=" + new string('0', 64))
        );
        File.AppendAllText(Path.Combine(directory, "sources", "proof-0.json"), "changed");
        Assert.Throws<InvalidDataException>(() => new ShadowCompositionAdapter(pin));
    }

    [Test]
    public void ChangedCurrentTrackEvidenceCannotReuseSavedExtraction()
    {
        var artist = Artist(1, "Performer");
        var track = Track(artist, "Shared Song");
        var changed = track with
        {
            Record = JsonSerializer.SerializeToElement(
                new
                {
                    kind = "track_sample",
                    row = new
                    {
                        uuid = track.Uuid,
                        artist_id = artist.Id,
                        title = "Other Song",
                    },
                }
            ),
        };
        Assert.Throws<InvalidDataException>(() =>
            new ShadowCompositionAdapter().Process(changed).Single()
        );
    }

    [Test]
    public void OpaqueFileAndNonmusicRetainDifferentOutcomes()
    {
        var artist = Artist(1, "Performer");
        var adapter = new ShadowCompositionAdapter();
        var opaque = adapter.Process(Track(artist, "ag2019-09-01t06.mp3")).Single();
        opaque.Outcome.Should().Be("unresolved");
        Proposal(opaque)
            .Mentions.Single()
            .NextEvidencePartition.Should()
            .Be("named_content_required");
        adapter.Process(Track(artist, "Banter")).Single().Outcome.Should().Be("not_applicable");
    }

    [Test]
    public void AChangedSegmentTitleCannotBorrowAnotherSongsValidEvidence()
    {
        var artist = Artist(1, "Performer");
        var other = Song(artist, "Other Song");
        var adapter = new ShadowCompositionAdapter(
            Packet(Proof(other, Guid.NewGuid(), "Other Song"))
        );
        var track = Track(artist, "Shared Song");
        var original = new TrackTitleExtractor().Extract(artist.Uuid, "Shared Song");
        var changed = original with
        {
            Segments = [original.Segments[0] with { Title = "Other Song" }],
        };
        var input = track with
        {
            Extraction = JsonSerializer.SerializeToElement(new { titleExtraction = changed }, Json),
        };
        Assert.Throws<InvalidDataException>(() => adapter.Process(input).Single());

        input = track with
        {
            Extraction = JsonSerializer.SerializeToElement(
                new
                {
                    resourceUuid = Guid.NewGuid(),
                    artistUuid = artist.Uuid,
                    titleExtraction = original,
                },
                Json
            ),
        };
        Assert.Throws<InvalidDataException>(() => adapter.Process(input).Single());
    }

    [Test]
    public async Task ResumeRejectsDifferentPrimaryEvidenceAndMissingEvidence()
    {
        var artist = Artist(1, "Performer");
        var song = Song(artist, "Shared Song");
        var first = new ShadowCompositionAdapter(
            Packet(Proof(song, Guid.NewGuid(), "Shared Song"))
        );
        var snapshot = Path.Combine(directory, "snapshot.jsonl");
        var extractions = Path.Combine(directory, "extractions.jsonl");
        File.WriteAllLines(
            snapshot,
            [
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "artist",
                        row = new
                        {
                            id = artist.Id,
                            uuid = artist.Uuid,
                            name = artist.Name,
                        },
                        configured_provider_mappings = Array.Empty<object>(),
                    }
                ),
                song.Record.GetRawText(),
            ]
        );
        File.WriteAllText(extractions, "");
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new { inputSha256 = Hash(File.ReadAllBytes(snapshot)) })
        );
        var output = Path.Combine(directory, "output");
        await new ShadowCatalogBatch([first]).RunAsync(snapshot, extractions, output);
        var changed = new ShadowCompositionAdapter(
            Packet(Proof(song, Guid.NewGuid(), "Shared Song"))
        );

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ShadowCatalogBatch([changed]).RunAsync(
                snapshot,
                extractions,
                output,
                resume: true
            )
        );
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ShadowCatalogBatch([new ShadowCompositionAdapter()]).RunAsync(
                snapshot,
                extractions,
                output,
                resume: true
            )
        );
    }

    [Test]
    public void ProviderWorkAssertionsShareWorkKeysWithoutAssigningCatalogIdentity()
    {
        var artist = Artist(1, "Provider artist context");
        var work = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                id = work,
                title = "Shared Song",
                relations = Array.Empty<object>(),
            }
        );
        var revision = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", "work", work.ToString("D")),
            CaptureVersion = "musicbrainz-work-json-v1",
            MediaType = "application/json",
            Payload = payload,
            Sha256 = Hash(payload),
        };
        var input = new ShadowCatalogInput(
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "provider_input",
                    row = new { uuid = revision.Uuid, artist_id = artist.Id },
                    input = revision,
                }
            ),
            null,
            artist,
            "musicbrainz.org"
        );
        var adapter = new ShadowCompositionAdapter();

        var result = adapter.Process(input).Single();

        adapter.DomainsFor("provider_input").Should().Equal("compositions");
        result.Outcome.Should().Be("proposed");
        result.EntityKeys.Should().Equal(ShadowCompositionAdapter.WorkKey(work));
        var proposal = (ShadowProviderCompositionProposal)result.Proposal!;
        proposal.CatalogWorkIdentityAssigned.Should().BeFalse();
        proposal.PublicationPermitted.Should().BeFalse();
        proposal
            .WorkAssertions.Single()
            .Work.Identity.Evidence.InputRevisionUuid.Should()
            .Be(revision.Uuid);
    }

    [Test]
    public void RetainedWorkSearchKeepsNamesakesAndWriterRolesAsProviderFacts()
    {
        var artist = Artist(1, "Provider context");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                count = 2,
                offset = 0,
                works = new object[]
                {
                    new
                    {
                        id = first,
                        title = "Shared Song",
                        relations = new[]
                        {
                            new
                            {
                                type = "composer",
                                direction = "backward",
                                artist = new { id = Guid.NewGuid(), name = "Source writer" },
                            },
                        },
                    },
                    new
                    {
                        id = second,
                        title = "Shared Song",
                        relations = Array.Empty<object>(),
                    },
                },
            }
        );
        var revision = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", "work-search", new string('0', 64)),
            CaptureVersion = "musicbrainz-work-search-json-v1",
            MediaType = "application/json",
            Payload = payload,
            Sha256 = Hash(payload),
        };
        var input = new ShadowCatalogInput(
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "provider_input",
                    row = new { uuid = revision.Uuid, artist_id = artist.Id },
                    input = revision,
                }
            ),
            null,
            artist,
            "musicbrainz.org"
        );

        var result = new ShadowCompositionAdapter().Process(input).Single();
        var proposal = (ShadowProviderCompositionProposal)result.Proposal!;

        result.Outcome.Should().Be("proposed");
        result
            .EntityKeys.Should()
            .BeEquivalentTo([
                ShadowCompositionAdapter.WorkKey(first),
                ShadowCompositionAdapter.WorkKey(second),
            ]);
        proposal.WorkSearchPages.Single().Hits.Should().HaveCount(2);
        proposal
            .WorkSearchPages.Single()
            .Hits![0]
            .WorkArtists!.Single()
            .Type!.Value.Should()
            .Be("composer");
        proposal.CatalogWorkIdentityAssigned.Should().BeFalse();
    }

    [Test]
    public void HydratedRecordingPathsJoinCrossArtistWorksAndKeepDifferentNamesakesSeparate()
    {
        var first = Artist(1, "Original performer");
        var cover = Artist(2, "Cover performer");
        var namesake = Artist(3, "Different performer");
        var firstMbid = Guid.NewGuid();
        var coverMbid = Guid.NewGuid();
        var namesakeMbid = Guid.NewGuid();
        var sharedWork = Guid.NewGuid();
        var differentWork = Guid.NewGuid();
        var writer = Guid.NewGuid();
        var songs = new[]
        {
            Song(first, "Shared Song"),
            Song(cover, "Shared Song"),
            Song(namesake, "Shared Song"),
        };
        var adapter = new ShadowCompositionAdapter();
        adapter.Observe(ArtistContext(first, firstMbid));
        adapter.Observe(ArtistContext(cover, coverMbid));
        adapter.Observe(ArtistContext(namesake, namesakeMbid));
        foreach (var song in songs)
        {
            adapter.Observe(song.Record);
        }
        adapter.Observe(ProviderRecording(first, firstMbid, "Shared Song", sharedWork).Record);
        adapter.Observe(ProviderRecording(cover, coverMbid, "Shared Song", sharedWork).Record);
        adapter.Observe(
            ProviderRecording(namesake, namesakeMbid, "Shared Song", differentWork).Record
        );
        adapter.Observe(
            ProviderRevision(
                first,
                "work",
                sharedWork,
                "musicbrainz-work-json-v1",
                new
                {
                    id = sharedWork,
                    title = "Original Work Title",
                    relations = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "composer",
                            ["direction"] = "backward",
                            ["target-type"] = "artist",
                            ["artist"] = new { id = writer, name = "Reported composer" },
                        },
                    },
                }
            ).Record
        );

        var results = songs.Select(song => adapter.Process(song).Single()).ToArray();
        results
            .Select(result => result.Outcome)
            .Should()
            .OnlyContain(outcome => outcome == "proposed");
        results[0].EntityKeys.Should().Contain(ShadowCompositionAdapter.WorkKey(sharedWork));
        results[1].EntityKeys.Should().Contain(ShadowCompositionAdapter.WorkKey(sharedWork));
        results[2].EntityKeys.Should().Contain(ShadowCompositionAdapter.WorkKey(differentWork));
        results[2].EntityKeys.Should().NotContain(ShadowCompositionAdapter.WorkKey(sharedWork));
        var path = Proposal(results[1]).Mentions.Single().ProviderCorrespondences!.Single();
        path.WriterRoles.Single()
            .Target!.Identity.Identity.ProviderKey.Should()
            .Be(writer.ToString("D"));
        path.Recording.ArtistCredits!.Single()
            .Artist!.Identity.Identity.ProviderKey.Should()
            .Be(coverMbid.ToString("D"));
        path.RemainingGaps.Should().Contain("canonical_author_people_unassigned");
        Proposal(results[1]).PublicationPermitted.Should().BeFalse();
        Proposal(results[1])
            .Mentions.Single()
            .Links.Single()
            .MatchingDiagnostics.PolicyVersion.Should()
            .BeNull();
    }

    [Test]
    public void CompetingRecordingWorksRemainUnresolvedAndTitleOnlyDoesNotLinkAnotherArtist()
    {
        var artist = Artist(1, "Performer");
        var other = Artist(2, "Same name");
        var mbid = Guid.NewGuid();
        var song = Song(artist, "Shared Song");
        var otherSong = Song(other, "Shared Song");
        var adapter = new ShadowCompositionAdapter();
        adapter.Observe(ArtistContext(artist, mbid));
        adapter.Observe(ArtistContext(other, Guid.NewGuid()));
        adapter.Observe(song.Record);
        adapter.Observe(otherSong.Record);
        adapter.Observe(ProviderRecording(artist, mbid, "Shared Song", Guid.NewGuid()).Record);
        adapter.Observe(ProviderRecording(artist, mbid, "Shared Song", Guid.NewGuid()).Record);

        var result = adapter.Process(song).Single();
        result.Outcome.Should().Be("unresolved");
        var mention = Proposal(result).Mentions.Single();
        mention.CompetingWorkKeys.Should().HaveCount(2);
        mention.ProviderCorrespondences.Should().HaveCount(2);
        mention.Links.Should().BeEmpty();
        adapter.Process(otherSong).Single().Outcome.Should().Be("unresolved");
    }

    [Test]
    public void ConflictingCatalogArtistIdsCannotSilentlyChooseAProviderWork()
    {
        var artist = Artist(1, "Performer");
        var mbid = Guid.NewGuid();
        var song = Song(artist, "Shared Song");
        var adapter = new ShadowCompositionAdapter();
        adapter.Observe(song.Record);
        adapter.Observe(ArtistContext(artist, Guid.NewGuid()));
        adapter.Observe(ArtistContext(artist, mbid));
        adapter.Observe(ProviderRecording(artist, mbid, "Shared Song", Guid.NewGuid()).Record);
        adapter.Process(song).Single().Outcome.Should().Be("unresolved");
    }

    [Test]
    public void UnsupportedProviderIndexDoesNotPoisonOtherCompositionInputs()
    {
        var artist = Artist(1, "Performer");
        var mbid = Guid.NewGuid();
        var song = Song(artist, "Shared Song");
        var valid = ProviderRecording(artist, mbid, "Shared Song", Guid.NewGuid());
        var invalid = ProviderRevision(
            artist,
            "recording",
            Guid.NewGuid(),
            "unsupported-capture-v99",
            new { title = "Unsupported" }
        );
        var adapter = new ShadowCompositionAdapter();
        adapter.Observe(ArtistContext(artist, mbid));
        adapter.Observe(song.Record);
        adapter.Observe(valid.Record);
        Action observe = () => adapter.Observe(invalid.Record);
        observe.Should().NotThrow();
        adapter.Process(invalid).Single().Outcome.Should().Be("failed");
        adapter.Process(song).Single().Outcome.Should().Be("proposed");
    }

    private static JsonElement ArtistContext(ShadowCatalogArtist artist, Guid musicBrainzId) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "artist",
                row = new
                {
                    id = artist.Id,
                    uuid = artist.Uuid,
                    name = artist.Name,
                    musicbrainz_id = musicBrainzId,
                },
            }
        );

    private static ShadowCatalogInput ProviderRecording(
        ShadowCatalogArtist artist,
        Guid mbid,
        string title,
        Guid work
    ) =>
        ProviderRevision(
            artist,
            "recording",
            Guid.NewGuid(),
            "musicbrainz-recording-work-json-v1",
            new Dictionary<string, object>
            {
                ["title"] = title,
                ["artist-credit"] = new[]
                {
                    new { name = artist.Name, artist = new { id = mbid, name = artist.Name } },
                },
                ["relations"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "performance",
                        ["direction"] = "forward",
                        ["target-type"] = "work",
                        ["work"] = new { id = work, title = "Reported work" },
                    },
                },
            }
        );

    private static ShadowCatalogInput ProviderRevision(
        ShadowCatalogArtist artist,
        string kind,
        Guid id,
        string version,
        object body
    )
    {
        var value = JsonSerializer.SerializeToElement(body);
        var fields = value
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        fields["id"] = JsonSerializer.SerializeToElement(id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fields);
        var revision = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", kind, id.ToString("D")),
            CaptureVersion = version,
            MediaType = "application/json",
            Payload = bytes,
            Sha256 = Hash(bytes),
        };
        return new(
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "provider_input",
                    row = new { uuid = revision.Uuid, artist_id = artist.Id },
                    input = revision,
                }
            ),
            null,
            artist,
            "musicbrainz.org"
        );
    }

    private static ShadowCompositionProposal Proposal(ShadowCatalogResult result) =>
        (ShadowCompositionProposal)result.Proposal!;

    private static ShadowCatalogArtist Artist(long id, string name) =>
        new(id, Guid.NewGuid(), name, ["archive.org"]);

    private static ShadowCatalogInput Song(ShadowCatalogArtist artist, string title) =>
        new(
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "song_sample",
                    row = new
                    {
                        id = 1,
                        uuid = Guid.NewGuid(),
                        artist_id = artist.Id,
                        name = title,
                    },
                }
            ),
            null,
            artist,
            "archive.org"
        );

    private static ShadowCatalogInput Track(
        ShadowCatalogArtist artist,
        string title,
        int duration = 300
    )
    {
        var extraction = new TrackTitleExtractor().Extract(artist.Uuid, title);
        return new(
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "track_sample",
                    row = new
                    {
                        id = 1,
                        uuid = Guid.NewGuid(),
                        artist_id = artist.Id,
                        title,
                        duration,
                    },
                }
            ),
            JsonSerializer.SerializeToElement(new { titleExtraction = extraction }, Json),
            artist,
            "archive.org"
        );
    }

    private static ShadowCatalogInput Setlist(
        ShadowCatalogArtist artist,
        ShadowCatalogInput song
    ) =>
        new(
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "setlist_sample",
                    row = new
                    {
                        id = 1,
                        uuid = Guid.NewGuid(),
                        artist_id = artist.Id,
                    },
                    unordered_memberships = new[] { new { song = song.Row } },
                }
            ),
            null,
            artist,
            "archive.org"
        );

    private static (ShadowCatalogInput Input, Guid Work, string Title) Proof(
        ShadowCatalogInput input,
        Guid work,
        string title
    ) => (input, work, title);

    private string Packet(params (ShadowCatalogInput Input, Guid Work, string Title)[] proofs)
    {
        var packet = Path.Combine(directory, "packet");
        var sources = Path.Combine(directory, "sources");
        Directory.CreateDirectory(packet);
        Directory.CreateDirectory(sources);
        var registry = new Dictionary<string, object>();
        var rows = new List<object>();
        for (var index = 0; index < proofs.Length; index++)
        {
            var (input, work, title) = proofs[index];
            var sourceId = "proof-" + index;
            var value = JsonSerializer.SerializeToElement(
                new
                {
                    id = work,
                    title,
                    writtenBy = "Source writer",
                    performer = input.Artist.Name,
                }
            );
            var name = sourceId + ".json";
            File.WriteAllText(Path.Combine(sources, name), value.GetRawText());
            registry[sourceId] = new
            {
                packet = "source",
                path = name,
                sha256 = Hash(File.ReadAllBytes(Path.Combine(sources, name))),
            };
            rows.Add(
                new
                {
                    claimId = "source:" + index,
                    literalTitle = input.Row.GetProperty("name").GetString(),
                    status = "source_supported_composition_correspondence_draft",
                    sourceDependencyGroups = new
                    {
                        catalogOriginalUuid = input.Uuid,
                        artistUuid = input.Artist.Uuid,
                    },
                    primaryEvidence = new[]
                    {
                        new
                        {
                            sourceId,
                            jsonPointer = "",
                            value,
                        },
                    },
                    primaryWriterClaims = new[]
                    {
                        new
                        {
                            literalName = "Source writer",
                            sourceRole = "written by",
                            canonicalPersonUuid = (Guid?)null,
                        },
                    },
                    workCorrespondenceDraft = new
                    {
                        provider = "musicbrainz.org",
                        resourceType = "work",
                        providerKey = work,
                        acceptedForPublication = false,
                    },
                    recordingIdentityClaimed = false,
                    publicationPerformed = false,
                    negativeMatchAsserted = false,
                    canonicalIdsMinted = Array.Empty<Guid>(),
                    rationale = "Synthetic source explicitly connects this repertoire row to the authored work.",
                    competingIdentityAssessment = "No contradictory source assertion in this synthetic example.",
                    remainingGaps = new[]
                    {
                        "No canonical writer person identity or publication policy.",
                    },
                }
            );
        }

        WriteManifest(sources, new { version = "source-fixture", files = Inventory(sources) });
        File.WriteAllText(
            Path.Combine(packet, "source-pins.json"),
            JsonSerializer.Serialize(
                new
                {
                    packets = new Dictionary<string, object>
                    {
                        ["source"] = new
                        {
                            directory = sources,
                            manifestSha256 = Hash(
                                File.ReadAllBytes(Path.Combine(sources, "manifest.json"))
                            ),
                        },
                    },
                    citationSources = registry,
                },
                Json
            )
        );
        File.WriteAllLines(
            Path.Combine(packet, "claim-primary-adjudications.jsonl"),
            rows.Select(row => JsonSerializer.Serialize(row, Json))
        );
        WriteManifest(
            packet,
            new { version = "composition120-primary-adjudication-v1", files = Inventory(packet) }
        );
        var manifest = Path.Combine(packet, "manifest.json");
        return manifest + "#sha256=" + Hash(File.ReadAllBytes(manifest));
    }

    private static object[] Inventory(string directory) =>
        Directory
            .EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path) != "manifest.json")
            .Select(path =>
                (object)
                    new
                    {
                        path = Path.GetFileName(path),
                        bytes = new FileInfo(path).Length,
                        sha256 = Hash(File.ReadAllBytes(path)),
                    }
            )
            .ToArray();

    private static void WriteManifest(string directory, object manifest) =>
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, Json)
        );

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
