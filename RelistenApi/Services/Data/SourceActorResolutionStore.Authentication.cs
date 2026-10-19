using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Exact published producer identities. Historical replay retains the original grammar
    // and IDs; it never authorizes current interpretation or a new publication.
    private const string HistoricalCreditV4ExtractionPrefix =
        "recording-source-actor-envelope-v2/recording-extraction-v4/"
        + "retained-recording-credits-v2/recording-credits-v4/recording-actor-evidence-v1/"
        + "retained-actor-facts-v1/track-title-v1/local-selected-account-v1/";
    internal const string LegacyAliasV2DecoderV6ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v6/"
        + HistoricalCreditV4ExtractionPrefix
        + RetainedSourceActorAliasExtractor.LegacyV2Version;
    internal const string LegacyAliasV2DecoderV7ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v7/"
        + HistoricalCreditV4ExtractionPrefix
        + RetainedSourceActorAliasExtractor.LegacyV2Version;

    internal const string LegacyAliasV3DecoderV7ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v7/"
        + HistoricalCreditV4ExtractionPrefix
        + RetainedSourceActorAliasExtractor.LegacyV3Version;
    internal const string LegacyAliasV3DecoderV8ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v8/"
        + HistoricalCreditV4ExtractionPrefix
        + RetainedSourceActorAliasExtractor.LegacyV3Version;
    internal const string AliasV4TitleV1ProcessingVersion =
        "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v8/"
        + "recording-source-actor-envelope-v2/recording-extraction-v4/"
        + "retained-recording-credits-v2/recording-credits-v5/recording-actor-evidence-v1/"
        + "retained-actor-facts-v1/track-title-v1/local-selected-account-v1/"
        + "retained-source-actor-aliases-v1/source-actor-aliases-v4/recording-actor-evidence-v1/"
        + "retained-source-actor-alias-mapping-v1";

    // Pin the reader implementation as well as each historical producer. Decoder v8 keeps
    // the v6/v7 recording fields at their original coordinates; complete scope replay below
    // still refuses any changed field/file/root inventory. Future component changes require
    // an explicit compatibility decision rather than inheriting these historical grants.
    // Title v2 changes only the parallel title interpretation. Alias replay never consumes
    // those proposals: its complete original text/file/root scopes and local IDs stay intact.
    internal static bool SupportsHistoricalAliasReplay =>
        NormalizationRecordingProcessor.Version
        == "catalog-recording-processing-v4/legacy-recording-snapshot-v2/provider-observations-v8/"
            + "recording-source-actor-envelope-v2/recording-extraction-v4/"
            + "retained-recording-credits-v2/recording-credits-v5/recording-actor-evidence-v1/"
            + "retained-actor-facts-v1/track-title-v2/local-selected-account-v1/"
            + "retained-source-actor-aliases-v1/source-actor-aliases-v4/recording-actor-evidence-v1/"
            + "retained-source-actor-alias-mapping-v1";

    internal static SourceActorAliasGrammar RequireOriginalAliasGrammar(
        string processingVersion,
        bool allowHistoricalProof
    )
    {
        if (processingVersion == NormalizationRecordingProcessor.Version)
        {
            return SourceActorAliasGrammar.Current;
        }

        if (allowHistoricalProof && SupportsHistoricalAliasReplay)
        {
            switch (processingVersion)
            {
                case LegacyAliasV2DecoderV6ProcessingVersion:
                case LegacyAliasV2DecoderV7ProcessingVersion:
                    return SourceActorAliasGrammar.LegacyV2;
                case LegacyAliasV3DecoderV7ProcessingVersion:
                case LegacyAliasV3DecoderV8ProcessingVersion:
                    return SourceActorAliasGrammar.LegacyV3;
                case AliasV4TitleV1ProcessingVersion:
                    return SourceActorAliasGrammar.Current;
            }
        }

        throw new ArgumentException(
            "Source actor scopes must reproduce their original published recording, files and selected root."
        );
    }

    // One authentication pass owns decoder/root caches. Each original input and selected root
    // document stays open only while its scopes are replayed; file extraction borrows that
    // document rather than opening the entire retained payload once per file.
    internal sealed class OriginalInputAuthenticator(
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
            builder ??= new(RequireOriginalAliasGrammar(processingVersion, allowHistoricalProof));
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
