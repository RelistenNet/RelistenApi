using System.Security.Cryptography;
using System.Text;

namespace RelistenUserService.Mcp;

/// <summary>
/// Supplies the IDs that one write tool call sends to the playlist service.
/// </summary>
/// <remarks>
/// The playlist service deduplicates a write by its command or operation ID and by a hash of
/// its payload, which includes every ID the write creates. A model that retries after a lost
/// response cannot repeat IDs it never saw, so a caller-supplied idempotency key derives all
/// of them. The same key, account, target and position then yield the same IDs, and the
/// playlist service replays the stored result instead of writing twice. Without a key each
/// ID is new, so a repeated call is a separate write.
///
/// The account is part of the derivation so that one listener's key can never collide with
/// another listener's receipts.
/// </remarks>
internal sealed class McpWriteIds
{
    public const int MaximumKeyLength = 200;

    private readonly string? derivationPrefix;

    private McpWriteIds(string? derivationPrefix) => this.derivationPrefix = derivationPrefix;

    /// <param name="target">
    /// What the key is scoped to: the tool name for a creation, or the playlist being edited.
    /// It must not contain a line break.
    /// </param>
    public static McpWriteIds For(UserId actor, string target, string? idempotencyKey) =>
        new(
            idempotencyKey is null
                ? null
                : $"relisten-mcp-write-v1\n{actor.Value}\n{target}\n{idempotencyKey}\n"
        );

    public static bool IsValidKey(string? idempotencyKey) =>
        idempotencyKey is null or { Length: >= 1 and <= MaximumKeyLength };

    /// <summary>Returns the ID for one role and position within the call.</summary>
    public Guid Create(string role, params int[] position)
    {
        if (derivationPrefix is null)
        {
            return Guid.CreateVersion7();
        }

        // The key is the only caller-controlled part and may contain line breaks, so the
        // fixed-format role and position follow it and are the last line of the hash input.
        var input = $"{derivationPrefix}{role}/{string.Join('/', position)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16);

        // Account IDs must parse as UUIDv7. Only the version and variant bits are checked,
        // so the timestamp field carries hash bits here.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
