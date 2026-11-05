using FluentAssertions;
using Newtonsoft.Json;
using Relisten;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationJson
{
    [Test]
    public void Api_settings_preserve_normalization_tokens_and_legacy_numeric_enums()
    {
        var value = new
        {
            Kind = SongKind.Song,
            Venue = VenueKind.Room,
            Name = VenueNameKind.Official,
            Precision = (VenueCoordinatePrecision?)null,
            Qualifiers = new[] { TrackQualifier.WithGuest, TrackQualifier.TapeFlip },
            Legacy = (DayOfWeek)2,
        };

        JsonConvert
            .SerializeObject(value, RelistenApiJsonOptionsWrapper.DefaultSerializerSettings)
            .Should()
            .Be(
                "{\"Kind\":\"song\",\"Venue\":\"room\",\"Name\":\"official\",\"Precision\":null,\"Qualifiers\":[\"with_guest\",\"tape_flip\"],\"Legacy\":2}"
            );
    }
}
