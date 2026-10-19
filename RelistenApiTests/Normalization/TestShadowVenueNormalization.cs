using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Public adapter behavior for unaccepted corpus hypotheses, never an accuracy qualification.</summary>
[TestFixture]
public sealed class TestShadowVenueNormalization
{
    [Test]
    public void ModernMsgActuallyJoinsTheFullNameAcrossArtistsWithOriginalEvidenceAndKeys()
    {
        var msg = Venue(
            113002,
            "MSG",
            1,
            "NY, NY",
            uuid: Guid.Parse("bcdf830e-9543-27fd-f851-db137f524991")
        );
        var full = Venue(5642, "Madison Square Garden", 1, "New York, New York");
        var other = Venue(300, "Madison Square Garden", 2, "New York City, NY");
        var association = Source(1, 113002, 5642, "2010-11-21");
        var adapter = Observe(msg, full, other, association, Source(2, 300, 300, "2017-12-31"));

        var result = Result(adapter, msg);
        var proposal = Proposal(result);
        var normalized = proposal.Normalization!;
        var hypothesis = normalized.Hypothesis!;
        proposal.DisplayName.Should().Be("Madison Square Garden");
        proposal
            .ProposedPlaceKey.Should()
            .Be(Proposal(Result(adapter, full)).ProposedPlaceKey)
            .And.Be(Proposal(Result(adapter, other)).ProposedPlaceKey);
        result.InputUuid.Should().Be(msg.GetProperty("row").GetProperty("uuid").GetGuid());
        result
            .EntityKeys.Should()
            .Contain(proposal.ProposedPlaceKey)
            .And.Contain(
                ShadowCatalogIdentity.Key("venue", "catalog\n" + result.InputUuid.ToString("D"))
            );
        normalized.OriginalName.Should().Be("MSG");
        normalized.DisplayLocation.Should().Be("New York City, New York, US");
        normalized.GeographicKey.Should().Be("geonames:5128581");
        normalized.MembershipBasis.Should().Be("initialism_with_source_show_context");
        hypothesis.MemberCount.Should().Be(3);
        hypothesis.ArtistCount.Should().Be(2);
        hypothesis.SourceObservationCount.Should().Be(2);
        hypothesis
            .SourceExamples.Should()
            .Contain(value => value.SourceVenueId == 113002 && value.ShowVenueId == 5642);
        hypothesis.PremisesContinuityVerified.Should().BeFalse();
        hypothesis.RecordingDates.DescribesNameValidity.Should().BeFalse();
        proposal.AcceptedIdentity.Should().BeFalse();
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(result.Evidence).GetProperty("NativeVenue"),
                msg.GetProperty("row")
            )
            .Should()
            .BeTrue();
        adapter.DomainsFor("source_sample").Should().BeEmpty();
    }

    [Test]
    public void InitialismExpansionIsGenericAndKeepsObservedCaseVariants()
    {
        var full = Venue(1, "North Harbor Live", 1);
        var shortName = Venue(2, "NHL", 2);
        var lowercase = Venue(3, "nhl", 3);
        var adapter = Observe(
            full,
            shortName,
            lowercase,
            Source(1, 1, 1),
            Source(2, 2, 2),
            Source(3, 3, 3)
        );

        var canonical = Proposal(Result(adapter, full));
        foreach (var record in new[] { shortName, lowercase })
        {
            var proposed = Proposal(Result(adapter, record));
            proposed.DisplayName.Should().Be("North Harbor Live");
            proposed.ProposedPlaceKey.Should().Be(canonical.ProposedPlaceKey);
            proposed
                .Normalization!.Hypothesis!.Names.Should()
                .Contain(["NHL", "nhl", "North Harbor Live"]);
        }
    }

    [Test]
    public void UnusedAliasJoinsAnObservedPlaceWithoutReplacingItsAnchorOrInventingUsage()
    {
        var full = Venue(10, "North Harbor Live", 1);
        var unused = Venue(1, "NHL", 2, "NY, NY");
        var source = Source(1, 10, 10);
        var original = Proposal(Result(Observe(full, source), full));
        var adapter = Observe(full, unused, source);

        var joined = Proposal(Result(adapter, unused));
        joined.ProposedPlaceKey.Should().Be(original.ProposedPlaceKey);
        joined.DisplayName.Should().Be("North Harbor Live");
        joined.Normalization!.OriginalName.Should().Be("NHL");
        joined.Normalization.Hypothesis!.AnchorVenueUuid.Should().Be(Id(10));
        joined.Normalization.Hypothesis.MemberCount.Should().Be(2);
        joined.Normalization.Hypothesis.SourceObservationCount.Should().Be(1);
        Result(adapter, unused).InputUuid.Should().Be(Id(1));
        Proposal(Result(Observe(unused), unused)).Normalization!.Hypothesis.Should().BeNull();
    }

    [Test]
    public void MissingCountryUsesOnlyAnExactAliasOfTheSameRecordingsResolvedLocality()
    {
        var full = Venue(1, "North Harbor Live", 1);
        var missing = Venue(2, "NHL", 1, "NYC");
        var adapter = Observe(full, missing, Source(1, 2, 1));

        var result = Result(adapter, missing);
        var proposed = Proposal(result);
        result.Outcome.Should().Be("proposed");
        proposed.ProposedPlaceKey.Should().Be(Proposal(Result(adapter, full)).ProposedPlaceKey);
        proposed.Normalization!.DisplayLocation.Should().Be("New York City, New York, US");
        proposed
            .Locality.CountryCode.Should()
            .BeNull("the original incomplete wording stays retained");
        proposed.Locality.OriginalWording.Should().Be("NYC");
        Proposal(Result(Observe(full, missing, Source(1, 1, 1)), missing))
            .Normalization!.Hypothesis.Should()
            .BeNull("a global city alias cannot invent a missing country");
    }

    [TestCase("NYC", "Unrelated Hall", "New York, NY", null, null)]
    [TestCase("NYC", "North Harbor Live", "Los Angeles, CA", null, null)]
    [TestCase("NYC, Canada", "North Harbor Live", "New York, NY", null, null)]
    [TestCase("NYC", "North Harbor Live", "New York, NY", "1 First Street", "2 Second Street")]
    public void SameDayContextCannotOverrideContradictoryNamesGeographyOrAddresses(
        string locality,
        string fullName,
        string fullLocality,
        string? sourceAddress,
        string? showAddress
    )
    {
        var full = Venue(1, fullName, 1, fullLocality, address: showAddress);
        var missing = Venue(2, "NHL", 1, locality, address: sourceAddress);
        var adapter = Observe(full, missing, Source(1, 2, 1));

        Proposal(Result(adapter, missing)).Normalization!.Hypothesis.Should().BeNull();
    }

    [Test]
    public void AForeignArtistsVenueRowCannotSupplyAMissingCountryThroughAMalformedAssociation()
    {
        var foreign = Venue(1, "North Harbor Live", 2);
        var missing = Venue(2, "NHL", 1, "NYC");
        var adapter = Observe(foreign, missing, Source(2, 1, 1), Source(1, 2, 1));

        Proposal(Result(adapter, missing)).Normalization!.Hypothesis.Should().BeNull();
    }

    [TestCase("Madison Square Garden", "MSG")]
    [TestCase("North Harbor Live", "NHL")]
    public void RedundantLocalityInsideAVenueNameDoesNotCreateAFalseSourceShowConflict(
        string fullName,
        string initialism
    )
    {
        var full = Venue(10, fullName, 2);
        var suffixed = Venue(1, fullName + ", NYC, NY", 1);
        var shortName = Venue(2, initialism, 1);
        var original = Proposal(Result(Observe(full, Source(2, 10, 10)), full));
        var adapter = Observe(full, suffixed, shortName, Source(2, 10, 10), Source(1, 2, 1));

        foreach (var record in new[] { full, suffixed, shortName })
        {
            var proposed = Proposal(Result(adapter, record));
            proposed.ProposedPlaceKey.Should().Be(original.ProposedPlaceKey);
            proposed.DisplayName.Should().Be(fullName);
            proposed.Normalization!.Hypothesis!.AnchorVenueUuid.Should().Be(Id(10));
        }
        Proposal(Result(adapter, suffixed))
            .Normalization!.OriginalName.Should()
            .Be(fullName + ", NYC, NY");
    }

    [Test]
    public void ContradictoryGeographicSuffixIsRetainedAndCannotEstablishTheInitialism()
    {
        var full = Venue(1, "North Harbor Live", 1);
        var suffixed = Venue(2, "North Harbor Live, Los Angeles, CA", 1);
        var shortName = Venue(3, "NHL", 1);
        var adapter = Observe(full, suffixed, shortName, Source(1, 1, 1), Source(1, 3, 2));

        Proposal(Result(adapter, shortName)).Normalization!.Status.Should().Be("ambiguous");
        Proposal(Result(adapter, suffixed)).Normalization!.Hypothesis.Should().BeNull();
    }

    [Test]
    public void ASharedInitialismCannotBridgeTwoDifferentFullNameAnchors()
    {
        var first = Venue(1, "East Bay Hall", 1);
        var second = Venue(2, "Electric Bridge House", 2);
        var abbreviation = Venue(3, "EBH", 3);
        var adapter = Observe(
            first,
            second,
            abbreviation,
            Source(1, 1, 1),
            Source(2, 2, 2),
            Source(3, 3, 3)
        );

        var uncertain = Result(adapter, abbreviation);
        uncertain.Outcome.Should().Be("unresolved");
        Proposal(uncertain).Normalization!.Status.Should().Be("ambiguous");
        Proposal(uncertain).Normalization!.CandidateCount.Should().Be(2);
        Proposal(uncertain).Normalization!.Hypothesis.Should().BeNull();
        Proposal(Result(adapter, first))
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, second)).ProposedPlaceKey);
    }

    [Test]
    public void AnExactSourceShowAssociationSelectsOneCompetingExpansionWithoutJoiningTheOther()
    {
        var first = Venue(1, "East Bay Hall", 1);
        var second = Venue(2, "Electric Bridge House", 2);
        var abbreviation = Venue(3, "EBH", 1);
        var adapter = Observe(first, second, abbreviation, Source(1, 3, 1), Source(2, 2, 2));

        Proposal(Result(adapter, abbreviation))
            .ProposedPlaceKey.Should()
            .Be(Proposal(Result(adapter, first)).ProposedPlaceKey);
        Proposal(Result(adapter, second))
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, first)).ProposedPlaceKey);
        Proposal(Result(adapter, abbreviation)).Normalization!.CandidateCount.Should().Be(2);
    }

    [Test]
    public void ContradictorySourceShowAssociationsKeepTheAbbreviationAmbiguous()
    {
        var first = Venue(1, "East Bay Hall", 1);
        var second = Venue(2, "Electric Bridge House", 1);
        var abbreviation = Venue(3, "EBH", 1);
        var adapter = Observe(
            first,
            second,
            abbreviation,
            Source(1, 3, 1, "2010-11-21"),
            Source(1, 3, 2, "2010-11-22")
        );

        Proposal(Result(adapter, abbreviation)).Normalization!.Hypothesis.Should().BeNull();
        Proposal(Result(adapter, abbreviation)).Normalization!.Status.Should().Be("ambiguous");
        Proposal(Result(adapter, first))
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, second)).ProposedPlaceKey);
    }

    [Test]
    public void KnownAddressesSplitHistoricalPremisesAndAnUnlocatedRowCannotBridgeThem()
    {
        var old = Venue(1, "Knitting Factory", 1, address: "47 East Houston Street");
        var newer = Venue(2, "Knitting Factory", 2, address: "74 Leonard Street");
        var unknown = Venue(3, "Knitting Factory", 3);
        var adapter = Observe(
            old,
            newer,
            unknown,
            Source(1, 1, 1, "1989-05-01"),
            Source(2, 2, 2, "2003-05-01"),
            Source(3, 3, 3)
        );

        Proposal(Result(adapter, old))
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, newer)).ProposedPlaceKey);
        var ambiguity = Proposal(Result(adapter, unknown)).Normalization!;
        ambiguity.Status.Should().Be("ambiguous");
        ambiguity.CandidateCount.Should().Be(2);
        ambiguity.Hypothesis.Should().BeNull();
    }

    [Test]
    public void SameNamedTheatresInDifferentCitiesRemainDifferentHypotheses()
    {
        var first = Venue(1, "Greek Theatre", 1, "Berkeley, CA");
        var second = Venue(2, "Greek Theatre", 2, "Los Angeles, CA");
        var adapter = Observe(first, second, Source(1, 1, 1), Source(2, 2, 2));

        var left = Proposal(Result(adapter, first));
        var right = Proposal(Result(adapter, second));
        left.Normalization!.Hypothesis.Should().NotBeNull();
        right.Normalization!.Hypothesis.Should().NotBeNull();
        left.ProposedPlaceKey.Should().NotBe(right.ProposedPlaceKey);
        left.Normalization.GeographicKey.Should().NotBe(right.Normalization.GeographicKey);
    }

    [Test]
    public void ARoomAssociationCannotRouteAnAmbiguousArenaAbbreviationIntoTheArena()
    {
        var arena = Venue(1, "Madison Square Garden", 1);
        var room = Venue(2, "Theatre at Madison Square Garden", 1);
        var abbreviation = Venue(3, "MSG", 1);
        var adapter = Observe(arena, room, abbreviation, Source(1, 1, 1), Source(1, 3, 2));

        var unresolved = Proposal(Result(adapter, abbreviation));
        unresolved.Normalization!.Status.Should().Be("ambiguous");
        unresolved
            .Normalization.MembershipBasis.Should()
            .Be("conflicting_source_show_venue_accounts");
        unresolved.Normalization.Hypothesis.Should().BeNull();
        unresolved
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, arena)).ProposedPlaceKey);
        Proposal(Result(adapter, room)).Normalization!.Hypothesis.Should().BeNull();
        Proposal(Result(adapter, room))
            .ProtectedUnknowns.Should()
            .Contain("room_or_stage_parent_requires_evidence");
    }

    [Test]
    public void NamedRoomsAndExplicitNameHistoryDoNotAcquireAnArenaIdentity()
    {
        var arena = Venue(1, "Madison Square Garden", 1);
        var forum = Venue(2, "Felt Forum", 2);
        var historical = Venue(
            3,
            "Theatre at Madison Square Garden",
            3,
            history: "Felt Forum (1968–1989)"
        );
        var adapter = Observe(
            arena,
            forum,
            historical,
            Source(1, 1, 1),
            Source(2, 2, 2),
            Source(3, 3, 3)
        );

        Proposal(Result(adapter, arena))
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, forum)).ProposedPlaceKey);
        var retained = Proposal(Result(adapter, historical));
        retained.Normalization!.Hypothesis.Should().BeNull();
        retained.PastNames.Should().ContainSingle().Which.Name.Should().Be("Felt Forum");
    }

    [TestCase("artist")]
    [TestCase("show")]
    [TestCase("date")]
    public void MalformedSourceShowNeighborsCannotSupplyNormalizationContext(string mismatch)
    {
        var full = Venue(1, "North Harbor Live", 1);
        var competing = Venue(3, "New Harbor Lights", 2);
        var abbreviation = Venue(2, "NHL", 1);
        var valid = Source(1, 1, 1);
        var bad = Source(
            1,
            2,
            1,
            showArtist: mismatch == "artist" ? 2 : 1,
            sourceShowId: mismatch == "show" ? 99 : 1,
            showDay: mismatch == "date" ? "2010-11-22" : null
        );
        var adapter = Observe(full, competing, abbreviation, valid, Source(2, 3, 3), bad);

        Proposal(Result(adapter, abbreviation)).Normalization!.Hypothesis.Should().BeNull();
        Proposal(Result(adapter, full))
            .Normalization!.Hypothesis!.SourceObservationCount.Should()
            .Be(1);
    }

    [Test]
    public void CompleteMembershipSurvivesExampleBoundsAndRepeatedSourceObservation()
    {
        var full = Venue(1, "North Harbor Live", 1);
        var aliases = Enumerable.Range(2, 125).Select(id => Venue(id, "NHL", id)).ToArray();
        var rows = new[] { full }.Concat(aliases).ToArray();
        var sources = Enumerable.Range(1, 126).Select(id => Source(id, id, id)).ToArray();
        var adapter = Observe(rows.Concat(sources).Concat(sources).ToArray());

        foreach (var record in new[] { full, aliases.Last() })
        {
            var hypothesis = Proposal(Result(adapter, record)).Normalization!.Hypothesis!;
            hypothesis.MemberCount.Should().Be(126);
            hypothesis.ArtistCount.Should().Be(126);
            hypothesis.SourceObservationCount.Should().Be(126);
            hypothesis.MemberExamples.Should().HaveCount(8);
            hypothesis.MembersTruncated.Should().BeTrue();
            hypothesis.SourceExamples.Should().HaveCount(8);
            hypothesis.SourceExamplesTruncated.Should().BeTrue();
        }
    }

    [Test]
    public void AmbiguousExpansionCountsAreCompleteBeyondTheDisplayBound()
    {
        var names = Enumerable
            .Range(1, 21)
            .Select(id => Venue(id, $"Arbor{id} Building{id}", id))
            .ToArray();
        var abbreviation = Venue(30, "AB", 30);
        var sources = Enumerable.Range(1, 21).Append(30).Select(id => Source(id, id, id));
        var adapter = Observe(names.Append(abbreviation).Concat(sources).ToArray());

        var normalized = Proposal(Result(adapter, abbreviation)).Normalization!;
        normalized.Hypothesis.Should().BeNull();
        normalized.CandidateCount.Should().Be(21);
        normalized.CandidateKeys.Should().HaveCount(8);
        normalized.CandidatesTruncated.Should().BeTrue();
    }

    [Test]
    public void ObservationOrderReplayAndConflictingContextRecoveryDoNotInventStableAcceptedIds()
    {
        var full = Venue(1, "North Harbor Live", 1);
        var competing = Venue(3, "New Harbor Lights", 2);
        var abbreviation = Venue(2, "NHL", 1);
        var first = Source(1, 1, 1);
        var second = Source(1, 2, 1);
        var records = new[] { full, competing, abbreviation, first, second, Source(2, 3, 3) };
        var forward = Observe(records);
        var reverse = Observe(records.Reverse().ToArray());
        var original = Proposal(Result(forward, abbreviation));
        original.Should().BeEquivalentTo(Proposal(Result(reverse, abbreviation)));
        var altered = JsonSerializer.SerializeToElement(
            new
            {
                kind = "source_sample",
                row = new
                {
                    uuid = second.GetProperty("row").GetProperty("uuid").GetGuid(),
                    artist_id = 1,
                    venue_id = 99,
                    display_date = "2010-11-21",
                },
            }
        );
        forward.Observe(altered);
        Proposal(Result(forward, abbreviation)).Normalization!.Hypothesis.Should().BeNull();
        Proposal(Result(Observe(records), abbreviation)).Should().BeEquivalentTo(original);
        original.AcceptedIdentity.Should().BeFalse();
    }

    [Test]
    public async Task VenueOnlyBatchUsesSourceContextWithoutEmittingOtherDomainsAndResumesExactly()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "venue-only-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.jsonl");
            var output = Path.Combine(directory, "output");
            var records = new[]
            {
                JsonSerializer.SerializeToElement(
                    new
                    {
                        kind = "artist",
                        row = new
                        {
                            id = 1,
                            uuid = Id(100),
                            name = "Artist",
                        },
                        configured_provider_mappings = Array.Empty<object>(),
                    }
                ),
                Venue(1, "Madison Square Garden", 1),
                Venue(2, "MSG", 1, "NY, NY"),
                Source(1, 2, 1),
            };
            await File.WriteAllLinesAsync(input, records.Select(record => record.GetRawText()));
            await new ShadowCatalogBatch([Observe()]).RunRowsInlineAsync(input, output);
            var originals = ReadVenueBatch(output);
            originals.Should().HaveCount(2);
            originals
                .Should()
                .OnlyContain(row => row.GetProperty("domain").GetString() == "venues");
            originals
                .Select(row => row.GetProperty("proposal").GetProperty("displayName").GetString())
                .Should()
                .OnlyContain(name => name == "Madison Square Garden");
            originals
                .Select(row =>
                    row.GetProperty("proposal").GetProperty("proposedPlaceKey").GetString()
                )
                .Distinct()
                .Should()
                .ContainSingle();

            await new ShadowCatalogBatch([Observe()]).RunRowsInlineAsync(
                input,
                output,
                resume: true
            );
            ReadVenueBatch(output)
                .Select(row => row.GetRawText())
                .Should()
                .Equal(originals.Select(row => row.GetRawText()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement[] ReadVenueBatch(string output) =>
        Directory
            .GetFiles(output, "*.jsonl.gz", SearchOption.AllDirectories)
            .Order()
            .SelectMany(path =>
            {
                using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
                using var reader = new StreamReader(stream);
                var results = new List<JsonElement>();
                while (reader.ReadLine() is { } line)
                {
                    results.Add(JsonSerializer.Deserialize<JsonElement>(line));
                }
                return results;
            })
            .ToArray();

    private static ShadowVenueAdapter Observe(params JsonElement[] records)
    {
        var adapter = new ShadowVenueAdapter(
            new ShadowVenueGeography(
                [
                    Place(5128581, "New York City", "NY", "New York", ["NY", "NYC", "New York"]),
                    Place(5327684, "Berkeley", "CA", "California", []),
                    Place(5368361, "Los Angeles", "CA", "California", []),
                ],
                "synthetic-normalization-reference-v1"
            )
        );
        foreach (var record in records)
        {
            adapter.Observe(record);
        }
        return adapter;
    }

    private static ShadowGazetteerPlace Place(
        long id,
        string name,
        string region,
        string regionName,
        string[] aliases
    ) => new(id, name, "US", region, regionName, "PPL", 0, 0, aliases, [region, regionName]);

    private static ShadowCatalogResult Result(ShadowVenueAdapter adapter, JsonElement record)
    {
        var artist = record.GetProperty("row").GetProperty("artist_id").GetInt64();
        return adapter
            .Process(
                new(
                    record,
                    null,
                    new(artist, Id(artist), "Artist " + artist, ["archive.org"]),
                    "archive.org"
                )
            )
            .Should()
            .ContainSingle()
            .Which;
    }

    private static ShadowVenueProposal Proposal(ShadowCatalogResult result) =>
        result.Proposal.Should().BeOfType<ShadowVenueProposal>().Which;

    private static Guid Id(long id) => new((int)id, 0, 0, new byte[8]);

    private static JsonElement Venue(
        long id,
        string name,
        long artist,
        string location = "New York, NY",
        string? address = null,
        string? history = null,
        Guid? uuid = null
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "venue_sample",
                row = new
                {
                    id,
                    uuid = uuid ?? Id(id),
                    artist_id = artist,
                    name,
                    location,
                    address,
                    past_names = history,
                },
            }
        );

    private static JsonElement Source(
        long artist,
        long sourceVenue,
        long showVenue,
        string day = "2010-11-21",
        long? showArtist = null,
        long sourceShowId = 1,
        string? showDay = null
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "source_sample",
                row = new
                {
                    uuid = Guid.NewGuid(),
                    artist_id = artist,
                    venue_id = sourceVenue,
                    show_id = sourceShowId,
                    display_date = day,
                },
                show_context = new
                {
                    id = 1,
                    uuid = Id(100000 + sourceVenue),
                    artist_id = showArtist ?? artist,
                    venue_id = showVenue,
                    display_date = showDay ?? day,
                },
            }
        );
}
