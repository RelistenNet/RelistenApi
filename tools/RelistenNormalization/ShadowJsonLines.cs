using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace Relisten.Normalization.Batch;

/// <summary>Bounds decoded lines before parsing retained metadata or provider wrappers.</summary>
internal static class ShadowJsonLines
{
    internal static async IAsyncEnumerable<string> ReadTextAsync(
        string path,
        int maximumLine,
        long maximumCharacters,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await using var file = File.OpenRead(path);
        await using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : file;
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        var buffer = new char[65536];
        var line = new StringBuilder();
        long characters = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            characters += read;
            if (characters > maximumCharacters)
            {
                throw new InvalidDataException(
                    "The decoded retained JSON Lines stream exceeds its character bound."
                );
            }
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, '\n', start, read - start);
                var end = newline < 0 ? read : newline;
                if (line.Length + end - start > maximumLine)
                {
                    throw new InvalidDataException(
                        "A retained JSON Lines row exceeds its character bound."
                    );
                }
                line.Append(buffer, start, end - start);
                if (newline < 0)
                {
                    break;
                }
                yield return line.ToString();
                line.Clear();
                start = newline + 1;
            }
        }
        if (line.Length != 0)
        {
            throw new InvalidDataException("Retained JSON Lines must retain a final newline.");
        }
    }
}
