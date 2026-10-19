using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Media.V1;
using ZstdSharp;

namespace Relisten.RealityPipeline.Blobs;

/// How a blob is encoded in object storage. Storage-only: the logical blob (a feature blob's
/// RLFT bytes, a Reality message) is what hashes, sizes and readers see.
/// <c>"RLZ1" | u8 codec | zstd frame</c>
///   codec 1  zstd of the bytes (reality blobs)
///   codec 2  zstd of an RLFT feature blob whose payload sections are byte-shuffled: each
///            section is transposed into byte planes by its element size (a record's size for
///            DTYPE_RECORD), so the float exponents compress together. Lossless; measured
///            −28% on Cornell's 446 files against −14% for zstd alone.
public sealed class BlobCodec(int zstdLevel = BlobCodec.DefaultLevel)
{
    public const int DefaultLevel = 19;
    private const byte Zstd = 1;
    private const byte ShuffledFeatureZstd = 2;
    private static readonly byte[] Magic = "RLZ1"u8.ToArray();

    public byte[] EncodeReality(ReadOnlySpan<byte> message) => Frame(Zstd, message);

    public byte[] EncodeFeature(ReadOnlySpan<byte> rlft)
    {
        var parsed = Rlft.Parse(rlft);
        var shuffled = rlft.ToArray();
        foreach (var (start, length, size) in Regions(parsed))
            Transpose(
                rlft.Slice(start, length),
                shuffled.AsSpan(start, length),
                size,
                toPlanes: true
            );
        return Frame(ShuffledFeatureZstd, shuffled);
    }

    public static byte[] Decode(ReadOnlySpan<byte> stored)
    {
        if (stored.Length < 5 || !stored[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not an encoded blob (missing RLZ1)");
        var codec = stored[4];
        using var decompressor = new Decompressor();
        var bytes = decompressor.Unwrap(stored[5..]).ToArray();
        switch (codec)
        {
            case Zstd:
                return bytes;
            case ShuffledFeatureZstd:
                // The header is never shuffled, so it can be read before the payload is restored.
                var parsed = Rlft.Parse(bytes, verifyCrc: false);
                var restored = (byte[])bytes.Clone();
                foreach (var (start, length, size) in Regions(parsed))
                    Transpose(
                        bytes.AsSpan(start, length),
                        restored.AsSpan(start, length),
                        size,
                        toPlanes: false
                    );
                Rlft.Parse(restored); // crc32 covers the original bytes
                return restored;
            default:
                throw new InvalidDataException($"Unknown blob codec {codec}");
        }
    }

    private byte[] Frame(byte codec, ReadOnlySpan<byte> bytes)
    {
        using var compressor = new Compressor(zstdLevel);
        var compressed = compressor.Wrap(bytes);
        var framed = new byte[5 + compressed.Length];
        Magic.CopyTo(framed, 0);
        framed[4] = codec;
        compressed.CopyTo(framed.AsSpan(5));
        return framed;
    }

    private static IEnumerable<(int Start, int Length, int ElementSize)> Regions(Rlft.Parsed parsed)
    {
        foreach (var section in parsed.Header.Sections)
        {
            var size = ElementSize(section);
            var length = (int)section.Length;
            if (size > 1 && length % size == 0)
                yield return (parsed.PayloadStart + (int)section.Offset, length, size);
        }
    }

    private static int ElementSize(Section section) =>
        section.Dtype == DType.Record
            ? section.Fields.Sum(f => Size(f.Dtype))
            : Size(section.Dtype);

    private static int Size(DType dtype) =>
        dtype switch
        {
            DType.U8 => 1,
            DType.U16 => 2,
            DType.U32 or DType.F32 => 4,
            DType.I64 or DType.F64 => 8,
            _ => 0,
        };

    // n elements of `size` bytes ⇄ `size` planes of n bytes.
    private static void Transpose(ReadOnlySpan<byte> from, Span<byte> to, int size, bool toPlanes)
    {
        var n = from.Length / size;
        for (var i = 0; i < n; i++)
        for (var k = 0; k < size; k++)
        {
            if (toPlanes)
                to[k * n + i] = from[i * size + k];
            else
                to[i * size + k] = from[k * n + i];
        }
    }
}

/// The feature blob envelope (docs/formats.md §Feature blobs):
/// <c>"RLFT" | u32le header_len | FeatureBlobHeader | payload | u32le crc32</c>.
public static class Rlft
{
    public const uint FormatVersion = 1;
    private static readonly byte[] Magic = "RLFT"u8.ToArray();

    public sealed record Parsed(FeatureBlobHeader Header, int PayloadStart, int PayloadLength);

    public static Parsed Parse(ReadOnlySpan<byte> blob, bool verifyCrc = true)
    {
        if (blob.Length < 12 || !blob[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not a feature blob (missing RLFT)");
        if (verifyCrc)
        {
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]);
            if (System.IO.Hashing.Crc32.HashToUInt32(blob[..^4]) != crc)
                throw new InvalidDataException("Feature blob crc32 mismatch");
        }

        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);
        if (headerLength < 0 || 8 + headerLength > blob.Length - 4)
            throw new InvalidDataException("Feature blob header overruns the blob");
        var header = FeatureBlobHeader.Parser.ParseFrom(blob.Slice(8, headerLength));
        if (header.FormatVersion != FormatVersion)
            throw new InvalidDataException($"Unknown feature blob format {header.FormatVersion}");

        var payloadStart = 8 + headerLength;
        var payloadLength = blob.Length - 4 - payloadStart;
        foreach (var section in header.Sections)
        {
            if (section.Offset + section.Length > (ulong)payloadLength)
                throw new InvalidDataException(
                    $"Feature blob section '{section.Name}' overruns the payload"
                );
        }
        return new Parsed(header, payloadStart, payloadLength);
    }
}
