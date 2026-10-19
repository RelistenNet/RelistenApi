using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace Relisten.Kb.Catalogue;

/// What one model call returned: the JSON text that matched the schema, and its token counts.
public sealed record ModelReply(string Json, int InputTokens, int OutputTokens);

/// A structured-output language model: instructions, data, and the JSON Schema the answer must
/// match. Tests substitute a fake.
public interface ICatalogueModel
{
    string ModelId { get; }

    Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    );
}

/// OpenAI chat completions with strict JSON Schema output (string-theory docs/llm.md §Stack).
/// The model and key come from OpenAI:Model and OpenAI:ApiKey.
public sealed class OpenAiCatalogueModel(IConfiguration configuration) : ICatalogueModel
{
    private ChatClient? client;

    public string ModelId =>
        configuration["OpenAI:Model"]
        ?? throw new InvalidOperationException("OpenAI:Model is not configured");

    // Created on first use, so hosts without a key still start; a call without one fails.
    private ChatClient Client =>
        client ??= new ChatClient(
            ModelId,
            new System.ClientModel.ApiKeyCredential(
                configuration["OpenAI:ApiKey"]
                    ?? throw new InvalidOperationException("OpenAI:ApiKey is not configured")
            ),
            // A chunk's answer can take minutes to generate; the SDK's default is 100 s.
            new OpenAI.OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) }
        );

    public async Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    )
    {
        // Instructions first and data last, so the stable prefix is cached by the provider.
        // The data is untrusted text: it only ever travels as the user message's JSON.
        var completion = await Client.CompleteChatAsync(
            [new SystemChatMessage(instructions), new UserChatMessage(data)],
            new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    schemaName,
                    BinaryData.FromString(schemaJson),
                    jsonSchemaIsStrict: true
                ),
            },
            cancellationToken
        );

        var reply = completion.Value;
        return new ModelReply(
            reply.Content[0].Text,
            reply.Usage?.InputTokenCount ?? 0,
            reply.Usage?.OutputTokenCount ?? 0
        );
    }
}
