using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace RelistenUserService.Library;

public sealed class LibraryCursorProtector
{
    private const byte FormatVersion = 1;
    private const int PayloadSize = 1 + 16 + sizeof(long);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);
    private readonly ITimeLimitedDataProtector _protector;

    public LibraryCursorProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector("Relisten.UserService.LibraryCursor.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Protect(Guid userId, long revision)
    {
        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[0] = FormatVersion;
        userId.TryWriteBytes(payload[1..17]);
        BinaryPrimitives.WriteInt64BigEndian(payload[17..], revision);
        return WebEncoders.Base64UrlEncode(_protector.Protect(payload.ToArray(), Lifetime));
    }

    public bool TryUnprotect(string? cursor, Guid expectedUserId, out long revision)
    {
        revision = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(WebEncoders.Base64UrlDecode(cursor), out _);
            if (payload.Length != PayloadSize || payload[0] != FormatVersion)
            {
                return false;
            }

            var userId = new Guid(payload.AsSpan(1, 16));
            revision = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(17));
            return userId == expectedUserId && revision >= 0;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
