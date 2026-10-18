using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Exercises population-wide shadow proposals through the adapter contract.
/// These fixtures protect identity boundaries; they are not a matching-accuracy evaluation.
/// </summary>
[TestFixture]
public sealed class TestShadowVenueAdapter
{
    [Test]
    public void NumberedStreetVenueNamesJoinAcrossArtistsWithoutChangingTheirCatalogIds()
    {
        var first = Venue("123 Pleasant Street", "Morgantown, WV", artist: 1);
        var second = Venue("123 Pleasant Street", "Morgantown, West Virginia", artist: 2);
        var adapter = Observe(first, second);

        var left = Result(adapter, first);
        var right = Result(adapter, second);
        var proposal = Proposal(left);

        left.InputUuid.Should().NotBe(right.InputUuid);
        proposal.ProposedPlaceKey.Should().Be(Proposal(right).ProposedPlaceKey);
        left.EntityKeys.Should().Contain(proposal.ProposedPlaceKey);
        right.EntityKeys.Should().Contain(proposal.ProposedPlaceKey);
        proposal
            .Locality.Should()
            .BeEquivalentTo(
                new
                {
                    City = "Morgantown",
                    Region = "WV",
                    CountryCode = "US",
                    Address = "123 Pleasant Street",
                    AddressEvidenceField = "name",
                }
            );
        proposal.Alternatives.Should().ContainSingle().Which.InputUuid.Should().Be(right.InputUuid);
        proposal.AcceptedIdentity.Should().BeFalse();
        left.Outcome.Should().Be("proposed");
    }

    [Test]
    public void EqualNamesAndCitiesRetrieveAlternativesWithoutJoiningPremises()
    {
        var first = Venue("The Fillmore", "San Francisco, CA", artist: 1);
        var second = Venue("The Fillmore", "San Francisco, California", artist: 2);
        var adapter = Observe(first, second);

        var left = Proposal(Result(adapter, first));
        var right = Proposal(Result(adapter, second));

        left.ProposedPlaceKey.Should().NotBe(right.ProposedPlaceKey);
        left.Locality.BrowseKey.Should().Be(right.Locality.BrowseKey);
        var alternative = left.Alternatives.Should().ContainSingle().Subject;
        alternative.Relationship.Should().Be("same_name_locality_premises_unresolved");
        alternative.Comparison.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        alternative.Comparison.ProposedRule.Should().BeNull();
        alternative
            .Comparison.Features.Should()
            .Contain(feature =>
                feature.Name == "locality" && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [Test]
    public void NamesakesInDifferentCitiesStaySeparate()
    {
        var first = Venue("Riviera Theatre", "Chicago, Illinois", artist: 1);
        var second = Venue("Riviera Theatre", "Charleston, SC", artist: 2);
        var adapter = Observe(first, second);

        var left = Proposal(Result(adapter, first));
        var right = Proposal(Result(adapter, second));

        left.ProposedPlaceKey.Should().NotBe(right.ProposedPlaceKey);
        left.Locality.BrowseKey.Should().NotBe(right.Locality.BrowseKey);
        left.Alternatives.Should()
            .ContainSingle()
            .Which.Relationship.Should()
            .Be("different_locality_or_location_history");
    }

    [Test]
    public void QualifiedVenueReferencesLinkArtistsButDoNotAcceptAnIdentity()
    {
        var first = Venue("Bowery Ballroom", "New York, New York", "setlistfm:33d61cdd", artist: 1);
        var second = Venue("Bowery Ballroom", "New York, NY", "setlistfm:33d61cdd", artist: 2);
        var adapter = Observe(first, second);

        var left = Proposal(Result(adapter, first));
        var right = Proposal(Result(adapter, second));

        left.ProposedPlaceKey.Should().Be(right.ProposedPlaceKey);
        left.ProviderReference!.Provider.Should().Be("setlistfm");
        left.Alternatives.Should()
            .ContainSingle()
            .Which.Relationship.Should()
            .Be("proposed_same_physical_place");
        left.Alternatives.Single()
            .Comparison.Disposition.Should()
            .NotBe(IdentityMatchDisposition.AutomaticMatch);
        left.Alternatives.Single()
            .Comparison.SupportedRules.Should()
            .NotContain(IdentityMatchRule.QualifiedEntityIdentifier);
        left.AcceptedIdentity.Should().BeFalse();
    }

    [Test]
    public void AProviderRecordWithDifferentStreetAddressesDoesNotEraseRelocation()
    {
        var first = Venue(
            "Knitting Factory",
            "New York, NY",
            "setlistfm:1234abcd",
            address: "47 East Houston Street"
        );
        var second = Venue(
            "Knitting Factory",
            "New York, NY",
            "setlistfm:1234abcd",
            address: "74 Leonard Street"
        );
        var adapter = Observe(first, second);

        var left = Proposal(Result(adapter, first));
        var right = Proposal(Result(adapter, second));

        left.ProposedPlaceKey.Should().NotBe(right.ProposedPlaceKey);
        left.ProtectedUnknowns.Should().Contain("provider_venue_name_location_or_scope_conflict");
        left.Alternatives.Should()
            .ContainSingle()
            .Which.Relationship.Should()
            .Be("different_address_or_relocation");
        left.Locality.Address.Should().Be("47 East Houston Street");
        right.Locality.Address.Should().Be("74 Leonard Street");
    }

    [Test]
    public void ASharedProviderReferenceCannotMergeARoomWithItsLargerVenue()
    {
        var room = Venue("Theatre at Madison Square Garden", "New York, NY", "setlistfm:1234abcd");
        var arena = Venue("Madison Square Garden", "New York, NY", "setlistfm:1234abcd");
        var adapter = Observe(room, arena);

        var roomProposal = Proposal(Result(adapter, room));
        var arenaProposal = Proposal(Result(adapter, arena));

        roomProposal.Scope.Should().Be("room");
        roomProposal.ProposedPlaceKey.Should().NotBe(arenaProposal.ProposedPlaceKey);
        roomProposal.ProviderReference.Should().Be(arenaProposal.ProviderReference);
    }

    [Test]
    public void ConflictingExplicitScopesUnderOneLegacyUuidRemainSeparateHypotheses()
    {
        var uuid = Guid.NewGuid();
        var building = Venue(
            "A Hall",
            "Chicago, IL",
            "setlistfm:1234abcd",
            uuid: uuid,
            scope: "building"
        );
        var room = Venue("A Hall", "Chicago, IL", "setlistfm:1234abcd", uuid: uuid, scope: "room");
        var otherArtist = Venue(
            "A Hall",
            "Chicago, IL",
            "setlistfm:1234abcd",
            artist: 2,
            scope: "building"
        );
        var adapter = Observe(building, room, otherArtist);

        var first = Result(adapter, building);
        var second = Result(adapter, room);

        first.Outcome.Should().Be("unresolved");
        second.Outcome.Should().Be("unresolved");
        Proposal(first).ProtectedUnknowns.Should().Contain("conflicting_snapshots_for_legacy_uuid");
        Proposal(first).ProposedPlaceKey.Should().NotBe(Proposal(second).ProposedPlaceKey);
        Proposal(first)
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, otherArtist)).ProposedPlaceKey);
    }

    [Test]
    public void NameHistoryRetainsUnicodeQuotesAndOnlyExplicitYearIntervals()
    {
        const string history =
            "Felt Forum (1968–1989); L'Étage 🎤; Bad range (2005–1990); Old Hall (١٩٦٨–١٩٨٩)";
        var record = Venue("Theatre at Madison Square Garden", "New York, NY", pastNames: history);
        var result = Result(Observe(record), record);
        var proposal = Proposal(result);

        proposal.PastNames.Should().HaveCount(4);
        proposal.PastNames[0].Name.Should().Be("Felt Forum");
        proposal.PastNames[0].FromYear.Should().Be(1968);
        proposal.PastNames[0].ToYear.Should().Be(1989);
        proposal.PastNames[1].Name.Should().Be("L'Étage 🎤");
        proposal.PastNames[1].FromYear.Should().BeNull();
        proposal.PastNames[2].FromYear.Should().BeNull();
        proposal.PastNames[3].Name.Should().Be("Old Hall (١٩٦٨–١٩٨٩)");
        proposal.PastNames[3].FromYear.Should().BeNull();
        foreach (var name in proposal.PastNames)
        {
            history
                .Substring(name.EvidenceOffset, name.EvidenceLength)
                .Should()
                .Be(name.EvidenceQuote);
        }
        JsonSerializer
            .SerializeToElement(result.Evidence)
            .GetProperty("NativeVenue")
            .GetProperty("past_names")
            .GetString()
            .Should()
            .Be(history);
        proposal
            .ProtectedUnknowns.Should()
            .Contain("past_names_do_not_establish_same_premises_or_complete_history");
    }

    [Test]
    public void CoordinatesWithUnknownLocalityRemainUnclassifiedAndDoNotJoin()
    {
        var first = Venue(
            "A Hall",
            "Unknown Location",
            latitude: 40.7142691,
            longitude: -74.0059729
        );
        var second = Venue(
            "A Hall",
            "Unknown Location",
            latitude: 40.7142691,
            longitude: -74.0059729
        );
        var adapter = Observe(first, second);

        var result = Result(adapter, first);
        var proposal = Proposal(result);

        result.Outcome.Should().Be("unresolved");
        proposal
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, second)).ProposedPlaceKey);
        proposal.Coordinates!.Kind.Should().Be("unknown");
        proposal.Coordinates.Latitude.Should().Be(40.7142691);
        proposal.Locality.City.Should().BeNull();
        proposal.EnrichmentTasks.Should().Contain("parse_unstructured_locality_or_multiple_places");
    }

    [Test]
    public void CompoundLocationsRemainUnresolvedWithoutRejectingAnAdministrativeAnd()
    {
        var compound = Venue(
            "House concert and Cat in the Cream",
            "North Olmsted, Ohio and Oberlin, Ohio"
        );
        var province = Venue("A Hall", "St John's, Newfoundland and Labrador, Canada");
        var adapter = Observe(compound, province);

        Result(adapter, compound).Outcome.Should().Be("unresolved");
        Proposal(Result(adapter, compound))
            .ProtectedUnknowns.Should()
            .Contain("possible_multiple_places_in_one_legacy_row");
        var single = Proposal(Result(adapter, province));
        single.Locality.City.Should().Be("St John's");
        single.Locality.Region.Should().Be("NL");
        single.Locality.CountryCode.Should().Be("CA");
        single.ProtectedUnknowns.Should().NotContain("possible_multiple_places_in_one_legacy_row");
    }

    [Test]
    public void OutputCoversEveryVenueAndIsIndependentOfObservationOrder()
    {
        var records = Enumerable
            .Range(0, 625)
            .Select(index =>
                Venue(
                    "Venue " + index,
                    index % 2 == 0 ? "Austin, TX" : "Unknown Location",
                    artist: index + 1
                )
            )
            .ToArray();
        var forward = Observe(records);
        var reverse = Observe(records.Reverse().ToArray());

        var first = records.Select(record => Result(forward, record)).ToArray();
        var second = records.Select(record => Result(reverse, record)).ToArray();

        first.Should().HaveCount(records.Length);
        first.Select(result => result.InputUuid).Should().OnlyHaveUniqueItems();
        first
            .Select(result => result.Outcome)
            .Should()
            .Contain("proposed")
            .And.Contain("unresolved");
        first
            .Select(result => Proposal(result).ProposedPlaceKey)
            .Should()
            .Equal(second.Select(result => Proposal(result).ProposedPlaceKey));
    }

    [Test]
    public void GazetteerAliasesShareGeographyWithoutMergingNamesakePremises()
    {
        var first = Venue("The Hall", "New York, NY", artist: 1);
        var second = Venue("The Hall", "NYC, New York", artist: 2);
        var adapter = Observe(Geography(), first, second);

        var left = Result(adapter, first);
        var right = Result(adapter, second);
        var locality = Proposal(left).Locality.Geography!;

        locality.ProposedGeographicKey.Should().Be("geonames:5128581");
        left.EntityKeys.Should().Contain(locality.ProposedGeographicKey);
        right.EntityKeys.Should().Contain(locality.ProposedGeographicKey);
        locality.Place!.FeatureCode.Should().Be("PPL");
        locality.HistoricalValidityKnown.Should().BeFalse();
        Proposal(left).ProposedPlaceKey.Should().NotBe(Proposal(right).ProposedPlaceKey);
        Proposal(left)
            .Alternatives.Single()
            .Relationship.Should()
            .Be("same_name_locality_premises_unresolved");
        Proposal(right).Locality.OriginalWording.Should().Be("NYC, New York");
    }

    [Test]
    public void QualifiedProviderVenueUsesResolvedAliasesButConflictingCitiesStaySeparate()
    {
        var first = Venue("The Hall", "New York, NY", "setlistfm:1234abcd", artist: 1);
        var alias = Venue("The Hall", "NYC, NY", "setlistfm:1234abcd", artist: 2);
        var adapter = Observe(Geography(), first, alias);

        Proposal(Result(adapter, first))
            .ProposedPlaceKey.Should()
            .Be(Proposal(Result(adapter, alias)).ProposedPlaceKey);

        var elsewhere = Venue(
            "The Hall",
            "Montréal, Quebec, Canada",
            "setlistfm:1234abcd",
            artist: 3
        );
        adapter.Observe(elsewhere);
        Proposal(Result(adapter, first))
            .ProposedPlaceKey.Should()
            .NotBe(Proposal(Result(adapter, alias)).ProposedPlaceKey);
        Proposal(Result(adapter, first))
            .ProtectedUnknowns.Should()
            .Contain("provider_venue_name_location_or_scope_conflict");
    }

    [Test]
    public void GazetteerDoesNotRankAmbiguousNamesOrDiscardUnknownRegionConstraints()
    {
        var ambiguous = Venue("A Hall", "Springfield, USA");
        var constrained = Venue("A Hall", "Springfield, IL");
        var wrongRegion = Venue("A Hall", "Springfield, California");
        var unknownRegion = Venue("A Hall", "Springfield, Made-up Region, USA");
        var missingCountry = Venue("A Hall", "Springfield");
        var absent = Venue("A Hall", "Unlisted Town, IL");
        var adapter = Observe(
            Geography(),
            ambiguous,
            constrained,
            wrongRegion,
            unknownRegion,
            missingCountry,
            absent
        );

        var candidates = Proposal(Result(adapter, ambiguous)).Locality.Geography!;
        candidates.Status.Should().Be("ambiguous_gazetteer_candidates");
        candidates.CandidateCount.Should().Be(2);
        candidates.ProposedGeographicKey.Should().BeNull();
        Result(adapter, ambiguous).Outcome.Should().Be("unresolved");
        Proposal(Result(adapter, constrained))
            .Locality.Geography!.ProposedGeographicKey.Should()
            .Be("geonames:101");
        Proposal(Result(adapter, wrongRegion)).Locality.Geography!.CandidateCount.Should().Be(0);
        Proposal(Result(adapter, unknownRegion)).Locality.Geography!.CandidateCount.Should().Be(0);
        Proposal(Result(adapter, missingCountry))
            .Locality.Geography!.Status.Should()
            .Be("country_required");
        Proposal(Result(adapter, absent))
            .Locality.Geography!.Status.Should()
            .Be("no_exact_gazetteer_candidate");
    }

    [Test]
    public void GeographicProposalsPreserveDistrictsDiacriticsAndUnknownHistory()
    {
        var first = Venue("A Hall", "Montréal, QC");
        var alias = Venue("A Hall", "Montreal, Quebec, Canada");
        var district = Venue("A Hall", "Brooklyn, New York");
        var city = Venue("A Hall", "New York, NY");
        var adapter = Observe(Geography(), first, alias, district, city);

        Proposal(Result(adapter, first))
            .Locality.Geography!.ProposedGeographicKey.Should()
            .Be(Proposal(Result(adapter, alias)).Locality.Geography!.ProposedGeographicKey);
        var borough = Proposal(Result(adapter, district)).Locality.Geography!;
        borough.Place!.FeatureCode.Should().Be("PPLX");
        borough
            .ProposedGeographicKey.Should()
            .NotBe(Proposal(Result(adapter, city)).Locality.Geography!.ProposedGeographicKey);
        borough.HistoricalValidityKnown.Should().BeFalse();
    }

    [Test]
    public void ExplicitGeographyCannotHideConflictingOrUnrecognizedLocationEvidence()
    {
        JsonElement Explicit(string country, string location) =>
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "venue_sample",
                    row = new
                    {
                        uuid = Guid.NewGuid(),
                        artist_id = 1,
                        name = "A Hall",
                        city = "New York",
                        country,
                        location,
                    },
                }
            );
        var conflicting = Explicit("US", "New York, Ontario, Canada");
        var unrecognized = Explicit("Unrecognized Country", "New York, NY");
        var adapter = Observe(Geography(), conflicting, unrecognized);

        Result(adapter, conflicting).Outcome.Should().Be("unresolved");
        Proposal(Result(adapter, conflicting))
            .Locality.Geography!.Status.Should()
            .Be("conflicting_catalog_geography");
        Result(adapter, unrecognized).Outcome.Should().Be("unresolved");
        Proposal(Result(adapter, unrecognized))
            .Locality.Geography!.ProposedGeographicKey.Should()
            .BeNull();
    }

    [Test]
    public void KnownCompactRegionFormatsResolveButExtraComponentsRemainUnknown()
    {
        var compact = Venue("A Hall", "New York NY");
        var combined = Venue("A Hall", "New York, NY USA");
        var extra = Venue("A Hall", "Springfield, MA, Illinois, USA");
        var adapter = Observe(Geography(), compact, combined, extra);

        Proposal(Result(adapter, compact))
            .Locality.Geography!.ProposedGeographicKey.Should()
            .Be("geonames:5128581");
        Proposal(Result(adapter, combined))
            .Locality.Geography!.ProposedGeographicKey.Should()
            .Be("geonames:5128581");
        Proposal(Result(adapter, extra))
            .Locality.OriginalWording.Should()
            .Be("Springfield, MA, Illinois, USA");
        Proposal(Result(adapter, extra))
            .Locality.Geography!.ProposedGeographicKey.Should()
            .BeNull();
        Result(adapter, extra).Outcome.Should().Be("unresolved");
    }

    [Test]
    public void ACanonicalCityNameCannotOverrideAnotherPlacesExactAlias()
    {
        var geography = new ShadowVenueGeography(
            new[]
            {
                new ShadowGazetteerPlace(
                    101,
                    "Springfield",
                    "US",
                    "IL",
                    "Illinois",
                    "PPL",
                    39.7,
                    -89.6,
                    ["Springfield"],
                    ["IL", "Illinois"]
                ),
                new ShadowGazetteerPlace(
                    103,
                    "Another Springfield",
                    "US",
                    "IL",
                    "Illinois",
                    "PPLX",
                    39.8,
                    -89.6,
                    ["Another Springfield", "Springfield"],
                    ["IL", "Illinois"]
                ),
            },
            "alias-collision-fixture"
        );
        var row = Venue("A Hall", "Springfield, IL");
        var adapter = Observe(geography, row);

        var proposal = Proposal(Result(adapter, row)).Locality.Geography!;
        proposal.Status.Should().Be("ambiguous_gazetteer_candidates");
        proposal.Candidates.Select(place => place.GeonameId).Should().Equal(101L, 103L);
        proposal.ProposedGeographicKey.Should().BeNull();
    }

    [Test]
    public void PinnedGazetteerRejectsChangedBytesAndChangesMethodIdentityOnRefresh()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-geography-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            using (
                var archive = ZipFile.Open(
                    Path.Combine(directory, "cities500.zip"),
                    ZipArchiveMode.Create
                )
            )
            {
                using var writer = new StreamWriter(archive.CreateEntry("cities500.txt").Open());
                writer.WriteLine(
                    string.Join(
                        '\t',
                        new[]
                        {
                            "5128581",
                            "New York City",
                            "New York City",
                            "NYC,New York",
                            "40.7",
                            "-74.0",
                            "P",
                            "PPL",
                            "US",
                            "",
                            "NY",
                            "",
                            "",
                            "",
                            "1",
                            "",
                            "1",
                            "America/New_York",
                            "2026-10-18",
                        }
                    )
                );
            }
            File.WriteAllText(
                Path.Combine(directory, "admin1CodesASCII.txt"),
                "US.NY\tNew York\tNew York\t5128638\n"
            );
            File.WriteAllText(
                Path.Combine(directory, "readme.txt"),
                "Synthetic offline fixture; not acquired reference data."
            );
            string WriteManifest(string revision)
            {
                var path = Path.Combine(directory, "manifest.json");
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(
                        new
                        {
                            version = "shadow-geonames-cities500-v1",
                            revision,
                            files = new[]
                            {
                                "cities500.zip",
                                "admin1CodesASCII.txt",
                                "readme.txt",
                            }.Select(name => new
                            {
                                path = name,
                                bytes = new FileInfo(Path.Combine(directory, name)).Length,
                                sha256 = Convert.ToHexStringLower(
                                    SHA256.HashData(
                                        File.ReadAllBytes(Path.Combine(directory, name))
                                    )
                                ),
                            }),
                        }
                    )
                );
                return path
                    + "#sha256="
                    + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            }
            var first = new ShadowVenueAdapter(WriteManifest("first"));
            var venue = Venue("A Hall", "NYC, NY");
            first.Observe(venue);
            Proposal(Result(first, venue))
                .Locality.Geography!.ProposedGeographicKey.Should()
                .Be("geonames:5128581");
            var nextPin = WriteManifest("next");
            new ShadowVenueAdapter(nextPin).Version.Should().NotBe(first.Version);
            File.AppendAllText(Path.Combine(directory, "admin1CodesASCII.txt"), "changed");
            var loadChangedInput = () => new ShadowVenueAdapter(nextPin);
            loadChangedInput.Should().Throw<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ShadowVenueGeography Geography() =>
        new(
            new[]
            {
                new ShadowGazetteerPlace(
                    5128581,
                    "New York City",
                    "US",
                    "NY",
                    "New York",
                    "PPL",
                    40.7,
                    -74,
                    ["New York City", "New York", "NYC", "NYC"],
                    ["NY", "New York"]
                ),
                new ShadowGazetteerPlace(
                    5110302,
                    "Brooklyn",
                    "US",
                    "NY",
                    "New York",
                    "PPLX",
                    40.6,
                    -73.9,
                    ["Brooklyn"],
                    ["NY", "New York"]
                ),
                new ShadowGazetteerPlace(
                    6077243,
                    "Montréal",
                    "CA",
                    "10",
                    "Quebec",
                    "PPL",
                    45.5,
                    -73.6,
                    ["Montréal", "Montreal"],
                    ["10", "Quebec"]
                ),
                new ShadowGazetteerPlace(
                    101,
                    "Springfield",
                    "US",
                    "IL",
                    "Illinois",
                    "PPL",
                    39.7,
                    -89.6,
                    ["Springfield"],
                    ["IL", "Illinois"]
                ),
                new ShadowGazetteerPlace(
                    102,
                    "Springfield",
                    "US",
                    "MA",
                    "Massachusetts",
                    "PPL",
                    42.1,
                    -72.6,
                    ["Springfield"],
                    ["MA", "Massachusetts"]
                ),
            },
            "synthetic-geography-fixture-v1"
        );

    private static ShadowVenueAdapter Observe(
        ShadowVenueGeography geography,
        params JsonElement[] records
    )
    {
        var adapter = new ShadowVenueAdapter(geography);
        foreach (var record in records)
        {
            adapter.Observe(record);
        }
        return adapter;
    }

    private static ShadowVenueAdapter Observe(params JsonElement[] records)
    {
        var adapter = new ShadowVenueAdapter();
        foreach (var record in records)
        {
            adapter.Observe(record);
        }
        return adapter;
    }

    private static ShadowCatalogResult Result(IShadowCatalogAdapter adapter, JsonElement record)
    {
        var artist = record.GetProperty("row").GetProperty("artist_id").GetInt64();
        var input = new ShadowCatalogInput(
            record,
            null,
            new(
                artist,
                new Guid((int)artist, 0, 0, new byte[8]),
                "Artist " + artist,
                ["archive.org"]
            ),
            "archive.org"
        );
        return adapter.Process(input).Should().ContainSingle().Subject;
    }

    private static ShadowVenueProposal Proposal(ShadowCatalogResult result) =>
        result.Proposal.Should().BeOfType<ShadowVenueProposal>().Subject;

    private static JsonElement Venue(
        string name,
        string location,
        string? upstream = null,
        long artist = 1,
        string? address = null,
        string? pastNames = null,
        double? latitude = null,
        double? longitude = null,
        Guid? uuid = null,
        string? scope = null
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "venue_sample",
                row = new
                {
                    uuid = uuid ?? Guid.NewGuid(),
                    artist_id = artist,
                    name,
                    location,
                    upstream_identifier = upstream,
                    address,
                    past_names = pastNames,
                    latitude,
                    longitude,
                    venue_kind = scope,
                },
            }
        );
}
