using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Accumulates matching membership, not supporting-account history. Repeated assertions do
/// not spend distinct-value capacity; a value omitted by either bound remains a blocking gap.
/// The reader owns source eligibility, payload authentication, owner clocks and raw witnesses.
/// </summary>
internal sealed class PeopleActorMatchingAccumulator(int maximumDistinctFacts, int maximumBytes)
{
    private readonly HashSet<string> names = new(System.StringComparer.Ordinal);
    private readonly HashSet<string> aliases = new(System.StringComparer.Ordinal);
    private readonly HashSet<PeopleContactLookup> contacts = [];
    private readonly HashSet<PeopleProfileLookup> profiles = [];
    private readonly HashSet<string> kinds = new(System.StringComparer.Ordinal);
    private readonly HashSet<string> omitted = new(System.StringComparer.Ordinal);
    private bool hasContext;
    private bool attachmentUncertainty;
    private bool conflictingKind;

    // Empty arrays, property names and false booleans provide a conservative fixed envelope.
    // Each added value includes a comma allowance. False -> true only shortens JSON.
    private int bytes = InitialBytes(maximumBytes);

    public IReadOnlyList<string> OmittedKinds =>
        omitted.Order(System.StringComparer.Ordinal).ToArray();

    public void Add(string creditedName, CreditedActorFacts<NormalizedEvidenceReference> facts)
    {
        hasContext = true;
        var context = PeopleActorProjection.Context(new[] { facts })!;
        attachmentUncertainty |= context.HasAttachmentUncertainty;
        conflictingKind |= context.HasConflictingKind;

        Add(names, creditedName, "names");
        foreach (var alias in facts.Aliases)
        {
            Add(aliases, alias.Wording, "aliases");
        }
        foreach (var contact in context.Contacts)
        {
            Add(contacts, contact, "contacts");
        }
        foreach (var profile in context.Profiles)
        {
            Add(profiles, profile, "profiles");
        }
        if (facts.Kind != null)
        {
            Add(kinds, facts.Kind.Kind, "source_kinds");
        }
    }

    public PersonActorMatchingFacts Build()
    {
        return new(
            hasContext,
            names.Order(System.StringComparer.Ordinal).ToArray(),
            aliases.Order(System.StringComparer.Ordinal).ToArray(),
            contacts
                .OrderBy(value => value.ContactType, System.StringComparer.Ordinal)
                .ThenBy(value => value.LookupKey, System.StringComparer.Ordinal)
                .ToArray(),
            profiles
                .OrderBy(value => value.Provider, System.StringComparer.Ordinal)
                .ThenBy(value => value.Namespace, System.StringComparer.Ordinal)
                .ThenBy(value => value.LookupKey, System.StringComparer.Ordinal)
                .ThenBy(value => value.Relationship)
                .ToArray(),
            kinds.Order(System.StringComparer.Ordinal).ToArray(),
            attachmentUncertainty,
            conflictingKind
        );
    }

    private void Add<T>(HashSet<T> values, T value, string kind)
    {
        if (values.Contains(value))
        {
            return;
        }
        if (values.Count >= maximumDistinctFacts)
        {
            omitted.Add(kind);
            return;
        }
        var required = JsonSerializer.SerializeToUtf8Bytes(value).Length + 1;
        if (required > maximumBytes - bytes)
        {
            omitted.Add("bytes");
            return;
        }
        values.Add(value);
        bytes += required;
    }

    private static int InitialBytes(int maximumBytes)
    {
        var size = JsonSerializer
            .SerializeToUtf8Bytes(
                new PersonActorMatchingFacts(false, [], [], [], [], [], false, false)
            )
            .Length;
        if (maximumBytes < size)
        {
            throw new System.ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        return size;
    }
}
