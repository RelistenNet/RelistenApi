using System.Security.Cryptography;

namespace Relisten.UserApi.Services;

public sealed class ShortIdService
{
    private static readonly char[] Alphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

    public string Generate()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }
}
