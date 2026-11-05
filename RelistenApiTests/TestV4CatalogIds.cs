using System.Text.Json;
using FluentAssertions;
using Relisten.Identifiers;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestV4CatalogIds
{
    [Test]
    public void Published_vectors_establish_uuid_byte_order_and_full_bit_range()
    {
        // jetify-com/typeid spec/valid.json, recorded by the design experiment.
        // Its independently specified values catch an endian error that round trips alone miss.
        using var vectors = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures/v4/typeid-valid.json"
                )
            )
        );
        foreach (var vector in vectors.RootElement.EnumerateArray())
        {
            var uuid = Guid.Parse(vector.GetProperty("uuid").GetString()!);
            var typeId = vector.GetProperty("typeid").GetString()!;
            var suffix = typeId[(typeId.LastIndexOf('_') + 1)..];
            UuidTypeId.Encode(uuid).Should().Be(suffix);
            UuidTypeId.Decode(suffix).Should().Be(uuid);
        }
    }

    [Test]
    public void Resource_types_preserve_arbitrary_legacy_uuid_bits()
    {
        var random = new Random(17);
        var bytes = new byte[16];
        for (var i = 0; i < 1000; i++)
        {
            random.NextBytes(bytes);
            var uuid = new Guid(bytes, bigEndian: true);
            var track = new TrackId(uuid);
            TrackId.Parse(track.ToString()).Value.Should().Be(uuid);
            var importedVenue = new ImportedVenueId(uuid);
            ImportedVenueId.Parse(importedVenue.ToString()).Should().Be(importedVenue);
        }
    }

    [Test]
    public void Resource_kind_and_multi_underscore_prefix_are_unambiguous()
    {
        var uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        SourceSetId.Parse(new SourceSetId(uuid).ToString()).Value.Should().Be(uuid);
        TrackId.TryParse(new SourceId(uuid).ToString(), out _).Should().BeFalse();
        ImportedSongId.TryParse(new ImportedVenueId(uuid).ToString(), out _).Should().BeFalse();
        Action parse = () => TrackId.Parse(new SourceId(uuid).ToString());
        parse.Should().Throw<FormatException>();
    }

    [TestCase(null)]
    [TestCase("track_80000000000000000000000000")]
    [TestCase("track_0000000000000000000000000I")]
    [TestCase("track_0000000000000000000000000l")]
    [TestCase("track_0000000000000000000000000o")]
    [TestCase("track_0000000000000000000000000u")]
    [TestCase("track_0000000000000000000000000")]
    [TestCase("track_000000000000000000000000000")]
    [TestCase("TRACK_00000000000000000000000000")]
    [TestCase("track__00000000000000000000000000")]
    [TestCase("track_00000000000000000000000000 ")]
    public void Malformed_or_noncanonical_ids_are_rejected(string? value)
    {
        TrackId.TryParse(value, out _).Should().BeFalse();
    }

    [Test]
    public void Short_normalized_ids_do_not_acquire_uuid_identity()
    {
        ShortTypeId.IsValid("venue_k3m9x2qa", PublicIdPrefixes.Venue).Should().BeTrue();
        ShortTypeId.IsValid("song_7hd2pw4e", PublicIdPrefixes.Song).Should().BeTrue();
        ShortTypeId.IsValid("song_7hd2pw4e", PublicIdPrefixes.Venue).Should().BeFalse();
        ShortTypeId.IsValid("venue_k3m9x2qI", PublicIdPrefixes.Venue).Should().BeFalse();
        UuidTypeId.TryParse("venue_k3m9x2qa", PublicIdPrefixes.Venue, out _).Should().BeFalse();
    }
}
