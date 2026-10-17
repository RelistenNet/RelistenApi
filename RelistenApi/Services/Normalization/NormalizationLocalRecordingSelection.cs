using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>The directory and its single retained root account, without widening file ownership.</summary>
public sealed record NormalizationLocalRecordingContext(
    ProviderRecordingObservation DirectoryRecording,
    ProviderRecordingObservation RootAccount,
    IReadOnlyList<ProviderDecodeIssue> RootIssues
);

/// <summary>
/// Selects the root account that supports one Local directory. A metadata root describes many
/// directories; its other accounts must never supply recording credits to this directory.
/// </summary>
public static class NormalizationLocalRecordingSelection
{
    public const string Version = "local-selected-account-v1";
    public const int DefaultMaximumAccounts = 100_000;

    /// <summary>
    /// Checks the account count before creating provider observations. Malformed entries also
    /// consume the bound; the decoder still reports their original shape errors. Bytes must be
    /// read through a bounded evidence reader before calling this method.
    /// </summary>
    public static ProviderDecodeResult DecodeRoot(
        NormalizationInputRevision rootInput,
        NormalizationProviderDecoder decoder,
        int maximumAccounts = DefaultMaximumAccounts,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAccounts, 1);
        if (
            rootInput.Resource.Provider != "local"
            || rootInput.Resource.ResourceType != "metadata_root"
        )
        {
            throw new ArgumentException(
                "A retained Local metadata root is required.",
                nameof(rootInput)
            );
        }
        using (var document = NormalizationRetainedDocument.Open(rootInput, cancellationToken))
        {
            var root = document.Resolve("");
            if (
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
            )
            {
                var count = 0;
                foreach (var date in data.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (date.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    var entries = date.Value.GetArrayLength();
                    if (entries > maximumAccounts - count)
                    {
                        throw new InvalidOperationException(
                            "Local metadata root exceeds its directory account limit."
                        );
                    }
                    count += entries;
                }
            }
        }
        return decoder.Decode(rootInput, cancellationToken);
    }

    /// <summary>
    /// Requires the exact supporting root revision and one account whose provider identity is
    /// the directory. Returned root issues and prose remain inside that account's original
    /// JSON subtree. Source-wide root evidence does not become a file origin.
    /// </summary>
    public static NormalizationLocalRecordingContext Select(
        NormalizationInputRevision directoryInput,
        ProviderDecodeResult directoryDecoded,
        NormalizationRecordingBindingSnapshot binding,
        NormalizationInputRevision rootInput,
        ProviderDecodeResult rootDecoded,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedRoot = NormalizationProviderResources.LocalRootForDirectory(
            directoryInput.Resource
        );
        if (
            rootInput.Resource != expectedRoot
            || rootInput.Uuid == Guid.Empty
            || directoryInput.Uuid == Guid.Empty
            || directoryDecoded.InputRevisionUuid != directoryInput.Uuid
            || directoryDecoded.Resource != directoryInput.Resource
            || directoryDecoded.Recordings.Count != 1
            || rootDecoded.InputRevisionUuid != rootInput.Uuid
            || rootDecoded.Resource != rootInput.Resource
            || binding.InputRevisionUuid != directoryInput.Uuid
            || binding.Resource != directoryInput.Resource
            || binding.SupportingInputs is not { Count: 1 }
            || binding.SupportingInputs[0]
                != new NormalizationBindingInputReference(rootInput.Uuid, rootInput.Resource)
        )
        {
            throw new ArgumentException(
                "Local recording extraction requires the exact directory and supporting root revisions."
            );
        }
        var directory = directoryDecoded.Recordings[0];
        if (
            directory.Origin.Item != directoryInput.Resource
            || directory.Origin.Scope != ProviderObservationScope.ResourceItem
        )
        {
            throw new ArgumentException("A decoded Local directory recording is required.");
        }
        var accounts = rootDecoded
            .Recordings.Where(account => account.Origin.Item == directoryInput.Resource)
            .ToArray();
        if (accounts.Length != 1)
        {
            throw new ArgumentException(
                "Local recording extraction requires one retained root directory account."
            );
        }
        var account = accounts[0];
        using var rootDocument = NormalizationRetainedDocument.Open(rootInput, cancellationToken);
        rootDocument.ValidateEvidence(account.Origin.Evidence);
        var accountValue = rootDocument.Resolve(account.Origin.Evidence.JsonPointer);
        if (
            account.Origin.Scope != ProviderObservationScope.CatalogMember
            || account.Origin.Evidence.JsonPointer.Length == 0
            || accountValue.ValueKind != JsonValueKind.Object
            || !accountValue.TryGetProperty("dir", out var dir)
            || dir.ValueKind != JsonValueKind.String
            || NormalizationProviderResources.LocalDirectory(
                rootInput.Resource.ProviderKey,
                dir.GetString()!
            ) != directoryInput.Resource
            || account.Files.Count != 0
        )
        {
            throw new ArgumentException(
                "The selected root account does not describe this Local directory."
            );
        }
        ValidateAccountText(rootDocument, account, accountValue, cancellationToken);
        var issues = rootDecoded
            .Issues.Where(issue => WithinAccount(account.Origin.Evidence, issue.Evidence))
            .ToArray();
        foreach (var issue in issues)
        {
            rootDocument.ValidateEvidence(issue.Evidence);
        }
        return new(directory, account, issues);
    }

    private static void ValidateAccountText(
        NormalizationRetainedDocument document,
        ProviderRecordingObservation account,
        JsonElement accountValue,
        CancellationToken cancellationToken
    )
    {
        var originalFields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (accountValue.TryGetProperty("taper_notes", out var notes))
        {
            var pointer = account.Origin.Evidence.JsonPointer + "/taper_notes";
            if (notes.ValueKind == JsonValueKind.String)
            {
                originalFields.Add(pointer, notes.GetString()!);
            }
            else if (notes.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var note in notes.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note.ValueKind == JsonValueKind.String)
                    {
                        originalFields.Add(
                            pointer + "/" + index.ToString(CultureInfo.InvariantCulture),
                            note.GetString()!
                        );
                    }
                    index++;
                }
            }
        }
        foreach (var field in account.Text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateEvidence(field.Text.Evidence);
            if (
                field.Field != "taper_notes"
                || !originalFields.TryGetValue(field.Text.Evidence.JsonPointer, out var original)
                || original != field.Text.Value
            )
            {
                throw new ArgumentException(
                    "Root recording prose must retain the selected account's original taper notes."
                );
            }
        }
    }

    private static bool WithinAccount(
        NormalizedEvidenceReference account,
        NormalizedEvidenceReference field
    ) =>
        account.InputRevisionUuid == field.InputRevisionUuid
        && (
            field.JsonPointer == account.JsonPointer
            || field.JsonPointer.StartsWith(account.JsonPointer + "/", StringComparison.Ordinal)
        );
}
