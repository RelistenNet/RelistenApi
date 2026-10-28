using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Normalization.Songs;

namespace Relisten.Normalization;

/// What one model call was and what became of it (norm.model_calls): job and prompt version,
/// the request data as sent, the reply as received, and the outcome (accepted, unsure, set
/// aside and why).
public sealed record ModelCallRecord(
    string Job,
    string PromptVersion,
    string Model,
    string Request,
    ModelReply Reply,
    JsonObject Outcome
);

/// Writes model-call receipts, append-only, in the transaction that writes the call's answers
/// (run: that run, or null when the answer wrote nothing).
public static partial class ModelCalls
{
    public static Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid? run,
        ModelCallRecord call
    )
    {
        // Postgres text and jsonb hold no NUL, and a model can still send one ("\u0000"); the
        // receipt keeps everything else it said rather than failing the chunk it records.
        var reply = WithoutNul(call.Reply.Json);
        var json = IsJson(reply);
        return connection.ExecuteAsync(
            """
            INSERT INTO norm.model_calls (uuid, run_uuid, job, prompt_version, model, provider_request_id,
                                          request, reply, raw_reply, outcome, input_tokens, output_tokens)
            VALUES (@uuid, @run, @job, @prompt, @model, @requestId, @request::jsonb,
                    @reply::jsonb, @raw, @outcome::jsonb, @input, @output)
            """,
            new
            {
                uuid = Guid.CreateVersion7(),
                run,
                job = call.Job,
                prompt = call.PromptVersion,
                model = call.Model,
                requestId = call.Reply.RequestId,
                request = WithoutNul(call.Request),
                reply = json ? reply : null,
                raw = json ? null : reply,
                outcome = WithoutNul(call.Outcome.ToJsonString()),
                input = call.Reply.InputTokens,
                output = call.Reply.OutputTokens,
            },
            tx
        );
    }

    /// The text with every NUL replaced by U+FFFD, as a character and as a JSON escape. An
    /// escaped backslash before "u0000" is literal text and stays.
    internal static string WithoutNul(string text) =>
        NulEscape().Replace(text.Replace('\0', '\uFFFD'), m => m.Groups[1].Value + "\\uFFFD");

    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.IgnoreCase)]
    private static partial Regex NulEscape();

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// A JSON array of strings, for an outcome.
    public static JsonArray Strings(System.Collections.Generic.IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }
}
