using System;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public static class ModelCalls
{
    public static Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid? run,
        ModelCallRecord call
    )
    {
        var json = IsJson(call.Reply.Json);
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
                request = call.Request,
                reply = json ? call.Reply.Json : null,
                raw = json ? null : call.Reply.Json,
                outcome = call.Outcome.ToJsonString(),
                input = call.Reply.InputTokens,
                output = call.Reply.OutputTokens,
            },
            tx
        );
    }

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
