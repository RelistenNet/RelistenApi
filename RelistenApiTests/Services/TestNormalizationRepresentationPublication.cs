using System.Text;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRepresentationPublication
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task Latin1HtmlSupportsPersonIdentityAndRecordingCreditWithExactEvidence()
    {
        const string raw = "<p>Taped by Zoë &amp; François</p>";
        var input = await Capture(
            raw,
            "text/html; charset=iso-8859-1",
            Encoding.Latin1,
            "people",
            ""
        );
        var quote = "Taped by Zoë &amp; François";
        input = input with
        {
            Reference = input.Reference with
            {
                StartCodePoint = 3,
                EndCodePoint = 3 + quote.Length,
                Quote = quote,
            },
        };
        var people = new NormalizedPeopleService(fixture.Db, fixture.Work);
        var person = new CanonicalPerson(Guid.NewGuid(), "François");
        var source = await fixture.Source();
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            quote,
            person.Uuid,
            null,
            [input.Reference]
        );

        (await fixture.PublishPeople(people, input, new([person], [], [], [credit])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await people.FindCandidatesAsync("François"))
            .Should()
            .ContainSingle(match => match.Uuid == credit.PersonUuid);
        var stored = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT quote FROM normalization.recording_credit_evidence WHERE credit_uuid=@Uuid",
                credit
            )
        );
        stored.Should().Be(quote);
    }

    [Test]
    public async Task QuoteFreeHtmlNameAndLocalityReviewUseDecodedOriginalDocument()
    {
        const string raw = "<p>Fran&#231;ois Hall</p><p>Montréal, QC</p>";
        var input = await Capture(raw, "text/html; charset=utf-16", Encoding.Unicode, "venues", "");
        var venues = new NormalizedVenueService(fixture.Db, fixture.Work);
        var venue = new CanonicalVenue(Guid.NewGuid(), "François Hall");
        await fixture.PublishVenues(venues, input, new([venue], [], [], [], [], []));
        var owner = await fixture.OwnerUuid("venues", "venue", venue.Uuid);
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Montréal, QC",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            input.Reference
        );

        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                venue.Uuid,
                1,
                "accept",
                "The retained page names the locality",
                Location: location
            ),
            "operator"
        );
        (await venues.FindCandidatesAsync("François Hall"))
            .Should()
            .ContainSingle(match => match.Uuid == venue.Uuid);
        var stored = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT original_locality FROM normalization.resolved_venue_locations WHERE resolved_venue_uuid=@Uuid",
                venue
            )
        );
        stored.Should().Be("Montréal, QC");
    }

    [Test]
    public async Task Utf16JsonStructuredLocationReviewRequiresMatchingRetainedFields()
    {
        const string raw =
            """{"name":"Montréal Hall","location":{"original_locality":"Montréal, QC","city":"Montréal","country":"CA"}}""";
        var input = await Capture(
            raw,
            "application/vnd.relisten+json; charset=utf-16",
            Encoding.Unicode,
            "venues",
            "/name"
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), "Montréal Hall");
        var venues = new NormalizedVenueService(fixture.Db, fixture.Work);
        await fixture.PublishVenues(venues, input, new([venue], [], [], [], [], []));
        var owner = await fixture.OwnerUuid("venues", "venue", venue.Uuid);
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Montréal, QC",
            "CA",
            null,
            "Montréal",
            null,
            null,
            null,
            null,
            null,
            null,
            input.Reference with
            {
                JsonPointer = "/location",
            }
        );
        var invalid = () =>
            venues.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner,
                    0,
                    venue.Uuid,
                    1,
                    "accept",
                    "Unsupported city",
                    Location: location with
                    {
                        City = "Toronto",
                    }
                ),
                "operator"
            );
        await invalid.Should().ThrowAsync<ArgumentException>();

        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                venue.Uuid,
                1,
                "accept",
                "Structured locality from retained JSON",
                Location: location
            ),
            "operator"
        );
        var stored = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT city FROM normalization.resolved_venue_locations WHERE resolved_venue_uuid=@Uuid",
                venue
            )
        );
        stored.Should().Be("Montréal");
    }

    [Test]
    public async Task RealPhishHtmlSupportsMusicPublicationAndBadQuoteLeavesNoIdentity()
    {
        var raw = await File.ReadAllTextAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                "setlist-1992-11-21.html"
            )
        );
        var input = await Capture(raw, "text/html; charset=utf-8", Encoding.UTF8, "songs", "");
        const string title = "The Landlady";
        var start =
            raw[..raw.IndexOf(">" + title + "</a>", StringComparison.Ordinal)]
                .EnumerateRunes()
                .Count() + 1;
        var evidence = input.Reference with
        {
            StartCodePoint = start,
            EndCodePoint = start + title.Length,
            Quote = title,
        };
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var owner = await fixture.Own(input, "artist_repertoire", artist);
        var song = new RepertoireSongChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            DisplayTitle = title,
            Evidence = [evidence],
        };
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        var second = song with { Uuid = Guid.NewGuid() };
        var invalid = () =>
            music.PublishAsync(
                owner.Ticket.WorkUuid,
                new()
                {
                    RepertoireSongs =
                    [
                        song,
                        second with
                        {
                            Evidence = [evidence with { Quote = "Wrong title" }],
                        },
                    ],
                }
            );
        await invalid.Should().ThrowAsync<ArgumentException>();
        (await music.GetRepertoireAsync(song.Uuid)).Should().BeNull();
        (await music.GetRepertoireAsync(second.Uuid)).Should().BeNull();

        (await music.PublishAsync(owner.Ticket.WorkUuid, new() { RepertoireSongs = [song] }))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var stored = (await music.GetRepertoireAsync(song.Uuid))!;
        stored.Value.DisplayTitle.Should().Be(title);
        stored.Value.Evidence.Should().ContainSingle().Which.Should().Be(evidence);
    }

    private async Task<DomainInput> Capture(
        string raw,
        string mediaType,
        Encoding encoding,
        string domain,
        string pointer
    )
    {
        var resource = new NormalizationResourceKey(
            "representation-test",
            domain,
            Guid.NewGuid().ToString()
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var result = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                encoding.GetBytes(raw),
                mediaType,
                "test",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var subject = new NormalizationSubjectKey(
            domain,
            domain == "people" ? "person" : "venue",
            Guid.NewGuid()
        );
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-1",
            [resource],
            []
        );
        return new(resource, subject, ticket, new(result.Revision.Uuid, pointer));
    }
}
