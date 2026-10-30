using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Normalization;

namespace Relisten.NormalizationTests;

public class TestCatalogueVocabulary
{
    // These strings predate the enum properties. Temporal's default JSON payload
    // converter must still read them when an existing normalization workflow resumes.
    private const string HistoricalPayload = """
        {"SongKind":"segment","VenueKind":"room","NameKind":"official","Precision":"city","Qualifiers":["with_guest","tape_flip"],"MissingPrecision":null}
        """;

    [Test]
    public void HistoricalStringPayloadsReadAndWriteWithoutChangingTheirShape()
    {
        var payload = JsonSerializer.Deserialize<Payload>(HistoricalPayload)!;

        payload.SongKind.Should().Be(SongKind.Segment);
        payload.VenueKind.Should().Be(VenueKind.Room);
        payload.NameKind.Should().Be(VenueNameKind.Official);
        payload.Precision.Should().Be(VenueCoordinatePrecision.City);
        payload.Qualifiers.Should().Equal(TrackQualifier.WithGuest, TrackQualifier.TapeFlip);
        payload.MissingPrecision.Should().BeNull();
        JsonSerializer.Serialize(payload).Should().Be(HistoricalPayload);
    }

    [TestCase("1")]
    [TestCase("\"1\"")]
    [TestCase("\"WithGuest\"")]
    [TestCase("\"WITH_GUEST\"")]
    [TestCase("\"unknown\"")]
    [TestCase("null")]
    public void InvalidWireValuesAreRejectedInsteadOfSilentlyBecomingAnEnum(string json)
    {
        FluentActions
            .Invoking(() => JsonSerializer.Deserialize<TrackQualifier>(json))
            .Should()
            .Throw<JsonException>();
    }

    [Test]
    public void InvalidArrayMembersAreRejected()
    {
        FluentActions
            .Invoking(() =>
                JsonSerializer.Deserialize<TrackQualifier[]>("""["jam","new_qualifier"]""")
            )
            .Should()
            .Throw<JsonException>();
    }

    [Test]
    public void NullablePropertiesAcceptNullAndEmptyQualifierListsStayEmpty()
    {
        var payload = JsonSerializer.Deserialize<Payload>(
            """
            {"SongKind":"song","VenueKind":"site","NameKind":"spelling","Precision":null,"Qualifiers":[],"MissingPrecision":null}
            """
        )!;

        payload.Precision.Should().BeNull();
        payload.Qualifiers.Should().BeEmpty();
    }

    [Test]
    public void StructuredOutputTokensAndJsonUseTheSameVocabulary()
    {
        AssertVocabulary<SongKind>();
        AssertVocabulary<VenueKind>();
        AssertVocabulary<VenueNameKind>();
        AssertVocabulary<VenueCoordinatePrecision>();
        AssertVocabulary<TrackQualifier>();

        NormalizationEnums.Values<TrackQualifier>().Should().Contain("with_guest");
        NormalizationEnums
            .TryParse<TrackQualifier>("with_guest", out var qualifier)
            .Should()
            .BeTrue();
        qualifier.Should().Be(TrackQualifier.WithGuest);
        NormalizationEnums.TryParse<TrackQualifier>("WithGuest", out _).Should().BeFalse();
        NormalizationEnums.TryParse<TrackQualifier>(null, out _).Should().BeFalse();
        FluentActions
            .Invoking(() => NormalizationEnums.Format((TrackQualifier)999))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void DatabaseTextColumnsMaterializeAsTypedValuesIncludingNullableColumns()
    {
        var table = new DataTable();
        table.Columns.Add(nameof(DatabaseRow.Kind), typeof(string));
        table.Columns.Add(nameof(DatabaseRow.VenueKind), typeof(string));
        table.Columns.Add(nameof(DatabaseRow.NameKind), typeof(string));
        table.Columns.Add(nameof(DatabaseRow.Precision), typeof(string));
        table.Rows.Add("jam", "room", "official", DBNull.Value);
        table.Rows.Add("song", "site", "spelling", "venue");
        using var reader = table.CreateDataReader();
        var parse = reader.GetRowParser<DatabaseRow>();

        reader.Read().Should().BeTrue();
        var first = parse(reader);
        first.Kind.Should().Be(SongKind.Jam);
        first.VenueKind.Should().Be(VenueKind.Room);
        first.NameKind.Should().Be(VenueNameKind.Official);
        first.Precision.Should().BeNull();

        reader.Read().Should().BeTrue();
        var second = parse(reader);
        second.VenueKind.Should().Be(VenueKind.Site);
        second.NameKind.Should().Be(VenueNameKind.Spelling);
        second.Precision.Should().Be(VenueCoordinatePrecision.Venue);
    }

    private static void AssertVocabulary<T>()
        where T : struct, Enum
    {
        NormalizationEnums.IsSupported(typeof(T)).Should().BeTrue();
        foreach (var value in Enum.GetValues<T>())
        {
            var token = NormalizationEnums.Format(value);
            NormalizationEnums.Values<T>().Should().Contain(token);
            NormalizationEnums.Parse<T>(token).Should().Be(value);
            JsonSerializer.Serialize(value).Should().Be(JsonSerializer.Serialize(token));
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(token)).Should().Be(value);
        }
    }

    private sealed record Payload(
        SongKind SongKind,
        VenueKind VenueKind,
        VenueNameKind NameKind,
        VenueCoordinatePrecision? Precision,
        IReadOnlyList<TrackQualifier> Qualifiers,
        VenueCoordinatePrecision? MissingPrecision
    );

    private sealed class DatabaseRow
    {
        public SongKind Kind { get; set; }
        public VenueKind VenueKind { get; set; }
        public VenueNameKind NameKind { get; set; }
        public VenueCoordinatePrecision? Precision { get; set; }
    }
}
