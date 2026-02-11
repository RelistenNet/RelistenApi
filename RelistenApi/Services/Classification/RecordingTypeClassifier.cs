using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;

namespace Relisten.Services.Classification
{
    /// <summary>
    /// Classifies a source's recording type (soundboard, audience, matrix, etc.)
    /// using a two-layer pipeline:
    ///   Layer 1: Rule-based regex pattern matching (fast, free, ~70% of cases)
    ///   Layer 2: LLM classification via GPT-4o-mini (for ambiguous cases)
    /// </summary>
    public class RecordingTypeClassifier
    {
        private readonly LlmClassificationService _llm;
        private readonly ILogger<RecordingTypeClassifier> _log;

        /// <summary>Minimum confidence from rule-based layer to skip LLM.</summary>
        private const float LlmThreshold = 0.7f;

        public RecordingTypeClassifier(
            LlmClassificationService llm,
            ILogger<RecordingTypeClassifier> log)
        {
            _llm = llm;
            _log = log;
        }

        /// <summary>
        /// Classify a source based on its metadata fields.
        /// Returns the classification result with type, confidence, and method.
        /// </summary>
        public async Task<RecordingTypeResult> ClassifyAsync(
            SourceMetadataForClassification metadata,
            bool allowLlm = true,
            CancellationToken ct = default)
        {
            // Layer 1: Rule-based classification
            var ruleResult = ClassifyWithRules(metadata);

            if (ruleResult.Confidence >= LlmThreshold || !allowLlm)
            {
                return ruleResult;
            }

            // Layer 2: LLM classification for ambiguous cases
            try
            {
                var llmResult = await ClassifyWithLlm(metadata, ct);
                if (llmResult != null)
                {
                    return llmResult;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LLM classification failed for source, falling back to rule result");
            }

            // Fall back to rule-based result if LLM fails
            return ruleResult;
        }

        /// <summary>
        /// Synchronous rule-based classification. No API calls, no cost.
        /// </summary>
        public RecordingTypeResult ClassifyWithRules(SourceMetadataForClassification meta)
        {
            var allText = CombineFields(meta);

            // Check for webcast first (most specific)
            if (IsWebcast(allText, meta))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.Webcast,
                    Confidence = 0.85f,
                    Method = "rule"
                };
            }

            // Check for FM/Pre-FM
            var fmResult = CheckFm(allText, meta);
            if (fmResult != null)
            {
                return fmResult;
            }

            // Check for Matrix (before SBD, since matrix contains SBD references)
            if (IsMatrix(allText, meta))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.Matrix,
                    Confidence = 0.85f,
                    Method = "rule"
                };
            }

            // Check for Ultra Matrix
            if (IsUltraMatrix(allText, meta))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.UltraMatrix,
                    Confidence = 0.80f,
                    Method = "rule"
                };
            }

            // Check for Soundboard
            var sbdResult = CheckSoundboard(allText, meta);
            if (sbdResult != null)
            {
                return sbdResult;
            }

            // Check if text mentions SBD-adjacent position (false positive for SBD)
            if (IsSbdAdjacent(allText))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.Audience,
                    Confidence = 0.75f,
                    Method = "rule"
                };
            }

            // Default: if no indicators found, classify as unknown with low confidence
            // (let the LLM layer handle it or leave as unknown)
            return new RecordingTypeResult
            {
                RecordingType = RecordingType.Unknown,
                Confidence = 0.3f,
                Method = "rule"
            };
        }

        #region Rule-Based Pattern Matching

        // Patterns that indicate a true soundboard recording
        private static readonly Regex SbdPositivePattern = new(
            @"\b(soundboard|sbd)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Patterns that negate SBD (indicate proximity to board, not direct feed)
        private static readonly Regex SbdNegatorPattern = new(
            @"(near|behind|from|by\s+the|next\s+to|beside|close\s+to|adjacent|area|rows?\s+(from|behind)|feet?\s+(from|behind)|in\s+front\s+of)\s+.{0,20}\b(soundboard|sbd|board)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Direct SBD feed indicators (high confidence)
        private static readonly Regex SbdDirectFeedPattern = new(
            @"\b(board\s*feed|direct\s*(from\s*)?board|console\s*recording|board\s*patch|direct\s*patch|pre[- ]?board|post[- ]?board)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matrix indicators
        private static readonly Regex MatrixPattern = new(
            @"\bmatrix\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // SBD + AUD blend patterns (matrix)
        private static readonly Regex SbdAudBlendPattern = new(
            @"\b(sbd|soundboard)\s*[\+\&]\s*(aud|audience)\b|\b(aud|audience)\s*[\+\&]\s*(sbd|soundboard)\b|\bboard\s*[\+\&]?\s*mics?\b|\bmics?\s*[\+\&]?\s*board\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Ultra Matrix indicators
        private static readonly Regex UltraMatrixPattern = new(
            @"\bultra[- ]?matrix\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // FM/Pre-FM patterns
        private static readonly Regex PreFmPattern = new(
            @"\bpre[- ]?fm\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FmPattern = new(
            @"\b(fm\s*broadcast|radio\s*broadcast|fm\s*recording|broadcast\s*recording)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Webcast patterns
        private static readonly Regex WebcastPattern = new(
            @"\b(webcast|live\s*stream|livestream|nugs\.net|nugs\.tv|live\s*phish\s*stream)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // SBD-adjacent (recorded near the board but not a direct feed)
        private static readonly Regex SbdAdjacentPattern = new(
            @"(taped|recorded|set\s*up|mic(rophone)?s?\s*(placed|set|positioned)?)\s*.{0,30}\b(near|behind|beside|by|next\s+to|in\s+front\s+of|close\s+to)\s*.{0,15}\b(soundboard|sbd|board|console|mixer)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private RecordingTypeResult? CheckSoundboard(string allText, SourceMetadataForClassification meta)
        {
            // Check for direct feed indicators first (highest confidence)
            if (SbdDirectFeedPattern.IsMatch(allText))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.Soundboard,
                    Confidence = 0.95f,
                    Method = "rule"
                };
            }

            // Check for SBD keyword
            if (SbdPositivePattern.IsMatch(allText))
            {
                // But verify it's not negated by proximity language
                if (SbdNegatorPattern.IsMatch(allText))
                {
                    // SBD mentioned but with negation - this is ambiguous
                    // Return low confidence so LLM layer handles it
                    return new RecordingTypeResult
                    {
                        RecordingType = RecordingType.Audience,
                        Confidence = 0.5f,
                        Method = "rule"
                    };
                }

                // Check the identifier specifically (high signal)
                if (meta.Identifier != null && SbdPositivePattern.IsMatch(meta.Identifier))
                {
                    return new RecordingTypeResult
                    {
                        RecordingType = RecordingType.Soundboard,
                        Confidence = 0.90f,
                        Method = "rule"
                    };
                }

                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.Soundboard,
                    Confidence = 0.80f,
                    Method = "rule"
                };
            }

            return null;
        }

        private bool IsMatrix(string allText, SourceMetadataForClassification meta)
        {
            // Explicit "matrix" keyword
            if (MatrixPattern.IsMatch(allText) && !UltraMatrixPattern.IsMatch(allText))
            {
                return true;
            }

            // SBD + AUD blend patterns
            if (SbdAudBlendPattern.IsMatch(allText))
            {
                return true;
            }

            return false;
        }

        private bool IsUltraMatrix(string allText, SourceMetadataForClassification meta)
        {
            return UltraMatrixPattern.IsMatch(allText);
        }

        private RecordingTypeResult? CheckFm(string allText, SourceMetadataForClassification meta)
        {
            if (PreFmPattern.IsMatch(allText))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.PreFm,
                    Confidence = 0.90f,
                    Method = "rule"
                };
            }

            if (FmPattern.IsMatch(allText))
            {
                return new RecordingTypeResult
                {
                    RecordingType = RecordingType.Fm,
                    Confidence = 0.85f,
                    Method = "rule"
                };
            }

            return null;
        }

        private bool IsWebcast(string allText, SourceMetadataForClassification meta)
        {
            return WebcastPattern.IsMatch(allText);
        }

        private bool IsSbdAdjacent(string allText)
        {
            return SbdAdjacentPattern.IsMatch(allText);
        }

        private static string CombineFields(SourceMetadataForClassification meta)
        {
            return string.Join(" ",
                meta.Identifier ?? "",
                meta.Title ?? "",
                meta.Source ?? "",
                meta.Lineage ?? "",
                meta.TaperNotes ?? "",
                meta.Description ?? ""
            );
        }

        #endregion

        #region LLM Classification

        private const string SystemPrompt = @"You are an expert at classifying live music recording sources. Given metadata about a recording, determine its recording type.

RECORDING TYPES:
- ""soundboard"": A direct audio feed from the mixing console/soundboard. The signal comes from the board itself, NOT from microphones placed near the board.
- ""audience"": Microphones placed in the audience/crowd to capture the live sound. Also called ""FOB"" (front of board), ""AUD"", or ""taper"" recordings. IMPORTANT: A recording described as being made ""near the soundboard"", ""behind the board"", or ""from the SBD area"" is an AUDIENCE recording, not a soundboard recording. The taper's physical proximity to the mixing console does not make it a soundboard recording.
- ""matrix"": A blend/mix of a soundboard feed and audience microphones. Often described as ""SBD + AUD"" or ""board + mics"".
- ""ultra_matrix"": A multi-source matrix using more than two source types.
- ""pre_fm"": A pre-broadcast FM radio recording (captured before transmission).
- ""fm"": An FM radio broadcast recording.
- ""webcast"": A live stream or webcast recording (e.g., from nugs.net).
- ""unknown"": Cannot determine the recording type from the available metadata.

COMMON FALSE POSITIVES TO WATCH FOR:
- ""Recorded from behind the SBD"" = AUDIENCE (taper position, not source type)
- ""Taped 3 rows from the soundboard"" = AUDIENCE
- ""Set up near the board"" = AUDIENCE
- ""SBD area"" = AUDIENCE (location reference)
- ""Close to the console"" = AUDIENCE

Respond with JSON: {""recording_type"": ""<type>"", ""confidence"": <0.0-1.0>, ""reasoning"": ""<brief explanation>""}";

        private async Task<RecordingTypeResult?> ClassifyWithLlm(
            SourceMetadataForClassification meta,
            CancellationToken ct)
        {
            var userContent = BuildLlmUserContent(meta);

            var response = await _llm.ClassifyAsync<LlmRecordingTypeResponse>(
                SystemPrompt,
                userContent,
                "rectype",
                ct);

            if (response == null)
            {
                return null;
            }

            var parsed = ParseRecordingType(response.recording_type);
            if (parsed == null)
            {
                _log.LogWarning("LLM returned unknown recording type: {Type}", response.recording_type);
                return null;
            }

            return new RecordingTypeResult
            {
                RecordingType = parsed.Value,
                Confidence = Math.Clamp(response.confidence, 0f, 1f),
                Method = "llm",
                Reasoning = response.reasoning
            };
        }

        private static string BuildLlmUserContent(SourceMetadataForClassification meta)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(meta.Identifier))
                parts.Add($"Archive.org identifier: {meta.Identifier}");
            if (!string.IsNullOrWhiteSpace(meta.Title))
                parts.Add($"Title: {meta.Title}");
            if (!string.IsNullOrWhiteSpace(meta.Source))
                parts.Add($"Source: {meta.Source}");
            if (!string.IsNullOrWhiteSpace(meta.Lineage))
                parts.Add($"Lineage: {meta.Lineage}");
            if (!string.IsNullOrWhiteSpace(meta.TaperNotes))
                parts.Add($"Taper notes: {meta.TaperNotes}");
            if (!string.IsNullOrWhiteSpace(meta.Description))
                parts.Add($"Description: {TruncateForLlm(meta.Description)}");

            return string.Join("\n", parts);
        }

        private static string TruncateForLlm(string text)
        {
            // Keep description under 2000 chars for LLM context
            return text.Length > 2000 ? text[..2000] + "..." : text;
        }

        private static RecordingType? ParseRecordingType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;

            return type.ToLowerInvariant().Replace("-", "").Replace("_", "") switch
            {
                "soundboard" or "sbd" => RecordingType.Soundboard,
                "audience" or "aud" or "fob" => RecordingType.Audience,
                "matrix" => RecordingType.Matrix,
                "ultramatrix" => RecordingType.UltraMatrix,
                "prefm" => RecordingType.PreFm,
                "fm" => RecordingType.Fm,
                "webcast" => RecordingType.Webcast,
                "unknown" => RecordingType.Unknown,
                _ => null
            };
        }

        #endregion
    }

    #region Models

    /// <summary>
    /// Input metadata for recording type classification.
    /// Constructed from archive.org metadata or source database fields.
    /// </summary>
    public class SourceMetadataForClassification
    {
        public string? Identifier { get; set; }
        public string? Title { get; set; }
        public string? Source { get; set; }
        public string? Lineage { get; set; }
        public string? TaperNotes { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Result of recording type classification.
    /// </summary>
    public class RecordingTypeResult
    {
        public RecordingType RecordingType { get; set; }
        public float Confidence { get; set; }
        public string Method { get; set; } = "rule";
        public string? Reasoning { get; set; }
    }

    /// <summary>
    /// JSON response shape from the LLM classification.
    /// </summary>
    internal class LlmRecordingTypeResponse
    {
        public string recording_type { get; set; } = "unknown";
        public float confidence { get; set; }
        public string? reasoning { get; set; }
    }

    #endregion
}
