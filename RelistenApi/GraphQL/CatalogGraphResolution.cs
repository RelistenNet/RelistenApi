using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.GraphQL;

public sealed class CatalogGraphReference
{
    public CatalogReferenceKind Kind { get; set; }
    public CatalogReferenceScheme Scheme { get; set; } = CatalogReferenceScheme.V4;
    public string Id { get; set; } = "";
    public string? ArtistId { get; set; }

    internal CatalogReference Read()
    {
        if (
            Id.Length > 1024
            || !CatalogReferenceLookup.Supports(Kind, Scheme)
            || !CatalogReferenceValidation.ValidIdentity(Kind, Scheme, Id)
        )
        {
            throw new FormatException("Unsupported catalog reference.");
        }
        var artist = ArtistId is { } id ? UuidTypeId.Parse(id, "artist") : (Guid?)null;
        if (artist is null && CatalogReferenceValidation.RequiresArtist(Kind, Scheme))
        {
            throw new FormatException("This legacy identity requires an artist.");
        }
        return new(Kind, Scheme, Id, artist);
    }
}

public sealed record CatalogGraphResolution(
    CatalogReferenceKind Kind,
    CatalogReferenceScheme Scheme,
    string Id,
    CatalogReferenceAvailability Availability,
    string? Reason,
    object? Target
);

public sealed class CatalogResolutionType : ObjectType<CatalogGraphResolution>
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphResolution> d)
    {
        d.Name("CatalogResolution");
        d.Field(r => r.Target).Type<CatalogTargetType>();
    }
}

public sealed class CatalogTargetType : UnionType
{
    protected override void Configure(IUnionTypeDescriptor d)
    {
        d.Name("CatalogTarget");
        d.Type<ArtistType>()
            .Type<ShowType>()
            .Type<SourceType>()
            .Type<SourceSetType>()
            .Type<SourceTrackType>()
            .Type<YearType>()
            .Type<TourType>()
            .Type<SongType>()
            .Type<ImportedSongType>()
            .Type<VenueType>()
            .Type<ImportedVenueType>()
            .Type<ArtistVenueType>()
            .Type<SourceReviewType>()
            .Type<SetlistShowType>();
    }
}
