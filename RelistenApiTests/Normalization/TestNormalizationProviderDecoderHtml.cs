using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderHtml
{
    private readonly NormalizationProviderDecoder decoder = new();

    [TestCase("utf-8")]
    [TestCase("iso-8859-1")]
    [TestCase("utf-16")]
    public void JerryKeepsRepeatedSongsSetBoundariesEntitiesAndRawAnnotations(string charset)
    {
        const string raw = """
            <p class="venue-name">Fran&#231;ois Hall</p><p class="venue-name">Jerry García Band</p>
            <p class="venue-name">Tour &amp; Notes</p><p class="venue-address">Montréal, QC</p>
            <p class="venue-country">Canada</p><time datetime="1970-xx-xx"></time>
            <section data-set="Early set"><ol>
            <li><a href="/song/dark-star/">Dark Star</a> ></li>
            <li><a href="/song/drums/">Drums</a>*</li>
            <li><a href="/song/dark-star/">Dark Star</a></li>
            </ol></section><ol data-set="Encore"></ol>
            """;
        var input = Html(
            raw,
            "jerrygarcia.com",
            "mirror_show_page",
            "jerry/1970-xx-xx-show",
            "jerry-mirror-html:1",
            charset
        );
        var result = decoder.Decode(input);
        var edition = result.Setlists.Should().ContainSingle().Subject;
        edition.Ordered.Should().BeTrue();
        edition.LiteralDate!.Value.Should().Be("1970-xx-xx");
        edition.Performers.Single().Name!.Value.Should().Be("Jerry García Band");
        edition.Sets.Select(set => set.Label!.Value).Should().Equal("Early set", "Encore");
        edition
            .Passages.Select(passage => passage.Title.Value)
            .Should()
            .Equal("Dark Star", "Drums", "Dark Star");
        edition.Passages.Select(passage => passage.SetOrdinal).Should().Equal(1, 1, 1);
        edition.Passages.Select(passage => passage.Ordinal).Should().Equal(1, 2, 3);
        edition.Passages[0].TransitionMarker!.Value.Should().Be(">");
        edition.Passages[1].TransitionMarker.Should().BeNull();
        edition.Passages[2].TransitionMarker.Should().BeNull();
        edition
            .Passages[1]
            .Annotations.Single()
            .Text.Value.Should()
            .Be("<li><a href=\"/song/drums/\">Drums</a>*</li>");
        edition
            .Passages[0]
            .SongIdentities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("jerrygarcia.com", "song", "dark-star"));
        edition
            .Passages[2]
            .SongIdentities.Single()
            .Identity.Should()
            .Be(edition.Passages[0].SongIdentities.Single().Identity);
        edition.Passages.Should().OnlyContain(passage => passage.OccurrenceIdentity == null);
        edition.Venue!.Names.Single().Value.Should().Be("François Hall");
        edition.Venue.OriginalLocality!.Value.Should().Be("Montréal, QC");
        edition.Text.Single().Text.Value.Should().Contain("&amp;");
        ValidateAllEvidence(input, result);
        ValidateRawText(
            result
                .Setlists.SelectMany(setlist => setlist.Text)
                .Concat(edition.Passages.SelectMany(passage => passage.Annotations))
        );
    }

    [Test]
    public void PlainJerryTitlesDoNotInventTransitionsOrSongIdentities()
    {
        var input = Html(
            """
            <p class="venue-name">Hall 🎤</p><p class="venue-name">Band</p>
            <ol><li>First Song</li><li>Second Song &gt;</li><li><a href="https://unrelated.invalid/song/name">Third Song</a></li></ol>
            """,
            "jerrygarcia.com",
            "mirror_show_page",
            "jerry/show",
            "jerry-mirror-html:1"
        );
        var result = decoder.Decode(input);
        var passages = result.Setlists.Single().Passages;
        passages[0].Title.Value.Should().Be("First Song");
        passages[0].TransitionMarker.Should().BeNull();
        passages[1].Title.Value.Should().Be("Second Song");
        passages[1].TransitionMarker!.Value.Should().Be(">");
        passages[1].TransitionMarker!.Evidence.Quote.Should().Be("&gt;");
        passages.Should().OnlyContain(passage => passage.SongIdentities.Count == 0);
        result.Setlists.Single().LiteralDate.Should().BeNull();
        ValidateAllEvidence(input, result);
    }

    [Test]
    public void AttributeLookingTextInsideAnotherValueCannotSupplyDateOrTooltip()
    {
        var input = Html(
            """
            <p class="venue-name">Hall</p><p class="venue-name">Band</p>
            <time data-note=' datetime="1900-01-01"' datetime="1970-xx-xx"></time>
            <ol><li>Song</li></ol>
            """,
            "jerrygarcia.com",
            "mirror_show_page",
            "jerry/show",
            "jerry-mirror-html:1"
        );
        var result = decoder.Decode(input);
        var date = result.Setlists.Single().LiteralDate!;
        date.Value.Should().Be("1970-xx-xx");
        date.Evidence.Quote.Should().Be("1970-xx-xx");
        ValidateAllEvidence(input, result);

        var page = Html(
            """
            <div class="setlist-container"><div class="setlist-body"><span class="set-label">Set 1</span>
            <a class="setlist-song" href="/song/song" data-note=' title="Wrong note"' title="Actual &amp; raw note">Song</a>
            </div></div>
            """,
            "phish.net",
            "ratings",
            "1970-01-01",
            "phish-net-http-v1"
        );
        var parsed = decoder.Decode(page);
        parsed
            .Setlists.Single()
            .Passages.Single()
            .Annotations.Single()
            .Text.Value.Should()
            .Be("Actual &amp; raw note");
        ValidateAllEvidence(page, parsed);
    }

    [Test]
    public void JerryPlaceholderDoesNotInventAnEdition()
    {
        var input = Html(
            "<p>No show details</p>",
            "jerrygarcia.com",
            "mirror_show_page",
            "jerry/show",
            "jerry-mirror-html:1"
        );
        var result = decoder.Decode(input);
        result.Setlists.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == "missing_jerry_show_details");
        ValidateAllEvidence(input, result);
    }

    [TestCase(
        "1992-11-21",
        "The Landlady",
        "PRITCHARD GYM, SB SPORTS COMPLEX, SUNY STONY BROOK",
        ">",
        30
    )]
    [TestCase("1997-11-22", "Mike's Song", "HAMPTON COLISEUM", "->", 15)]
    public void RealPhishNetPagesRetainOrderedSetsFootnotesVenueAndOriginalSpans(
        string date,
        string firstSong,
        string venue,
        string transition,
        int passageCount
    )
    {
        var raw = File.ReadAllText(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                $"setlist-{date}.html"
            )
        );
        var input = Html(raw, "phish.net", "ratings", date, "phish-net-http-v1");
        var result = decoder.Decode(input);
        var edition = result.Setlists.Should().ContainSingle().Subject;
        edition.Ordered.Should().BeTrue();
        edition.Origin.Scope.Should().Be(ProviderObservationScope.PageMember);
        edition.LiteralDate!.Value.Should().Be(date);
        edition.Performers.Single().Name!.Value.Should().Be("PHISH");
        edition.Sets.Select(set => set.Label!.Value).Should().Equal("SET 1", "SET 2", "ENCORE");
        edition.Passages.Should().HaveCount(passageCount);
        result.Issues.Should().NotContain(issue => issue.Code == "html_source_span_unavailable");
        edition.Passages[0].Title.Value.Should().Be(firstSong);
        edition.Passages[0].TransitionMarker!.Value.Should().Be(transition);
        edition.Venue!.Names.Select(name => name.Value).Should().Equal(venue);
        edition.Venue.Identities.Should().ContainSingle();
        edition
            .Passages.Should()
            .Contain(passage => passage.Annotations.Any(note => note.Field == "footnote"));
        edition.Text.Should().Contain(text => text.Field == "footnotes");
        edition.Text.Should().Contain(text => text.Field == "setlist_notes");
        edition.Passages.Should().OnlyContain(passage => passage.OccurrenceIdentity == null);
        if (date == "1992-11-21")
        {
            edition
                .Passages.Count(passage => passage.Title.Value == "Hold Your Head Up")
                .Should()
                .Be(2);
            edition.Passages.Count(passage => passage.Title.Value == "Llama").Should().Be(2);
            edition
                .Passages.Where(passage => passage.Title.Value == "Llama")
                .Select(passage => passage.Ordinal)
                .Distinct()
                .Should()
                .HaveCount(2);
        }
        else
        {
            edition
                .Passages[0]
                .Annotations.Should()
                .Contain(note =>
                    note.Field == "info" && note.Text.Value.Contains("Single jam format")
                );
        }
        ValidateAllEvidence(input, result);
        ValidateRawText(
            edition.Text.Concat(edition.Passages.SelectMany(passage => passage.Annotations))
        );
    }

    [Test]
    public void ArtistIndexHeaderLinkIsNotASetlistPermalink()
    {
        var input = Html(
            """
            <div class="setlist-container"><span class="setlist-date"><a href="/setlists/phish">PHISH</a></span>
            <div class="setlist-body"><span class="set-label">Set 1</span><a class="setlist-song" href="/song/song">Song</a></div></div>
            """,
            "phish.net",
            "ratings",
            "1992-11-21",
            "phish-net-http-v1"
        );
        var result = decoder.Decode(input);
        result.Setlists.Single().Origin.Item.Should().Be(input.Resource);
        result.Setlists.Single().Origin.Scope.Should().Be(ProviderObservationScope.PageMember);
        result.Issues.Should().ContainSingle(issue => issue.Code == "missing_setlist_permalink");
        ValidateAllEvidence(input, result);
    }

    [Test]
    public void MinimalRatingsPageHasNoInventedPerformanceAccount()
    {
        var input = Html(
            "Overall: 4.2/5 (10 ratings)<div class='tpc-comment review'>Recorded by Review Author</div>",
            "phish.net",
            "ratings",
            "1992-11-21",
            "phish-net-http-v1"
        );
        var result = decoder.Decode(input);
        result.Setlists.Should().BeEmpty();
        result.Recordings.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        ValidateAllEvidence(input, result);
    }

    [Test]
    public void ShowCatalogPreservesOtherArtistsAndLiteralDatesWithoutInventingOrderedSongs()
    {
        var input = Json(
            """
            {"error":false,"data":[{"showid":12,"artistid":3,"artist_name":"Trey Anastasio Band","showdate":"198x-xx-xx",
            "venueid":99,"venue":"Hall","city":"City","state":"Region","country":"US","setlist_notes":"A &amp; B; with guest"},null]}
            """,
            "shows",
            "v5"
        );
        var result = decoder.Decode(input);
        var edition = result.Setlists.Should().ContainSingle().Subject;
        edition.Origin.Scope.Should().Be(ProviderObservationScope.CatalogMember);
        edition.Origin.Item.Should().Be(new NormalizationResourceKey("phish.net", "show", "12"));
        edition.Performers.Single().Name!.Value.Should().Be("Trey Anastasio Band");
        edition.Performers.Single().Identities.Single().Identity.ProviderKey.Should().Be("3");
        edition.LiteralDate!.Value.Should().Be("198x-xx-xx");
        edition.Ordered.Should().BeFalse();
        edition.Passages.Should().BeEmpty();
        edition.Sets.Should().BeEmpty();
        edition.Text.Single().Text.Value.Should().Be("A &amp; B; with guest");
        result.Issues.Should().ContainSingle(issue => issue.Code == "expected_show_object");
        ValidateAllEvidence(input, result);
    }

    [Test]
    public void ReviewAuthorsNeverBecomeRecordingTapers()
    {
        var input = Json(
            """{"error":false,"data":[{"uid":42,"username":"Tape Wizard","review_text":"Recorded by Pat"}]}""",
            "reviews",
            "1992-11-21"
        );
        var result = decoder.Decode(input);
        result.Recordings.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
        result
            .Issues.Should()
            .ContainSingle(issue => issue.Code == "review_is_not_recording_credit");
        ValidateAllEvidence(input, result);
    }

    [TestCase("{\"error\":true,\"data\":[]}", "provider_error")]
    [TestCase("{\"error\":false,\"data\":{}}", "expected_phishnet_data_array")]
    public void InvalidPhishNetEnvelopesProduceNoClaims(string raw, string issue)
    {
        var input = Json(raw, "shows", "v5");
        var result = decoder.Decode(input);
        result.Setlists.Should().BeEmpty();
        result.Venues.Should().BeEmpty();
        result.Issues.Should().ContainSingle(item => item.Code == issue);
        ValidateAllEvidence(input, result);
    }

    private static NormalizationInputRevision Html(
        string raw,
        string provider,
        string resourceType,
        string key,
        string version,
        string charset = "utf-8"
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new(provider, resourceType, key),
            CaptureVersion = version,
            MediaType = "text/html; charset=" + charset,
            Payload = Encoding.GetEncoding(charset).GetBytes(raw),
        };

    private static NormalizationInputRevision Json(string raw, string resourceType, string key) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("phish.net", resourceType, key),
            CaptureVersion = "phish-net-http-v1",
            MediaType = "application/json",
            Payload = Encoding.UTF8.GetBytes(raw),
        };

    private static void ValidateRawText(IEnumerable<ProviderTextObservation> text)
    {
        foreach (var field in text)
        {
            field.Text.Value.Should().Be(field.Text.Evidence.Quote);
        }
    }

    private static void ValidateAllEvidence(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        Validate(JsonSerializer.SerializeToElement(result));
        return;

        void Validate(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("JsonPointer", out _))
                {
                    document.ValidateEvidence(value.Deserialize<NormalizedEvidenceReference>()!);
                }
                foreach (var property in value.EnumerateObject())
                {
                    Validate(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Validate(item);
                }
            }
        }
    }
}
