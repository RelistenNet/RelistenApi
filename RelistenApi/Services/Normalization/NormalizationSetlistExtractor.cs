using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>Retains evidenced account proposals without accepting music identities or catalog associations.</summary>
public sealed class NormalizationSetlistExtractor
{
    public const string Version = "setlist-extraction-v1";
    public const int MaximumAccountsPerInput = 1_000;
    public const int MaximumPassagesPerInput = 10_000;

    /// <summary>
    /// Uses the original decoded accounts, including conflicting observations of the same provider
    /// account. The processor and store reproduce decoding from retained bytes; this boundary checks
    /// their prepared assertions and every nested citation without interpreting provider data again.
    /// </summary>
    public NormalizationSetlistInputExtraction Extract(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded,
        NormalizationSetlistPreparedInput prepared,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accounts = RequireAccounts(input, decoded, prepared);
        using var document = NormalizationRetainedDocument.Open(input, cancellationToken);
        var evidence = new AccountEvidenceValidator(document, cancellationToken);
        var proposals = new List<NormalizationSetlistAccountProposal>();

        for (var accountIndex = 0; accountIndex < accounts.Length; accountIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = accounts[accountIndex];
            evidence.Validate(account);
            var passages = account
                .Passages.Select(
                    (passage, index) =>
                        new NormalizationSetlistPassageProposal(
                            ProposalUuid(input.Uuid, account.Origin, accountIndex, passage, index),
                            passage
                        )
                )
                .ToArray();
            proposals.Add(new(account, passages));
        }

        foreach (var issue in decoded.Issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateEvidence(issue.Evidence);
        }

        return new(input.Uuid, proposals.ToArray(), decoded.Issues);
    }

    private static ProviderSetlistObservation[] RequireAccounts(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded,
        NormalizationSetlistPreparedInput prepared
    )
    {
        if (
            input.Uuid == Guid.Empty
            || decoded.InputRevisionUuid != input.Uuid
            || decoded.Resource != input.Resource
            || prepared.Input.InputRevisionUuid != input.Uuid
            || prepared.Input.Resource != input.Resource
            || prepared.Contributor != input.Resource
        )
        {
            throw new ArgumentException("Setlist extraction requires one exact retained input.");
        }

        if (
            decoded.Setlists.Count > MaximumAccountsPerInput
            || decoded.Setlists.Sum(account => (long)account.Passages.Count)
                > MaximumPassagesPerInput
        )
        {
            throw new ArgumentException("Retained input exceeds the setlist extraction bounds.");
        }

        if (prepared.Accounts.Count == 0 || prepared.Accounts.Count > MaximumAccountsPerInput)
        {
            throw new ArgumentException(
                "Preparation must retain the requested account observations."
            );
        }

        var identity = prepared.Accounts[0].Account.Origin.Item;
        if (
            prepared.Accounts.Any(account =>
                account.Account.Origin.Item != identity
                || account.Binding.InputRevisionUuid != input.Uuid
                || account.Binding.Resource != input.Resource
                || account.Binding.Account != account.Account.Origin
            )
        )
        {
            throw new ArgumentException(
                "Prepared bindings must identify their original account assertions."
            );
        }

        var accounts = decoded.Setlists.Where(account => account.Origin.Item == identity).ToArray();
        // Compare a multiset, rather than a dictionary keyed by account identity or origin:
        // duplicate assertions must survive, including exact duplicates at the same origin.
        var actual = accounts
            .Select(account => JsonSerializer.Serialize(account))
            .Order(StringComparer.Ordinal);
        var expected = prepared
            .Accounts.Select(account => JsonSerializer.Serialize(account.Account))
            .Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Preparation must retain every exact decoded account assertion."
            );
        }

        return accounts;
    }

    private static Guid ProposalUuid(
        Guid inputRevisionUuid,
        ProviderObservationOrigin account,
        int accountIndex,
        ProviderPassageObservation passage,
        int passageIndex
    )
    {
        // Array indexes distinguish repeated songs and duplicate account observations, even when
        // their cited origins are identical. These are proposal keys, never accepted occurrences.
        var digest = SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    Version,
                    inputRevisionUuid,
                    account,
                    accountIndex,
                    passageIndex,
                    passage,
                }
            )
        );
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>Explicit traversal keeps new observation fields visible during contract changes.</summary>
    private sealed class AccountEvidenceValidator(
        NormalizationRetainedDocument document,
        CancellationToken cancellationToken
    )
    {
        public void Validate(ProviderSetlistObservation account)
        {
            document.ValidateEvidence(account.Origin.Evidence);
            foreach (var performer in account.Performers)
            {
                Value(performer.Name);
                Identities(performer.Identities);
            }

            Value(account.LiteralDate);
            if (account.Venue is { } venue)
            {
                document.ValidateEvidence(venue.Origin.Evidence);
                foreach (var name in venue.Names)
                {
                    Value(name);
                }
                Identities(venue.Identities);
                Value(venue.OriginalLocality);
                Value(venue.Country);
                Value(venue.Region);
                Value(venue.City);
                Value(venue.Address);
                Value(venue.Latitude);
                Value(venue.Longitude);
                Text(venue.Text);
            }

            foreach (var set in account.Sets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                document.ValidateEvidence(set.Evidence);
                Value(set.Label);
                Value(set.EncoreNumber);
                Identity(set.Identity);
            }

            foreach (var passage in account.Passages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Identity(passage.OccurrenceIdentity);
                Value(passage.Title);
                Identities(passage.SongIdentities);
                Value(passage.SetLabel);
                Value(passage.TransitionMarker);
                Value(passage.Segue);
                Value(passage.Tape);
                foreach (var number in passage.FootnoteNumbers)
                {
                    Value(number);
                }
                Text(passage.Annotations);
                foreach (var performer in passage.PerformerAnnotations)
                {
                    Value(performer.Name);
                    Identities(performer.Identities);
                }
            }

            Text(account.Text);
        }

        private void Text(IReadOnlyList<ProviderTextObservation> fields)
        {
            foreach (var field in fields)
            {
                Value(field.Text);
            }
        }

        private void Identities(IReadOnlyList<ObservedProviderIdentity> identities)
        {
            foreach (var identity in identities)
            {
                Identity(identity);
            }
        }

        private void Identity(ObservedProviderIdentity? identity)
        {
            if (identity != null)
            {
                document.ValidateEvidence(identity.Evidence);
            }
        }

        private void Value<T>(ObservedValue<T>? observation)
        {
            if (observation == null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateEvidence(observation.Evidence);
            if (observation.Evidence.StartCodePoint != null)
            {
                // HTML values may be entity-decoded or normalized; their exact original span
                // is validated above. Provider-specific interpretation stays in the decoder.
                return;
            }

            var retained = document.Resolve(observation.Evidence.JsonPointer);
            // Compare decoded scalar values, not JSON escaping or numeric formatting.
            if (!EqualityComparer<T>.Default.Equals(observation.Value, retained.Deserialize<T>()))
            {
                throw new ArgumentException("An observation must retain its exact cited value.");
            }
        }
    }
}
