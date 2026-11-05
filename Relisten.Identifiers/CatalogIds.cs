namespace Relisten.Identifiers;

/// <summary>A catalog ID retaining the exact UUID stored by ingestion.</summary>
public interface IUuidCatalogId
{
    Guid Value { get; }
}

/// <summary>A resource-specific parsing contract for host serializer and route adapters.</summary>
public interface IUuidCatalogId<TSelf> : IUuidCatalogId
    where TSelf : struct, IUuidCatalogId<TSelf>
{
    static abstract string Prefix { get; }
    static abstract TSelf Parse(string value);
}

// Each type prevents cross-resource substitution in application code. The shared codec
// owns the wire format; these values intentionally have no serializer or database dependencies.
public readonly record struct ArtistId(Guid Value) : IUuidCatalogId<ArtistId>
{
    public static string Prefix => PublicIdPrefixes.Artist;

    public static ArtistId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out ArtistId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct ShowId(Guid Value) : IUuidCatalogId<ShowId>
{
    public static string Prefix => PublicIdPrefixes.Show;

    public static ShowId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out ShowId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct SourceId(Guid Value) : IUuidCatalogId<SourceId>
{
    public static string Prefix => PublicIdPrefixes.Source;

    public static SourceId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out SourceId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct SourceSetId(Guid Value) : IUuidCatalogId<SourceSetId>
{
    public static string Prefix => PublicIdPrefixes.SourceSet;

    public static SourceSetId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out SourceSetId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct TrackId(Guid Value) : IUuidCatalogId<TrackId>
{
    public static string Prefix => PublicIdPrefixes.Track;

    public static TrackId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out TrackId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct YearId(Guid Value) : IUuidCatalogId<YearId>
{
    public static string Prefix => PublicIdPrefixes.Year;

    public static YearId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out YearId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct TourId(Guid Value) : IUuidCatalogId<TourId>
{
    public static string Prefix => PublicIdPrefixes.Tour;

    public static TourId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out TourId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct ImportedVenueId(Guid Value) : IUuidCatalogId<ImportedVenueId>
{
    public static string Prefix => PublicIdPrefixes.ImportedVenue;

    public static ImportedVenueId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out ImportedVenueId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct ImportedSongId(Guid Value) : IUuidCatalogId<ImportedSongId>
{
    public static string Prefix => PublicIdPrefixes.ImportedSong;

    public static ImportedSongId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out ImportedSongId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct SetlistShowId(Guid Value) : IUuidCatalogId<SetlistShowId>
{
    public static string Prefix => PublicIdPrefixes.SetlistShow;

    public static SetlistShowId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out SetlistShowId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}

public readonly record struct SourceReviewId(Guid Value) : IUuidCatalogId<SourceReviewId>
{
    public static string Prefix => PublicIdPrefixes.SourceReview;

    public static SourceReviewId Parse(string value) => new(UuidTypeId.Parse(value, Prefix));

    public static bool TryParse(string? value, out SourceReviewId id)
    {
        var valid = UuidTypeId.TryParse(value, Prefix, out var uuid);
        id = new(uuid);
        return valid;
    }

    public override string ToString() => UuidTypeId.Format(Prefix, Value);
}
