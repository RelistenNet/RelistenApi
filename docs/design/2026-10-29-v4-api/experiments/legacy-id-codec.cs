// Investigation only; this is not the proposed production codec or ID API.
// Run with .NET 10: dotnet run --file <this-file> -- <TypeID spec valid.json>
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

string Encode(Guid value)
{
    var number = UInt128.Parse(value.ToString("N"), NumberStyles.HexNumber);
    var suffix = new char[26];
    for (var i = suffix.Length - 1; i >= 0; i--)
    {
        suffix[i] = alphabet[(int)(number & 31)];
        number >>= 5;
    }

    return new string(suffix);
}

Guid Decode(string suffix)
{
    if (suffix.Length != 26 || suffix[0] > '7')
    {
        throw new FormatException("Expected a 128-bit suffix");
    }

    UInt128 number = 0;
    foreach (var character in suffix)
    {
        var digit = alphabet.IndexOf(character);
        if (digit < 0)
        {
            throw new FormatException("Expected lowercase Crockford base32");
        }

        number = (number << 5) | (uint)digit;
    }

    return Guid.ParseExact(number.ToString("x32"), "N");
}

var vectors = JsonDocument.Parse(File.ReadAllText(args[0]));
var vectorCount = 0;
foreach (var vector in vectors.RootElement.EnumerateArray())
{
    var uuid = Guid.Parse(vector.GetProperty("uuid").GetString()!);
    var typeId = vector.GetProperty("typeid").GetString()!;
    var expected = typeId[(typeId.LastIndexOf('_') + 1)..];
    if (Encode(uuid) != expected || Decode(expected) != uuid)
    {
        throw new Exception($"Specification vector failed: {vector.GetProperty("name")}");
    }

    vectorCount++;
}

const int sampleCount = 10000;
for (var i = 0; i < sampleCount; i++)
{
    var bytes = RandomNumberGenerator.GetBytes(16);
    var uuid = new Guid(bytes, bigEndian: true);
    if (Decode(Encode(uuid)) != uuid)
    {
        throw new Exception("Arbitrary 128-bit round trip failed");
    }
}

var endianExample = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
var defaultBytes = Convert.ToHexString(endianExample.ToByteArray()).ToLowerInvariant();
if (defaultBytes == endianExample.ToString("N"))
{
    throw new Exception("Expected the default Guid byte order to differ");
}

Console.WriteLine(
    $"PASS: {vectorCount} specification vectors; {sampleCount} arbitrary 128-bit round trips."
);
Console.WriteLine(
    $"Byte-order example: UUID text {endianExample:N}; default Guid bytes {defaultBytes}."
);
Console.WriteLine(
    "Scope: suffix encoding only; no ID library, prefix binding, JSON, routes, or database migration tested."
);
