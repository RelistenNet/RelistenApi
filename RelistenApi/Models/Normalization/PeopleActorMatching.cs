using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>A nonexclusive contact lookup, never a canonical external identity.</summary>
public sealed record PeopleContactLookup(string ContactType, string LookupKey);

/// <summary>Qualified source context. A shared profile or project account does not identify a person.</summary>
public sealed record PeopleProfileLookup(
    string Provider,
    string Namespace,
    string LookupKey,
    ActorProfileRelationship Relationship = ActorProfileRelationship.AttributedProfile
);

/// <summary>People-only nonunique retrieval routes, bounded separately from ordinary names.</summary>
public sealed record PeopleCandidateHints(
    IReadOnlyList<PeopleContactLookup> Contacts,
    IReadOnlyList<PeopleProfileLookup> Profiles,
    int Limit = 100
);

/// <summary>Source assertions for comparison; none grants identifier authority or actor kind.</summary>
public sealed record PersonActorMatchContext(
    IReadOnlyList<PeopleContactLookup> Contacts,
    IReadOnlyList<PeopleProfileLookup> Profiles,
    bool HasAttachmentUncertainty = false,
    bool HasConflictingKind = false
);

/// <summary>One supported lookup shape, shared by extraction projection and candidate retrieval.</summary>
public static class PeopleActorLookupContract
{
    public static bool Supports(PeopleContactLookup contact) =>
        Text(contact.ContactType, 64) && Text(contact.LookupKey, 512);

    public static bool Supports(PeopleProfileLookup profile) =>
        Text(profile.Provider, 255)
        && Text(profile.Namespace, 64)
        && Text(profile.LookupKey, 2048)
        && System.Enum.IsDefined(profile.Relationship);

    private static bool Text(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
}
