using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Exact published producer identities. Only alias-v2 roster replay and the known v6/v7
    // recording-decoder equivalence are supported; unrelated component changes must refuse.
    private const string LegacyAliasV2ExtractionPrefix =
        "recording-source-actor-envelope-v2/recording-extraction-v4/"
        + "retained-recording-credits-v2/recording-credits-v4/recording-actor-evidence-v1/"
        + "retained-actor-facts-v1/track-title-v1/local-selected-account-v1/";
    internal const string LegacyAliasV2DecoderV6ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v6/"
        + LegacyAliasV2ExtractionPrefix
        + RetainedSourceActorAliasExtractor.LegacyV2Version;
    internal const string LegacyAliasV2DecoderV7ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v7/"
        + LegacyAliasV2ExtractionPrefix
        + RetainedSourceActorAliasExtractor.LegacyV2Version;

    // One authentication pass owns decoder/root caches. Each original input and selected root
    // document stays open only while its scopes are replayed; file extraction borrows that
    // document rather than opening the entire retained payload once per file.
    private sealed class OriginalInputAuthenticator(
        string processingVersion,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> inputs,
        bool allowHistoricalProof
    )
    {
        private readonly NormalizationProviderDecoder decoder = new();
        private readonly Dictionary<Guid, ProviderDecodeResult> roots = [];
        private NormalizationRecordingSourceActorAliasBuilder? builder;

        public SourceActorResolutionInput Authenticate(
            NormalizationRecordingPreparedInput prepared,
            SourceActorExtractionOutput group,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            var input = inputs[prepared.Binding.InputRevisionUuid];
            var decoded = decoder.Decode(input, ct);
            var recording = NormalizationRecordingExtractor.RequireRecording(
                input,
                decoded,
                prepared.Binding
            );
            var selected = SelectOriginalRoot(input, decoded, prepared.Binding, ct);
            var root =
                selected == null
                    ? null
                    : inputs[prepared.Binding.SupportingInputs!.Single().InputRevisionUuid];

            using var document = NormalizationRetainedDocument.Open(input, ct);
            using var rootDocument =
                root == null ? null : NormalizationRetainedDocument.Open(root, ct);
            SourceActorResolutionScope[] scopes =
                group.SourceActorAliases == null
                    ? []
                    : AuthenticateScopes(
                        input,
                        document,
                        recording,
                        root,
                        rootDocument,
                        selected,
                        group.SourceActorAliases,
                        ct
                    );
            return new(prepared, 0, group.SourceActorAliases != null, scopes, group.DecodeIssues);
        }

        private NormalizationLocalRecordingContext? SelectOriginalRoot(
            NormalizationInputRevision input,
            ProviderDecodeResult decoded,
            NormalizationRecordingBindingSnapshot binding,
            CancellationToken ct
        )
        {
            if (input.Resource.Provider != "local")
            {
                return null;
            }
            var root = inputs[binding.SupportingInputs!.Single().InputRevisionUuid];
            if (!roots.TryGetValue(root.Uuid, out var rootDecoded))
            {
                rootDecoded = NormalizationLocalRecordingSelection.DecodeRoot(
                    root,
                    decoder,
                    cancellationToken: ct
                );
                roots.Add(root.Uuid, rootDecoded);
            }
            return NormalizationLocalRecordingSelection.Select(
                input,
                decoded,
                binding,
                root,
                rootDecoded,
                ct
            );
        }

        private SourceActorResolutionScope[] AuthenticateScopes(
            NormalizationInputRevision input,
            NormalizationRetainedDocument document,
            ProviderRecordingObservation recording,
            NormalizationInputRevision? root,
            NormalizationRetainedDocument? rootDocument,
            NormalizationLocalRecordingContext? selected,
            IReadOnlyList<NormalizationRecordingSourceActorAliasScope> aliases,
            CancellationToken ct
        )
        {
            builder ??= new(RequireGrammar());
            var expected = builder.Build(input, document, recording, ct).AsEnumerable();
            if (selected != null)
            {
                expected = expected.Concat(
                    builder.Build(root!, rootDocument!, selected.RootAccount, ct)
                );
            }
            if (!SourceActorResolutionCheckpoint.Same(aliases, expected.ToArray()))
            {
                throw new ArgumentException(
                    "Source actor scopes must reproduce their original published recording, files and selected root."
                );
            }
            return aliases
                .Select(scope => new SourceActorResolutionScope(
                    scope,
                    ReadFields(
                        scope,
                        scope.InputRevisionUuid == input.Uuid ? document : rootDocument!
                    ),
                    []
                ))
                .ToArray();
        }

        private SourceActorAliasGrammar RequireGrammar()
        {
            if (processingVersion == NormalizationRecordingProcessor.Version)
            {
                return SourceActorAliasGrammar.Current;
            }
            if (
                allowHistoricalProof
                && (
                    processingVersion == LegacyAliasV2DecoderV6ProcessingVersion
                    || processingVersion == LegacyAliasV2DecoderV7ProcessingVersion
                )
                && NormalizationRecordingProcessor.Version
                    == "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v7/"
                        + LegacyAliasV2ExtractionPrefix
                        + RetainedSourceActorAliasExtractor.Version
            )
            {
                return SourceActorAliasGrammar.LegacyV2;
            }
            throw new ArgumentException(
                "Source actor scopes must reproduce their original published recording, files and selected root."
            );
        }

        private static SourceActorResolutionField[] ReadFields(
            NormalizationRecordingSourceActorAliasScope scope,
            NormalizationRetainedDocument document
        ) =>
            scope
                .Extraction.Scopes.SelectMany(coverage =>
                    coverage.Evidence.Select(evidence =>
                    {
                        document.ValidateEvidence(evidence);
                        var raw =
                            evidence.Quote
                            ?? throw new ArgumentException(
                                "Source field coverage must retain its complete original text."
                            );
                        return new SourceActorResolutionField(
                            coverage.Field,
                            raw,
                            SourceActorResolutionCheckpoint.TextHash(raw),
                            evidence
                        );
                    })
                )
                .ToArray();
    }
}
