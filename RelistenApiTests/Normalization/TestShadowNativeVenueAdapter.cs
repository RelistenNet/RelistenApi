using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestShadowNativeVenueAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ShadowCatalogArtist Artist = new(
        7,
        Guid.NewGuid(),
        "Native artist",
        []
    );

    [Test]
    public void ArchiveVenueRetainsOriginalNamesLocalityAndRecordingCitationOnce()
    {
        var revision = Revision(
            "archive.org",
            "item_metadata",
            "item",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"item","venue":"Théâtre 🎵","coverage":"Montréal, QC","date":"1970-xx-xx"},"files":[]}"""
        );
        var result = Result(revision);
        var venue = Proposal(result).Venues.Should().ContainSingle().Subject;

        result.Outcome.Should().Be("proposed");
        venue.DisplayName.Should().Be("Théâtre 🎵");
        venue
            .Venue.Names.Single()
            .Evidence.Should()
            .Be(new NormalizedEvidenceReference(revision.Uuid, "/metadata/venue"));
        venue.Locality.OriginalWording.Should().Be("Montréal, QC");
        venue.Locality.City.Should().BeNull();
        venue.RecordingLinks.Single().LiteralDate!.Value.Should().Be("1970-xx-xx");
        venue.RecordingLinks.Single().CatalogSourceUuid.Should().BeNull();
        venue.AcceptedIdentity.Should().BeFalse();
        result.EntityKeys.Should().Contain(venue.ObservationKey);
    }

    [Test]
    public void CityCoordinatesRemainCityEvidenceAndSetlistIdentitiesRemainDistinct()
    {
        var revision = Revision(
            "setlist.fm",
            "setlist-page",
            "artist:page=1",
            "setlist-fm-http-v1",
            """{"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"account-one","eventDate":"03-02-1970","venue":{"id":"venue-one","name":"The Hall","city":{"name":"Boston","state":"MA","country":{"name":"United States"},"coords":{"lat":42.36,"long":-71.06}}},"sets":{"set":[]}}]}"""
        );
        var result = Result(revision);
        var venue = Proposal(result).Venues.Single();

        venue.Coordinates.Should().Be(new ShadowVenueCoordinates(42.36, -71.06, "city", true));
        venue.ProtectedUnknowns.Should().Contain("coordinates_do_not_identify_premises");
        venue
            .Venue.Identities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("setlist.fm", "venue", "venue-one"));
        venue.SetlistLinks.Single().Setlist.Item.ProviderKey.Should().Be("account-one");
        venue.Locality.City.Should().Be("Boston");
        venue.Locality.CountryCode.Should().BeNull();
        venue.AcceptedIdentity.Should().BeFalse();
    }

    [Test]
    public void ProviderHistoryIsRetainedProseWithoutInventedIntervalsOrPremises()
    {
        var revision = Revision(
            "phish.in",
            "catalog",
            "venues",
            "raw-http:1",
            """{"success":true,"data":[{"id":9,"name":"Current Hall","past_names":"Old Hall (1980–1995); formerly part of the theatre","location":"Philadelphia, PA","latitude":39.9,"longitude":-75.1}]}"""
        );
        var venue = Proposal(Result(revision)).Venues.Single();

        venue
            .Venue.Text.Single()
            .Text.Value.Should()
            .Be("Old Hall (1980–1995); formerly part of the theatre");
        venue.Venue.Text.Single().Text.Evidence.JsonPointer.Should().Be("/data/0/past_names");
        venue.Coordinates!.Kind.Should().Be("unknown");
        venue.ProtectedUnknowns.Should().Contain("name_validity_dates_unknown");
        venue.Venue.Origin.Item.ProviderKey.Should().Be("9");
    }

    [Test]
    public void CrossArtistNamesShareBrowseKeysButNeverOriginalObservationKeys()
    {
        var first = Revision(
            "archive.org",
            "item_metadata",
            "one",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"one","venue":"The Hall","coverage":"Boston, MA"},"files":[]}"""
        );
        var second = Revision(
            "archive.org",
            "item_metadata",
            "two",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"two","venue":"The Hall","coverage":"Boston, MA"},"files":[]}"""
        );
        var left = Proposal(Result(first)).Venues.Single();
        var right = Proposal(Result(second, Artist with { Id = 8, Uuid = Guid.NewGuid() }))
            .Venues.Single();

        left.BrowseKeys.Should().BeEquivalentTo(right.BrowseKeys);
        left.ObservationKey.Should().NotBe(right.ObservationKey);
        left.AcceptedIdentity.Should().BeFalse();
        right.AcceptedIdentity.Should().BeFalse();
    }

    [Test]
    public void EqualProviderIdsWithDifferentLocationsPreserveSeparateAccounts()
    {
        var first = Revision(
            "phish.in",
            "catalog",
            "venues",
            "raw-http:1",
            """{"success":true,"data":[{"id":9,"name":"The Hall","location":"Boston, MA"},{"id":9,"name":"The Hall","location":"Portland, ME"}]}"""
        );
        var venues = Proposal(Result(first)).Venues;

        venues.Should().HaveCount(2);
        venues.Select(venue => venue.ObservationKey).Distinct().Should().HaveCount(2);
        venues
            .Select(venue => venue.Locality.OriginalWording)
            .Should()
            .Equal("Boston, MA", "Portland, ME");
        venues.Should().OnlyContain(venue => !venue.AcceptedIdentity);
    }

    [Test]
    public void MissingVenueIsNotApplicableAndUnknownNameIsUnresolved()
    {
        var absent = Revision(
            "archive.org",
            "item_metadata",
            "item",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"item"},"files":[]}"""
        );
        var unknown = Revision(
            "archive.org",
            "item_metadata",
            "item",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"item","venue":"unknown"},"files":[]}"""
        );

        Result(absent).Outcome.Should().Be("not_applicable");
        Result(unknown).Outcome.Should().Be("unresolved");
        Proposal(Result(unknown)).Venues.Single().Venue.Names.Single().Value.Should().Be("unknown");
    }

    [Test]
    public void HashMismatchAndUnsupportedRepresentationCannotProduceVenueHypotheses()
    {
        var valid = Revision(
            "archive.org",
            "item_metadata",
            "item",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"item","venue":"The Hall"},"files":[]}"""
        );
        var tampered = new NormalizationInputRevision
        {
            Uuid = valid.Uuid,
            Resource = valid.Resource,
            Payload = Encoding.UTF8.GetBytes("{}"),
            Sha256 = valid.Sha256,
            MediaType = valid.MediaType,
            CaptureVersion = valid.CaptureVersion,
        };
        Result(tampered).Outcome.Should().Be("unresolved");
        Result(tampered).Proposal.Should().BeNull();
        Result(Revision("archive.org", "item_metadata", "item", "future-v1", "{}"))
            .Outcome.Should()
            .Be("unresolved");
    }

    [Test]
    public void LocalRootDoesNotPublishEveryDirectoryAsOneVenueInput()
    {
        var root = Revision(
            "local",
            "metadata_root",
            "catalog",
            "local-http-json-v1",
            """{"data":{"1970-01-01":[{"dir":"one","venue":"One Hall"},{"dir":"two","venue":"Two Hall"}]}}"""
        );

        Result(root).Outcome.Should().Be("not_applicable");
        Proposal(Result(root)).Venues.Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void LocalDirectoryUsesOnlyExactSelectedRootAccount(bool includeRoot)
    {
        var root = Revision(
            "local",
            "metadata_root",
            "catalog",
            "local-http-json-v1",
            """{"data":{"1970-xx-xx":[{"dir":"one","venue":"Selected Hall","city":"Montréal","state":"QC"},{"dir":"two","venue":"Other Hall"}]}}"""
        );
        var directory = Revision(
            "local",
            "directory_metadata",
            "[\"catalog\",\"one\"]",
            "local-http-json-v1",
            """{"tracks":[],"txts":[]}"""
        );
        var artifact = new RetainedHttpArtifact("/synthetic", 0, new string('a', 64));
        var binding = new RetainedLocalCatalogBinding(
            "unresolved",
            "no_playable_anchor",
            null,
            false,
            null,
            null,
            null,
            [],
            [],
            [],
            artifact,
            null,
            null
        );
        var provenance = new RetainedLocalProviderProvenance(
            directory.Uuid,
            root.Uuid,
            DateTimeOffset.UnixEpoch,
            "catalog",
            "one",
            "https://example.invalid/one",
            artifact,
            new("/root", root.Payload.LongLength, root.Sha256),
            artifact,
            artifact,
            artifact,
            0,
            new("/directory", directory.Payload.LongLength, directory.Sha256),
            binding
        );
        var helper = new ShadowProviderObservations();
        var adapter = new ShadowNativeVenueAdapter(helper);
        if (includeRoot)
        {
            adapter.Observe(
                JsonSerializer.SerializeToElement(
                    new
                    {
                        kind = "local_root_input",
                        row = new { uuid = root.Uuid },
                        input = root,
                    },
                    Json
                )
            );
        }
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "provider_input",
                row = new { uuid = directory.Uuid },
                input = directory,
                retained_local = provenance,
            },
            Json
        );
        var result = adapter.Process(new(record, null, Artist, "local")).Single();

        if (includeRoot)
        {
            var venue = Proposal(result).Venues.Should().ContainSingle().Subject;
            venue.DisplayName.Should().Be("Selected Hall");
            venue.Venue.Names.Single().Evidence.InputRevisionUuid.Should().Be(root.Uuid);
            venue.RecordingLinks.Single().Recording.Item.Should().Be(directory.Resource);
            venue.RecordingLinks.Single().CatalogSourceUuid.Should().BeNull();
            venue.RecordingLinks.Single().LiteralDate!.Value.Should().Be("1970-xx-xx");
        }
        else
        {
            result.Outcome.Should().Be("unresolved");
            JsonSerializer
                .Serialize(result.Evidence)
                .Should()
                .Contain("missing_retained_local_root");
        }
    }

    [TestCase("[\"Boston, MA\",\"Portland, ME\"]")]
    [TestCase("\"Boston, MA and Portland, ME\"")]
    public void AmbiguousOriginalLocalitiesRemainCitedAndUnresolved(string coverage)
    {
        var revision = Revision(
            "archive.org",
            "item_metadata",
            "item",
            "archive-item-response-v1",
            "{\"metadata\":{\"identifier\":\"item\",\"venue\":\"The Hall\",\"coverage\":"
                + coverage
                + "},\"files\":[]}"
        );
        var result = Result(revision);
        var venue = Proposal(result).Venues.Single();

        result.Outcome.Should().Be("unresolved");
        venue.LocalityAssertions.Should().NotBeEmpty();
        venue
            .LocalityAssertions.Should()
            .OnlyContain(assertion =>
                assertion.Evidence.InputRevisionUuid == revision.Uuid
                && assertion.Evidence.JsonPointer.StartsWith("/metadata/coverage")
            );
        venue.Locality.City.Should().BeNull();
        venue.AcceptedIdentity.Should().BeFalse();
    }

    [Test]
    public void HtmlVenuePreservesDecodedNameAndOriginalTextSpan()
    {
        var revision = Revision(
            "jerrygarcia.com",
            "mirror_show_page",
            "show-one",
            "jerry-mirror-html:1",
            """<p class="venue-name">Fran&#231;ois Hall</p><p class="venue-address">Montréal, QC</p><p class="venue-country">Canada</p><time datetime="1970-xx-xx"></time>""",
            "text/html"
        );
        var venue = Proposal(Result(revision)).Venues.Single();

        venue.DisplayName.Should().Be("François Hall");
        venue
            .Venue.Names.Single()
            .Evidence.Quote.Should()
            .Be("<p class=\"venue-name\">Fran&#231;ois Hall</p>");
        venue.Venue.Names.Single().Evidence.StartCodePoint.Should().NotBeNull();
        venue.SetlistLinks.Single().LiteralDate!.Value.Should().Be("1970-xx-xx");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ReaderHomeKeysRequireAuthenticatedCatalogSourceAndShow(bool matchingSource)
    {
        var revision = Revision(
            "archive.org",
            "item_metadata",
            "item",
            "archive-item-response-v1",
            """{"metadata":{"identifier":"item","venue":"The Hall"},"files":[]}"""
        );
        var sourceUuid = Guid.NewGuid();
        var showUuid = Guid.NewGuid();
        var artifact = new RetainedHttpArtifact("/synthetic", 0, new string('a', 64));
        var binding = new RetainedHttpCatalogBinding(
            "bound",
            null,
            sourceUuid,
            sourceUuid,
            showUuid,
            null,
            artifact,
            "/synthetic/source.jsonl.gz",
            1
        );
        var provenance = new RetainedHttpProviderProvenance(
            revision.Uuid,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "https://archive.org/metadata/item",
            200,
            artifact,
            artifact,
            artifact,
            0,
            new("/body", revision.Payload.LongLength, revision.Sha256),
            binding
        );
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "provider_input",
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = Artist.Id,
                    artist_uuid = Artist.Uuid,
                },
                input = revision,
                retained_http = provenance,
                catalog_binding = binding,
                source_context = new
                {
                    uuid = sourceUuid,
                    artist_id = Artist.Id,
                    show_id = 19,
                    upstream_identifier = matchingSource ? "item" : "another-item",
                },
                show_context = new
                {
                    uuid = showUuid,
                    id = 19,
                    artist_id = Artist.Id,
                },
            },
            Json
        );
        var result = new ShadowNativeVenueAdapter(new ShadowProviderObservations())
            .Process(new(record, null, Artist, "archive.org"))
            .Single();
        var link = Proposal(result).Venues.Single().RecordingLinks.Single();

        if (matchingSource)
        {
            result.EntityKeys.Should().Contain("source:" + sourceUuid.ToString("D"));
            result.EntityKeys.Should().Contain("show:" + showUuid.ToString("D"));
            link.CatalogSourceUuid.Should().Be(sourceUuid);
            link.CatalogShowUuid.Should().Be(showUuid);
        }
        else
        {
            result
                .EntityKeys.Should()
                .NotContain(key => key.StartsWith("source:") || key.StartsWith("show:"));
            link.CatalogSourceUuid.Should().BeNull();
            link.CatalogShowUuid.Should().BeNull();
        }
    }

    [Test]
    public void RepeatedProcessingKeepsAllKeysAndCitationsStable()
    {
        var input = Revision(
            "phish.in",
            "catalog",
            "venues",
            "raw-http:1",
            """{"success":true,"data":[{"id":9,"name":"The Hall","location":"Boston, MA"}]}"""
        );
        JsonSerializer
            .Serialize(Result(input))
            .Should()
            .Be(JsonSerializer.Serialize(Result(input)));
    }

    private static ShadowCatalogResult Result(
        NormalizationInputRevision revision,
        ShadowCatalogArtist? artist = null
    )
    {
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "provider_input",
                row = new { uuid = revision.Uuid },
                input = revision,
            },
            Json
        );
        return new ShadowNativeVenueAdapter(new ShadowProviderObservations())
            .Process(new(record, null, artist ?? Artist, revision.Resource.Provider))
            .Single();
    }

    private static ShadowNativeVenueProposal Proposal(ShadowCatalogResult result) =>
        (ShadowNativeVenueProposal)result.Proposal!;

    private static NormalizationInputRevision Revision(
        string provider,
        string type,
        string key,
        string version,
        string body,
        string mediaType = "application/json"
    )
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new(provider, type, key),
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            MediaType = mediaType,
            CaptureVersion = version,
        };
    }
}
