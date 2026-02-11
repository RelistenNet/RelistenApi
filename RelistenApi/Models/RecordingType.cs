using System;
using System.Data;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum RecordingType
    {
        Unknown,
        Soundboard,
        Audience,
        Matrix,
        UltraMatrix,
        PreFm,
        Fm,
        Webcast
    }

    /// <summary>
    /// Extension methods for RecordingType enum.
    /// </summary>
    public static class RecordingTypeExtensions
    {
        /// <summary>
        /// Converts a RecordingType enum to the snake_case string stored in the database.
        /// Use this instead of ToString().ToLowerInvariant() which doesn't handle
        /// UltraMatrix ("ultra_matrix") and PreFm ("pre_fm") correctly.
        /// </summary>
        public static string ToDbString(this RecordingType value)
        {
            return value switch
            {
                RecordingType.Soundboard => "soundboard",
                RecordingType.Audience => "audience",
                RecordingType.Matrix => "matrix",
                RecordingType.UltraMatrix => "ultra_matrix",
                RecordingType.PreFm => "pre_fm",
                RecordingType.Fm => "fm",
                RecordingType.Webcast => "webcast",
                _ => "unknown"
            };
        }
    }

    /// <summary>
    /// Dapper type handler for mapping between PostgreSQL TEXT column and RecordingType enum.
    /// Database stores lowercase snake_case values (e.g., "soundboard", "ultra_matrix", "pre_fm").
    /// </summary>
    public class RecordingTypeHandler : SqlMapper.TypeHandler<RecordingType>
    {
        public override RecordingType Parse(object value)
        {
            var str = value?.ToString()?.ToLowerInvariant().Replace("_", "") ?? "unknown";
            return str switch
            {
                "soundboard" or "sbd" => RecordingType.Soundboard,
                "audience" or "aud" or "fob" => RecordingType.Audience,
                "matrix" => RecordingType.Matrix,
                "ultramatrix" => RecordingType.UltraMatrix,
                "prefm" => RecordingType.PreFm,
                "fm" => RecordingType.Fm,
                "webcast" => RecordingType.Webcast,
                _ => RecordingType.Unknown
            };
        }

        public override void SetValue(IDbDataParameter parameter, RecordingType value)
        {
            parameter.Value = value.ToDbString();
        }
    }
}
