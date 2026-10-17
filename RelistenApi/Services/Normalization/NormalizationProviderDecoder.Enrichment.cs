using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

// Setlist pages describe observed accounts, with provider order and performer scope.
// Page membership never establishes the latest revision of an individual setlist.
public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodeSetlistFm(ProviderObservationReader reader)
    {
        var root = reader.Root;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("setlist", out var members)
            || members.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)
        )
        {
            throw new InvalidDataException("Setlist.fm retained input has no setlist page.");
        }

        if (
            !root.TryGetProperty("page", out var page)
            || page.ValueKind != JsonValueKind.Number
            || !page.TryGetInt32(out var pageNumber)
            || pageNumber <= 0
            || !root.TryGetProperty("itemsPerPage", out var pageSize)
            || pageSize.ValueKind != JsonValueKind.Number
            || !pageSize.TryGetInt32(out var size)
            || size <= 0
            || !root.TryGetProperty("total", out var total)
            || total.ValueKind != JsonValueKind.Number
            || !total.TryGetInt32(out var totalCount)
            || totalCount < 0
        )
        {
            throw new InvalidDataException("Setlist.fm retained pagination is invalid.");
        }
        var pageKey = reader.Input.Resource.ProviderKey.LastIndexOf(
            ":page=",
            StringComparison.Ordinal
        );
        if (
            pageKey < 0
            || !int.TryParse(
                reader.Input.Resource.ProviderKey[(pageKey + 6)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expectedPage
            )
            || pageNumber != expectedPage
        )
        {
            throw new InvalidDataException(
                "Setlist.fm retained page does not match its resource identity."
            );
        }

        var setlists = new List<ProviderSetlistObservation>();
        foreach (var (item, pointer) in EnrichmentMembers(members, "/setlist"))
        {
            if (
                item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idValue)
            )
            {
                reader.Issue("missing_setlist_identity", pointer);
                continue;
            }
            var identity = reader.Identity(idValue, pointer + "/id", "setlist.fm", "setlist");
            if (identity == null)
            {
                continue;
            }

            var performer = item.TryGetProperty("artist", out var artist)
                ? EnrichmentPerformer(
                    reader,
                    artist,
                    pointer + "/artist",
                    "musicbrainz.org",
                    "mbid"
                )
                : null;
            var (observedSets, passages) = EnrichmentSetlistFmSets(reader, item, pointer);

            setlists.Add(
                new()
                {
                    Origin = reader.Origin(
                        identity.Identity,
                        pointer,
                        ProviderObservationScope.PageMember
                    ),
                    Performers = performer == null ? [] : [performer],
                    LiteralDate = reader.String(item, "eventDate", pointer),
                    Venue = item.TryGetProperty("venue", out var venue)
                        ? EnrichmentVenue(
                            reader,
                            venue,
                            pointer + "/venue",
                            identity.Identity,
                            "setlist.fm",
                            true,
                            ProviderObservationScope.PageMember
                        )
                        : null,
                    Ordered = true,
                    Sets = observedSets,
                    Passages = passages,
                    Text = reader.TextFields(
                        item,
                        pointer,
                        new Dictionary<string, string> { ["info"] = "info" }
                    ),
                }
            );
        }
        return reader.Result() with { Setlists = setlists };
    }

    private static (
        IReadOnlyList<ProviderSetObservation> Sets,
        IReadOnlyList<ProviderPassageObservation> Passages
    ) EnrichmentSetlistFmSets(ProviderObservationReader reader, JsonElement item, string pointer)
    {
        var passages = new List<ProviderPassageObservation>();
        var observedSets = new List<ProviderSetObservation>();
        var passageOrdinal = 0;
        if (!item.TryGetProperty("sets", out var sets))
        {
            reader.Issue("missing_sets", pointer);
            return (observedSets, passages);
        }
        if (sets.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("invalid_sets", pointer + "/sets");
            return (observedSets, passages);
        }
        if (!sets.TryGetProperty("set", out var entries))
        {
            reader.Issue("missing_set_entries", pointer + "/sets");
            return (observedSets, passages);
        }
        if (entries.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            reader.Issue("invalid_sets", pointer + "/sets/set");
            return (observedSets, passages);
        }
        var setOrdinal = 0;
        foreach (var (set, setPointer) in EnrichmentMembers(entries, pointer + "/sets/set"))
        {
            setOrdinal++;
            if (set.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("invalid_set", setPointer);
                continue;
            }
            var label = reader.String(set, "name", setPointer);
            observedSets.Add(
                new()
                {
                    Ordinal = setOrdinal,
                    Evidence = reader.Evidence(setPointer),
                    Label = label,
                    EncoreNumber = set.TryGetProperty("encore", out var encore)
                        ? reader.Integer(encore, setPointer + "/encore")
                        : null,
                }
            );
            if (!set.TryGetProperty("song", out var songs))
            {
                reader.Issue("missing_set_songs", setPointer);
                continue;
            }
            if (songs.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            {
                reader.Issue("invalid_set_songs", setPointer + "/song");
                continue;
            }
            foreach (var (song, songPointer) in EnrichmentMembers(songs, setPointer + "/song"))
            {
                passageOrdinal++;
                if (song.ValueKind != JsonValueKind.Object)
                {
                    reader.Issue("invalid_passage", songPointer);
                    continue;
                }
                var title = reader.String(song, "name", songPointer);
                if (title == null || string.IsNullOrWhiteSpace(title.Value))
                {
                    reader.Issue("missing_passage_title", songPointer);
                    continue;
                }
                var performers = new List<ProviderPerformanceAnnotation>();
                EnrichmentAnnotation(
                    reader,
                    song,
                    songPointer,
                    "cover",
                    "cover_artist",
                    performers
                );
                EnrichmentAnnotation(reader, song, songPointer, "with", "guest_artist", performers);
                passages.Add(
                    new()
                    {
                        Ordinal = passageOrdinal,
                        Title = title,
                        SetLabel = label,
                        SetOrdinal = setOrdinal,
                        Tape = EnrichmentBoolean(reader, song, "tape", songPointer),
                        Annotations = reader.TextFields(
                            song,
                            songPointer,
                            new Dictionary<string, string> { ["info"] = "info" }
                        ),
                        PerformerAnnotations = performers,
                    }
                );
            }
        }

        return (observedSets, passages);
    }

    private static ProviderDecodeResult DecodePhantasyTourList(ProviderObservationReader reader)
    {
        if (reader.Root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Phantasy Tour retained listing is not an array.");
        }
        var setlists = new List<ProviderSetlistObservation>();
        foreach (var (item, pointer) in EnrichmentMembers(reader.Root, ""))
        {
            var decoded = EnrichmentPhantasyAccount(
                reader,
                item,
                pointer,
                ProviderObservationScope.PageMember,
                false
            );
            if (decoded != null)
            {
                setlists.Add(decoded);
            }
        }
        return reader.Result() with { Setlists = setlists };
    }

    private static ProviderDecodeResult DecodePhantasyTourShow(ProviderObservationReader reader)
    {
        if (
            reader.Root.ValueKind != JsonValueKind.Object
            || !reader.Root.TryGetProperty("data", out var item)
            || item.ValueKind != JsonValueKind.Object
        )
        {
            throw new InvalidDataException("Phantasy Tour retained input has no show detail.");
        }
        var decoded =
            EnrichmentPhantasyAccount(
                reader,
                item,
                "/data",
                ProviderObservationScope.ResourceItem,
                true
            ) ?? throw new InvalidDataException("Phantasy Tour retained show has no identity.");
        if (decoded.Origin.Item.ProviderKey != reader.Input.Resource.ProviderKey)
        {
            throw new InvalidDataException(
                "Phantasy Tour retained show does not match its resource identity."
            );
        }
        return reader.Result() with { Setlists = [decoded] };
    }

    private static ProviderSetlistObservation? EnrichmentPhantasyAccount(
        ProviderObservationReader reader,
        JsonElement item,
        string pointer,
        ProviderObservationScope scope,
        bool detail
    )
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue))
        {
            reader.Issue("missing_show_identity", pointer);
            return null;
        }
        var identity = reader.Identity(idValue, pointer + "/id", "phantasytour.com", "show");
        if (identity == null)
        {
            return null;
        }
        var performer = item.TryGetProperty("band", out var band)
            ? EnrichmentPerformer(reader, band, pointer + "/band", "phantasytour.com", "id")
            : null;
        var bandIdentity = performer?.Identities.FirstOrDefault();
        var (observedSets, passages) = EnrichmentPhantasySets(
            reader,
            item,
            pointer,
            identity,
            bandIdentity,
            detail
        );
        return new()
        {
            Origin = reader.Origin(identity.Identity, pointer, scope),
            Performers = performer == null ? [] : [performer],
            LiteralDate = reader.String(item, "dateTime", pointer),
            Venue = item.TryGetProperty("venue", out var venue)
                ? EnrichmentVenue(
                    reader,
                    venue,
                    pointer + "/venue",
                    identity.Identity,
                    "phantasytour.com",
                    false,
                    scope
                )
                : null,
            Ordered = detail,
            Sets = observedSets,
            Passages = passages,
            Text = EnrichmentPhantasyText(reader, item, pointer),
        };
    }

    private static (
        IReadOnlyList<ProviderSetObservation> Sets,
        IReadOnlyList<ProviderPassageObservation> Passages
    ) EnrichmentPhantasySets(
        ProviderObservationReader reader,
        JsonElement item,
        string pointer,
        ObservedProviderIdentity showIdentity,
        ObservedProviderIdentity? bandIdentity,
        bool detail
    )
    {
        var passages = new List<ProviderPassageObservation>();
        var observedSets = new List<ProviderSetObservation>();
        var passageOrdinal = 0;
        if (!detail)
        {
            return (observedSets, passages);
        }
        if (!item.TryGetProperty("sets", out var sets))
        {
            reader.Issue("missing_sets", pointer);
            return (observedSets, passages);
        }
        if (sets.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("invalid_sets", pointer + "/sets");
            return (observedSets, passages);
        }
        var setOrdinal = 0;
        foreach (var (set, setPointer) in EnrichmentMembers(sets, pointer + "/sets"))
        {
            setOrdinal++;
            if (set.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("invalid_set", setPointer);
                continue;
            }
            var label = reader.String(set, "name", setPointer);
            var setIdentity = set.TryGetProperty("id", out var setId)
                ? reader.Identity(setId, setPointer + "/id", "phantasytour.com", "set")
                : null;
            observedSets.Add(
                new()
                {
                    Ordinal = setOrdinal,
                    Evidence = reader.Evidence(setPointer),
                    Label = label,
                    Identity =
                        setIdentity == null
                            ? null
                            : setIdentity with
                            {
                                Identity = new(
                                    "phantasytour.com",
                                    "show_set",
                                    JsonSerializer.Serialize(
                                        new[]
                                        {
                                            showIdentity.Identity.ProviderKey,
                                            setIdentity.Identity.ProviderKey,
                                        }
                                    )
                                ),
                            },
                }
            );
            if (
                !set.TryGetProperty("songs", out var songs)
                || songs.ValueKind != JsonValueKind.Array
            )
            {
                reader.Issue("invalid_set_songs", setPointer);
                continue;
            }
            foreach (var (song, songPointer) in EnrichmentMembers(songs, setPointer + "/songs"))
            {
                passageOrdinal++;
                if (song.ValueKind != JsonValueKind.Object)
                {
                    reader.Issue("invalid_passage", songPointer);
                    continue;
                }
                var title = reader.String(song, "name", songPointer);
                if (title == null || string.IsNullOrWhiteSpace(title.Value))
                {
                    reader.Issue("missing_passage_title", songPointer);
                    continue;
                }
                var songsIdentities = new List<ObservedProviderIdentity>();
                if (song.TryGetProperty("id", out var songId))
                {
                    var observed = reader.Identity(
                        songId,
                        songPointer + "/id",
                        "phantasytour.com",
                        "song"
                    );
                    if (observed != null && bandIdentity != null)
                    {
                        songsIdentities.Add(
                            observed with
                            {
                                Identity = new(
                                    "phantasytour.com",
                                    "band_song",
                                    JsonSerializer.Serialize(
                                        new[]
                                        {
                                            bandIdentity.Identity.ProviderKey,
                                            observed.Identity.ProviderKey,
                                        }
                                    )
                                ),
                            }
                        );
                    }
                    else if (observed != null)
                    {
                        reader.Issue("song_identity_requires_performer", songPointer + "/id");
                    }
                }
                ObservedValue<string>? segue = null;
                ObservedValue<bool>? segueFlag = null;
                if (song.TryGetProperty("segue", out var marker))
                {
                    if (marker.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        segueFlag = new(
                            marker.GetBoolean(),
                            reader.Evidence(songPointer + "/segue")
                        );
                    }
                    else if (marker.ValueKind == JsonValueKind.String)
                    {
                        segue = reader.String(marker, songPointer + "/segue");
                    }
                    else if (marker.ValueKind != JsonValueKind.Null)
                    {
                        reader.Issue("invalid_segue", songPointer + "/segue");
                    }
                }
                var (notes, footnoteNumbers) = EnrichmentFootnotes(
                    reader,
                    item,
                    pointer,
                    song,
                    songPointer
                );
                passages.Add(
                    new()
                    {
                        Ordinal = passageOrdinal,
                        Title = title,
                        SongIdentities = songsIdentities,
                        SetLabel = label,
                        SetOrdinal = setOrdinal,
                        TransitionMarker = segue,
                        Segue = segueFlag,
                        FootnoteNumbers = footnoteNumbers,
                        Annotations = notes,
                    }
                );
            }
        }
        return (observedSets, passages);
    }

    private static IReadOnlyList<ProviderTextObservation> EnrichmentPhantasyText(
        ProviderObservationReader reader,
        JsonElement item,
        string pointer
    )
    {
        var text = new List<ProviderTextObservation>();
        foreach (var field in new[] { "credits", "prenotes", "postnotes" })
        {
            if (item.TryGetProperty(field, out var value))
            {
                EnrichmentProse(reader, value, pointer + "/" + field, field, text);
            }
        }
        if (
            item.TryGetProperty("footnotes", out var notes)
            && notes.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var (note, notePointer) in EnrichmentMembers(notes, pointer + "/footnotes"))
            {
                if (note.ValueKind == JsonValueKind.Object)
                {
                    var value = reader.String(note, "note", notePointer);
                    if (value != null)
                    {
                        text.Add(new("footnote", value));
                    }
                }
            }
        }
        return text;
    }

    // Keep structured credit strings separately cited. Neither a name-looking
    // value nor its field label authorizes a person or authorship assertion.
    private static void EnrichmentProse(
        ProviderObservationReader reader,
        JsonElement value,
        string pointer,
        string field,
        List<ProviderTextObservation> result
    )
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            result.Add(new(field, reader.String(value, pointer)!));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var (member, memberPointer) in EnrichmentMembers(value, pointer))
            {
                EnrichmentProse(reader, member, memberPointer, field, result);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                EnrichmentProse(
                    reader,
                    property.Value,
                    ProviderObservationReader.Pointer(pointer, property.Name),
                    field,
                    result
                );
            }
        }
    }

    private static (
        IReadOnlyList<ProviderTextObservation> Notes,
        IReadOnlyList<ObservedValue<int>> References
    ) EnrichmentFootnotes(
        ProviderObservationReader reader,
        JsonElement item,
        string pointer,
        JsonElement song,
        string songPointer
    )
    {
        var notes = new List<ProviderTextObservation>();
        var numbers = new List<ObservedValue<int>>();
        if (!song.TryGetProperty("footnotes", out var references))
        {
            return (notes, numbers);
        }
        if (references.ValueKind == JsonValueKind.Null)
        {
            return (notes, numbers);
        }
        if (references.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("invalid_footnotes", songPointer + "/footnotes");
            return (notes, numbers);
        }
        foreach (
            var (reference, referencePointer) in EnrichmentMembers(
                references,
                songPointer + "/footnotes"
            )
        )
        {
            if (reference.ValueKind == JsonValueKind.String)
            {
                notes.Add(new("footnote", reader.String(reference, referencePointer)!));
                continue;
            }
            var number = reader.Integer(reference, referencePointer);
            if (number == null)
            {
                continue;
            }
            numbers.Add(number);
            var resolved = false;
            if (
                item.TryGetProperty("footnotes", out var footnotes)
                && footnotes.ValueKind == JsonValueKind.Array
            )
            {
                foreach (
                    var (footnote, footnotePointer) in EnrichmentMembers(
                        footnotes,
                        pointer + "/footnotes"
                    )
                )
                {
                    if (
                        footnote.ValueKind == JsonValueKind.Object
                        && footnote.TryGetProperty("id", out var noteId)
                        && noteId.ValueKind == JsonValueKind.Number
                        && noteId.TryGetInt32(out var key)
                        && key == number.Value
                    )
                    {
                        var text = reader.String(footnote, "note", footnotePointer);
                        if (text != null)
                        {
                            notes.Add(new("footnote", text));
                            resolved = true;
                        }
                    }
                }
            }
            if (!resolved)
            {
                reader.Issue("unresolved_footnote", referencePointer);
            }
        }
        return (notes, numbers);
    }

    private static ObservedValue<bool>? EnrichmentBoolean(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer
    )
    {
        if (
            !parent.TryGetProperty(property, out var value)
            || value.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return new(value.GetBoolean(), reader.Evidence(pointer + "/" + property));
        }
        reader.Issue("expected_boolean", pointer + "/" + property);
        return null;
    }

    private static ProviderPerformerObservation? EnrichmentPerformer(
        ProviderObservationReader reader,
        JsonElement artist,
        string pointer,
        string provider,
        string identityProperty
    )
    {
        if (artist.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("invalid_performer", pointer);
            return null;
        }
        var identity = artist.TryGetProperty(identityProperty, out var id)
            ? reader.Identity(id, pointer + "/" + identityProperty, provider, "artist")
            : null;
        return new()
        {
            Name = reader.String(artist, "name", pointer),
            Identities = identity == null ? [] : [identity],
        };
    }

    private static void EnrichmentAnnotation(
        ProviderObservationReader reader,
        JsonElement song,
        string pointer,
        string property,
        string kind,
        List<ProviderPerformanceAnnotation> annotations
    )
    {
        if (!song.TryGetProperty(property, out var artist))
        {
            return;
        }
        var performer = EnrichmentPerformer(
            reader,
            artist,
            pointer + "/" + property,
            "musicbrainz.org",
            "mbid"
        );
        if (performer != null)
        {
            annotations.Add(new(kind, performer.Name, performer.Identities));
        }
    }

    private static ProviderVenueObservation? EnrichmentVenue(
        ProviderObservationReader reader,
        JsonElement venue,
        string pointer,
        NormalizationResourceKey parent,
        string provider,
        bool setlistFm,
        ProviderObservationScope scope
    )
    {
        if (venue.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("invalid_venue", pointer);
            return null;
        }
        var identity = venue.TryGetProperty("id", out var id)
            ? reader.Identity(id, pointer + "/id", provider, "venue")
            : null;
        var name = reader.String(venue, "name", pointer);
        ObservedValue<string>? country = null,
            region = null,
            city = null;
        ObservedValue<double>? latitude = null,
            longitude = null;
        if (
            setlistFm
            && venue.TryGetProperty("city", out var cityObject)
            && cityObject.ValueKind == JsonValueKind.Object
        )
        {
            city = reader.String(cityObject, "name", pointer + "/city");
            region = reader.String(cityObject, "state", pointer + "/city");
            if (
                cityObject.TryGetProperty("country", out var countryObject)
                && countryObject.ValueKind == JsonValueKind.Object
            )
            {
                country = reader.String(countryObject, "name", pointer + "/city/country");
            }
            if (
                cityObject.TryGetProperty("coords", out var coords)
                && coords.ValueKind == JsonValueKind.Object
            )
            {
                latitude = coords.TryGetProperty("lat", out var lat)
                    ? reader.Number(lat, pointer + "/city/coords/lat")
                    : null;
                longitude = coords.TryGetProperty("long", out var lon)
                    ? reader.Number(lon, pointer + "/city/coords/long")
                    : null;
            }
        }
        else if (!setlistFm)
        {
            city = reader.String(venue, "city", pointer);
            region = reader.String(venue, "state", pointer);
            country = reader.String(venue, "country", pointer);
        }
        return new()
        {
            Origin = reader.Origin(identity?.Identity ?? parent, pointer, scope),
            Names = name == null ? [] : [name],
            Identities = identity == null ? [] : [identity],
            OriginalLocality = reader.String(venue, "locale", pointer),
            City = city,
            Region = region,
            Country = country,
            Latitude = latitude,
            Longitude = longitude,
            CoordinateKind =
                setlistFm && (latitude != null || longitude != null)
                    ? ObservedCoordinateKind.City
                    : ObservedCoordinateKind.Unknown,
        };
    }

    private static IEnumerable<(JsonElement Value, string Pointer)> EnrichmentMembers(
        JsonElement value,
        string pointer
    )
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var member in value.EnumerateArray())
            {
                yield return (
                    member,
                    ProviderObservationReader.Pointer(
                        pointer,
                        (index++).ToString(CultureInfo.InvariantCulture)
                    )
                );
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            yield return (value, pointer);
        }
    }
}
