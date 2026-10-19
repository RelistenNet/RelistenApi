using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Protects the offline locality-reference contract through Load and Resolve.
/// Synthetic source records exercise identity and provenance, not matching accuracy.
/// </summary>
[TestFixture]
public sealed class TestShadowVenueGeography
{
    private string directory = null!;
    private string basePin = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-localities-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        using (
            var zip = ZipFile.Open(Path.Combine(directory, "cities500.zip"), ZipArchiveMode.Create)
        )
        using (var writer = new StreamWriter(zip.CreateEntry("cities500.txt").Open()))
        {
            writer.WriteLine(CityRow(101, "Springfield"));
            writer.WriteLine(CityRow(102, "Springfield"));
        }
        File.WriteAllText(
            Path.Combine(directory, "admin1CodesASCII.txt"),
            "US.CO\tColorado\tColorado\t5417618\n"
        );
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "Synthetic geography reference.");
        var basePath = Path.Combine(directory, "base.json");
        File.WriteAllText(
            basePath,
            JsonSerializer.Serialize(
                new
                {
                    version = "shadow-geonames-cities500-v1",
                    files = new[] { "cities500.zip", "admin1CodesASCII.txt", "readme.txt" }.Select(
                        name => Receipt(name)
                    ),
                }
            )
        );
        basePin = Pin(basePath);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public void SupplementResolvesOmittedTownWithoutSubstitutingNearbyCity()
    {
        var locality = Locality("Morrison");
        ShadowVenueGeography
            .Load(basePin)
            .Resolve(locality)
            .Status.Should()
            .Be("no_exact_gazetteer_candidate");

        var supplemented = ShadowVenueGeography.Load(Supplement());
        var result = supplemented.Resolve(locality);

        result.ProposedGeographicKey.Should().Be("geonames:5431916");
        result.Place!.Admin1Name.Should().Be("Colorado");
        result.Place.Name.Should().Be("Morrison");
        result.EvidenceIdentity.Should().NotBe(ShadowVenueGeography.Load(basePin).EvidenceIdentity);
        supplemented.Resolve(Locality("Morrison", region: "NY")).Place.Should().BeNull();
        supplemented.Resolve(Locality("Morrison", country: "CA")).Place.Should().BeNull();
    }

    [Test]
    public void SupplementPreservesExistingSameNameAmbiguity()
    {
        var result = ShadowVenueGeography.Load(Supplement()).Resolve(Locality("Springfield"));
        result.Status.Should().Be("ambiguous_gazetteer_candidates");
        result.Candidates.Select(place => place.GeonameId).Should().Equal(101L, 102L);
        result.ProposedGeographicKey.Should().BeNull();
    }

    [Test]
    public void SupplementRejectsConflictingDuplicateGeographicIdentity()
    {
        var pin = Supplement(duplicate: true);
        var load = () => ShadowVenueGeography.Load(pin);
        load.Should().Throw<InvalidDataException>().WithMessage("*repeats a geographic identity*");
    }

    [Test]
    public void SupplementRejectsChangedRetainedSource()
    {
        var pin = Supplement();
        File.AppendAllText(Path.Combine(directory, "morrison.rdf"), "changed");
        var load = () => ShadowVenueGeography.Load(pin);
        load.Should().Throw<InvalidDataException>().WithMessage("*record changed*");
    }

    [Test]
    public void SupplementRejectsChangedManifestPin()
    {
        var pin = Supplement();
        File.AppendAllText(Path.Combine(directory, "supplement.json"), " ");
        var load = () => ShadowVenueGeography.Load(pin);
        load.Should().Throw<InvalidDataException>().WithMessage("*manifest differs*");
    }

    [Test]
    public void SupplementRejectsCountryContradictionWithAuthenticatedRegion()
    {
        var pin = Supplement(country: "CA");
        var load = () => ShadowVenueGeography.Load(pin);
        load.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*authenticated country and region*");
    }

    [Test]
    public void SupplementRejectsIdentityThatDiffersFromItsSourceUrl()
    {
        var pin = Supplement(sourceUrl: "https://sws.geonames.org/123/about.rdf");
        var load = () => ShadowVenueGeography.Load(pin);
        load.Should().Throw<InvalidDataException>().WithMessage("*identity differs*");
    }

    [Test]
    public void SupplementRejectsDtdBeforeResolvingExternalEntities()
    {
        var pin = Supplement(includeDtd: true);
        var load = () => ShadowVenueGeography.Load(pin);
        load.Should().Throw<XmlException>();
    }

    private string Supplement(
        bool duplicate = false,
        string country = "US",
        string? sourceUrl = null,
        bool includeDtd = false
    )
    {
        var rdf = Rdf("Morrison", country);
        if (includeDtd)
        {
            rdf =
                "<!DOCTYPE RDF [<!ENTITY external SYSTEM 'file:///unused-locality-proof'>]>" + rdf;
        }
        File.WriteAllText(Path.Combine(directory, "morrison.rdf"), rdf);
        var names = new List<string> { "morrison.rdf" };
        if (duplicate)
        {
            File.WriteAllText(
                Path.Combine(directory, "conflicting.rdf"),
                Rdf("Different Town", country)
            );
            names.Add("conflicting.rdf");
        }
        var path = Path.Combine(directory, "supplement.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    version = "shadow-geonames-localities-v1",
                    baseManifest = basePin,
                    files = names.Select(name => new
                    {
                        path = name,
                        bytes = new FileInfo(Path.Combine(directory, name)).Length,
                        sha256 = Hash(File.ReadAllBytes(Path.Combine(directory, name))),
                        url = sourceUrl ?? "https://sws.geonames.org/5431916/about.rdf",
                        capturedAt = "2026-10-18T00:00:00Z",
                    }),
                }
            )
        );
        return Pin(path);
    }

    private object Receipt(string name) =>
        new
        {
            path = name,
            bytes = new FileInfo(Path.Combine(directory, name)).Length,
            sha256 = Hash(File.ReadAllBytes(Path.Combine(directory, name))),
        };

    private static string Rdf(string name, string country)
    {
        XNamespace gn = "http://www.geonames.org/ontology#";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace geo = "http://www.w3.org/2003/01/geo/wgs84_pos#";
        return new XElement(
            rdf + "RDF",
            new XElement(
                gn + "Feature",
                new XAttribute(rdf + "about", "https://sws.geonames.org/5431916/"),
                new XElement(gn + "name", name),
                new XElement(gn + "countryCode", country),
                new XElement(
                    gn + "featureClass",
                    new XAttribute(rdf + "resource", "https://www.geonames.org/ontology#P")
                ),
                new XElement(
                    gn + "featureCode",
                    new XAttribute(rdf + "resource", "https://www.geonames.org/ontology#P.PPL")
                ),
                new XElement(
                    gn + "parentADM1",
                    new XAttribute(rdf + "resource", "https://sws.geonames.org/5417618/")
                ),
                new XElement(geo + "lat", "39.6536"),
                new XElement(geo + "long", "-105.1911")
            )
        ).ToString();
    }

    private static string CityRow(long id, string name) =>
        string.Join(
            '\t',
            new[]
            {
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                name,
                name,
                "",
                "39.8",
                "-105",
                "P",
                "PPL",
                "US",
                "",
                "CO",
                "",
                "",
                "",
                "1000",
                "",
                "1",
                "America/Denver",
                "2026-10-18",
            }
        );

    private static ShadowVenueLocality Locality(
        string city,
        string region = "CO",
        string country = "US"
    ) => new(city, city, region, country, null, null, "catalog_location", null);

    private static string Pin(string path) => path + "#sha256=" + Hash(File.ReadAllBytes(path));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
