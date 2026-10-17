using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationSetlistExtractor
{
    private readonly NormalizationProviderDecoder decoder = new();
    private readonly NormalizationSetlistExtractor extractor = new();

    [Test]
    public void SetlistFmPreservesRepeatedSongsEmptyRepeatedSetsTapeAndPerformanceAnnotations()
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var account = decoded.Setlists.Single();

        var result = Extract(input, decoded);
        var proposal = result.Accounts.Should().ContainSingle().Subject;

        proposal.Account.Should().BeSameAs(account);
        proposal
            .Account.Sets.Select(set => set.Label?.Value)
            .Should()
            .Equal("First", "First", null);
        proposal.Account.Sets[1].EncoreNumber!.Value.Should().Be(1);
        proposal
            .Passages.Select(p => p.Passage.Title.Value)
            .Should()
            .Equal("Song", "Song", "Reprise");
        proposal.Passages.Select(p => p.Passage.Ordinal).Should().Equal(1, 2, 4);
        proposal.Passages.Select(p => p.Passage.SetOrdinal).Should().Equal(1, 1, 3);
        proposal.Passages.Take(2).Select(p => p.Passage.Tape!.Value).Should().Equal(true, false);
        proposal
            .Passages[0]
            .Passage.PerformerAnnotations.Select(a => a.Kind)
            .Should()
            .Equal("cover_artist", "guest_artist");
        proposal.Passages[0].Passage.Annotations.Single().Text.Value.Should().Be("tease");
        result.DecodeIssues.Should().ContainSingle(issue => issue.Code == "missing_passage_title");
        result
            .Accounts.SelectMany(a => a.Passages)
            .Select(p => p.ProposalUuid)
            .Should()
            .OnlyHaveUniqueItems();
        decoded
            .Repertoire.Should()
            .BeEmpty("performance cover annotations do not establish authorship");
        proposal.Passages.Should().OnlyContain(p => p.Passage.OccurrenceIdentity == null);
    }

    [Test]
    public void PhantasyPreservesFootnotesUnresolvedReferencesSegueAndCreditProse()
    {
        // Synthetic response using the checked-in Phantasy provider contract.
        var input = Input(
            "phantasytour.com",
            "show",
            "4",
            "phantasy-tour-http-v1",
            """
            {"data":{"id":4,"band":{"id":7,"name":"Band"},"credits":["Compiled by Someone"],
            "footnotes":[{"id":2,"note":"Guest guitarist"},{"id":3,"note":"Unreferenced note"}],
            "sets":[{"id":1,"name":"First","songs":[{"id":9,"name":"Song","segue":true,"footnotes":[2,99]},{"id":9,"name":"Song","segue":false}]},{"id":2,"songs":[]}]}}
            """
        );
        var result = Extract(input, decoder.Decode(input));
        var account = result.Accounts.Single();

        account.Account.Sets.Should().HaveCount(2);
        account.Passages.Select(p => p.Passage.Segue!.Value).Should().Equal(true, false);
        account.Passages[0].Passage.FootnoteNumbers.Select(n => n.Value).Should().Equal(2, 99);
        account.Passages[0].Passage.Annotations.Single().Text.Value.Should().Be("Guest guitarist");
        account
            .Account.Text.Should()
            .Contain(t => t.Field == "footnote" && t.Text.Value == "Unreferenced note");
        account
            .Account.Text.Should()
            .Contain(t => t.Field == "credits" && t.Text.Value == "Compiled by Someone");
        result.DecodeIssues.Should().ContainSingle(issue => issue.Code == "unresolved_footnote");
        account.Passages.Select(p => p.ProposalUuid).Should().OnlyHaveUniqueItems();
    }

    [TestCase("utf-8")]
    [TestCase("utf-16")]
    public void JerryRetainsOriginalUnicodeSpansEntityDecodedValuesAndEmptySet(string charset)
    {
        // Synthetic mirror HTML: supplementary characters precede cited titles deliberately.
        var input = Input(
            "jerrygarcia.com",
            "mirror_show_page",
            "jerry/show",
            "jerry-mirror-html:1",
            """
            <p class="venue-name">Hall 🎤</p><p class="venue-name">Jerry García Band</p>
            <section data-set="Early set"><ol><li><a href="/song/dark-star/">Dark &amp; Star</a> &gt;</li>
            <li><a href="/song/dark-star/">Dark &amp; Star</a>*</li></ol></section><ol data-set="Encore"></ol>
            """,
            "text/html; charset=" + charset,
            Encoding.GetEncoding(charset)
        );
        var decoded = decoder.Decode(input);
        var result = Extract(input, decoded);
        var account = result.Accounts.Single();

        account.Account.Should().BeSameAs(decoded.Setlists.Single());
        account.Account.Venue!.Names.Single().Value.Should().Be("Hall 🎤");
        account.Account.Sets.Select(s => s.Label!.Value).Should().Equal("Early set", "Encore");
        account
            .Passages.Select(p => p.Passage.Title.Value)
            .Should()
            .Equal("Dark & Star", "Dark & Star");
        account.Passages[0].Passage.TransitionMarker!.Evidence.Quote.Should().Be("&gt;");
        account.Passages[1].Passage.Annotations.Single().Text.Value.Should().Contain("*</li>");
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var passage in account.Passages)
        {
            document.ValidateEvidence(passage.Passage.Title.Evidence);
            passage.Passage.Title.Evidence.Quote.Should().Contain("&amp;");
        }
    }

    [TestCase("1992-11-21", 30)]
    [TestCase("1997-11-22", 15)]
    public void RealPhishNetFixturesPreserveAllSetsPassagesAndAnnotations(string date, int passages)
    {
        var raw = File.ReadAllText(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                $"setlist-{date}.html"
            )
        );
        var input = Input("phish.net", "ratings", date, "phish-net-http-v1", raw, "text/html");
        var decoded = decoder.Decode(input);
        var account = Extract(input, decoded).Accounts.Single();

        account.Account.Should().BeSameAs(decoded.Setlists.Single());
        account.Passages.Should().HaveCount(passages);
        account
            .Account.Sets.Select(set => set.Label!.Value)
            .Should()
            .Equal("SET 1", "SET 2", "ENCORE");
        account
            .Passages.Should()
            .Contain(p => p.Passage.Annotations.Any(a => a.Field == "footnote"));
        account.Account.Text.Should().Contain(t => t.Field == "setlist_notes");
        account.Passages.Should().OnlyContain(p => p.Passage.OccurrenceIdentity == null);
    }

    [Test]
    public void UnorderedCatalogAccountKeepsUnknownPositionsAndIndependentMissingBindings()
    {
        // Synthetic catalog member has no ordered passage assertions.
        var input = Input(
            "phish.net",
            "shows",
            "v5",
            "phish-net-http-v1",
            """{"error":false,"data":[{"showid":12,"artistid":3,"artist_name":"Band","showdate":"198x-xx-xx","setlist_notes":"Original account prose"},null]}"""
        );
        var decoded = decoder.Decode(input);
        var result = Extract(input, decoded);

        result.Accounts.Single().Account.Ordered.Should().BeFalse();
        result.Accounts.Single().Account.Sets.Should().BeEmpty();
        result.Accounts.Single().Passages.Should().BeEmpty();
        result.Accounts.Single().Account.LiteralDate!.Value.Should().Be("198x-xx-xx");
        result.DecodeIssues.Should().ContainSingle(issue => issue.Code == "expected_show_object");
    }

    [Test]
    public void ProposalKeysReplayAcrossDeserializationAndSeparateRepeatedSongsAndInputRevisions()
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var prepared = Prepare(input, decoded.Setlists);
        var restored = JsonSerializer.Deserialize<NormalizationSetlistPreparedInput>(
            JsonSerializer.Serialize(prepared)
        )!;

        var first = extractor.Extract(input, decoded, restored);
        var replay = extractor.Extract(input, decoded, restored);
        var otherInput = SetlistFm();
        var other = Extract(otherInput, decoder.Decode(otherInput));

        first
            .Accounts.Single()
            .Passages.Select(p => p.ProposalUuid)
            .Should()
            .Equal(replay.Accounts.Single().Passages.Select(p => p.ProposalUuid));
        first
            .Accounts.Single()
            .Passages.Select(p => p.ProposalUuid)
            .Should()
            .NotIntersectWith(other.Accounts.Single().Passages.Select(p => p.ProposalUuid));
        first.Accounts.Single().Passages.Select(p => p.ProposalUuid).Should().OnlyHaveUniqueItems();
        first
            .Accounts.Single()
            .Account.Should()
            .BeSameAs(
                decoded.Setlists.Single(),
                "binder copies do not replace original decoded accounts"
            );
    }

    [Test]
    public void ConflictingDuplicateAccountsRetainTheirExactOriginsAndRequireCompletePreparation()
    {
        // A provider page can repeat an account identity with conflicting content.
        var input = Input(
            "setlist.fm",
            "setlist-page",
            "artist:page=1",
            "setlist-fm-http-v1",
            """{"page":1,"itemsPerPage":20,"total":2,"setlist":[{"id":"same","sets":{"set":{"song":{"name":"First"}}}},{"id":"same","sets":{"set":{"song":{"name":"Second"}}}}]}"""
        );
        var decoded = decoder.Decode(input);
        var result = Extract(input, decoded);

        result.Accounts.Should().HaveCount(2);
        result
            .Accounts.Select(a => a.Account.Origin.Evidence.JsonPointer)
            .Should()
            .Equal("/setlist/0", "/setlist/1");
        result
            .Accounts.SelectMany(a => a.Passages)
            .Select(p => p.Passage.Title.Value)
            .Should()
            .Equal("First", "Second");
        var incomplete = Prepare(input, decoded.Setlists.Take(1).ToArray());
        var extract = () => extractor.Extract(input, decoded, incomplete);
        extract.Should().Throw<ArgumentException>().WithMessage("*every exact decoded account*");
    }

    [Test]
    public void ExactDuplicateOriginsRemainSeparateAccountObservations()
    {
        var input = SetlistFm();
        var original = decoder.Decode(input);
        // Deliberate duplicate decoder observation, not a second provider assertion.
        var decoded = original with
        {
            Setlists = [original.Setlists[0], original.Setlists[0]],
        };

        var result = Extract(input, decoded);

        result.Accounts.Should().HaveCount(2);
        result
            .Accounts.Should()
            .OnlyContain(account => ReferenceEquals(account.Account, original.Setlists[0]));
        result
            .Accounts.SelectMany(account => account.Passages)
            .Select(passage => passage.ProposalUuid)
            .Should()
            .OnlyHaveUniqueItems();
        result
            .Accounts.SelectMany(account => account.Passages)
            .Select(passage => passage.ProposalUuid)
            .Should()
            .Equal(
                Extract(input, decoded)
                    .Accounts.SelectMany(account => account.Passages)
                    .Select(passage => passage.ProposalUuid)
            );
    }

    [Test]
    public void ForgedPreparedAccountCannotReplaceTheDecodedOriginal()
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var original = decoded.Setlists.Single();
        var forged = original with { Ordered = false };
        var extract = () => extractor.Extract(input, decoded, Prepare(input, [forged]));

        extract.Should().Throw<ArgumentException>().WithMessage("*every exact decoded account*");
    }

    [Test]
    public void ForgedDecodedScalarCannotUseAnUnchangedCitation()
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var account = decoded.Setlists.Single();
        var passages = account.Passages.ToArray();
        passages[0] = passages[0] with { Title = passages[0].Title with { Value = "Forged" } };
        var forged = decoded with { Setlists = [account with { Passages = passages }] };

        var extract = () => Extract(input, forged);

        extract.Should().Throw<ArgumentException>().WithMessage("*exact cited value*");
    }

    [Test]
    public void ForeignNestedEvidenceAndForgedIssueEvidenceAreRejected()
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var account = decoded.Setlists.Single();
        var passages = account.Passages.ToArray();
        passages[0] = passages[0] with
        {
            Tape = passages[0].Tape! with
            {
                Evidence = new(Guid.NewGuid(), "/setlist/0/sets/set/0/song/0/tape"),
            },
        };
        var foreign = decoded with { Setlists = [account with { Passages = passages }] };
        var badAccount = () => Extract(input, foreign);
        var badIssue = () =>
            Extract(input, decoded with { Issues = [new("foreign", new(Guid.NewGuid(), ""))] });

        badAccount.Should().Throw<ArgumentException>().WithMessage("*different input revision*");
        badIssue.Should().Throw<ArgumentException>().WithMessage("*different input revision*");
    }

    [TestCase("contributor")]
    [TestCase("revision")]
    [TestCase("binding")]
    public void PreparationCannotSubstituteItsInputOrBinding(string substitution)
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var prepared = Prepare(input, decoded.Setlists);
        prepared = substitution switch
        {
            "contributor" => prepared with
            {
                Contributor = new("setlist.fm", "setlist-page", "other:page=1"),
            },
            "revision" => prepared with
            {
                Input = prepared.Input with { InputRevisionUuid = Guid.NewGuid() },
            },
            _ => prepared with
            {
                Accounts =
                [
                    prepared.Accounts[0] with
                    {
                        Binding = prepared.Accounts[0].Binding with
                        {
                            InputRevisionUuid = Guid.NewGuid(),
                        },
                    },
                ],
            },
        };

        var extract = () => extractor.Extract(input, decoded, prepared);

        extract.Should().Throw<ArgumentException>();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InputBoundsFailExplicitlyWithoutTruncating(bool accounts)
    {
        var input = SetlistFm();
        var decoded = decoder.Decode(input);
        var account = decoded.Setlists.Single();
        var excessive = decoded with
        {
            Setlists = accounts
                ? Enumerable.Repeat(account, 1_001).ToArray()
                :
                [
                    account with
                    {
                        Passages = Enumerable.Repeat(account.Passages[0], 10_001).ToArray(),
                    },
                ],
        };

        var extract = () => Extract(input, excessive);

        extract.Should().Throw<ArgumentException>().WithMessage("*extraction bounds*");
    }

    private NormalizationSetlistInputExtraction Extract(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded
    ) => extractor.Extract(input, decoded, Prepare(input, decoded.Setlists));

    private static NormalizationSetlistPreparedInput Prepare(
        NormalizationInputRevision input,
        IReadOnlyList<ProviderSetlistObservation> accounts
    ) =>
        new(
            input.Resource,
            0,
            new(input.Uuid, input.Resource),
            accounts
                .Select(account => new NormalizationPreparedSetlistAccount(
                    account,
                    new(
                        input.Uuid,
                        input.Resource,
                        account.Origin,
                        new(NormalizationCatalogBindingStatus.Missing, null, "artist_missing"),
                        new(
                            NormalizationCatalogBindingStatus.Ambiguous,
                            null,
                            "imported_ambiguous"
                        ),
                        new(
                            NormalizationCatalogBindingStatus.ProviderConflict,
                            null,
                            "performance_conflict"
                        ),
                        [],
                        new(0)
                    )
                ))
                .ToArray()
        );

    private static NormalizationInputRevision SetlistFm() =>
        // Synthetic setlist.fm page; the missing title deliberately leaves ordinal 3 unused.
        Input(
            "setlist.fm",
            "setlist-page",
            "artist:page=1",
            "setlist-fm-http-v1",
            """
            {"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"one","eventDate":"03-02-1970",
            "artist":{"mbid":"artist-id","name":"Band"},"venue":{"id":"venue-id","name":"Hall","city":{"name":"Town","coords":{"lat":42.1,"long":-72.3}}},
            "sets":{"set":[{"name":"First","song":[{"name":"Song","tape":true,"cover":{"name":"Cover performer"},"with":{"name":"Guest"},"info":"tease"},{"name":"Song","tape":false},{}]},
            {"name":"First","encore":1,"song":[]},{"song":[{"name":"Reprise"}]}]},"info":"Show notes"}]}
            """
        );

    private static NormalizationInputRevision Input(
        string provider,
        string type,
        string key,
        string captureVersion,
        string payload,
        string mediaType = "application/json",
        Encoding? encoding = null
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new(provider, type, key),
            CaptureVersion = captureVersion,
            Payload = (encoding ?? Encoding.UTF8).GetBytes(payload),
            MediaType = mediaType,
        };
}
