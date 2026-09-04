using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;

namespace Server.Tools.MapExport;

/// <summary>
///     A minimal 8-bit truecolour PNG encoder.
///     <para>
///         Hand-rolled on purpose. The repo has no imaging dependency at all, and the obvious
///         candidate - <c>System.Drawing.Common</c> - is Windows-only from .NET 7, which the
///         shard's Linux target rules out. A PNG is a signature, three chunks and a zlib stream;
///         adding SkiaSharp or ImageSharp to carry that is a poor trade.
///     </para>
///     <para>
///         Both pieces it needs are already in the box: <c>ZLibStream</c> from
///         <c>System.IO.Compression</c>, and <c>Crc32</c> from <c>System.IO.Hashing</c>, which
///         <c>Server.csproj</c> already references.
///     </para>
/// </summary>
internal static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int BytesPerPixel = 3;

    /// <summary>Writes <paramref name="rgb" /> (row-major, 3 bytes per pixel) as a PNG.</summary>
    public static void Write(string path, byte[] rgb, int width, int height)
    {
        using var file = File.Create(path);

        file.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type 2 = truecolour RGB
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace

        WriteChunk(file, "IHDR"u8, header);
        WriteChunk(file, "IDAT"u8, Compress(rgb, width, height));
        WriteChunk(file, "IEND"u8, default);
    }

    /// <summary>
    ///     Filters each scanline with Sub (subtract the pixel to the left) before deflating. Radar
    ///     maps are long horizontal runs of one colour, which Sub turns into runs of zero - much
    ///     cheaper for deflate than the raw bytes.
    /// </summary>
    private static byte[] Compress(byte[] rgb, int width, int height)
    {
        var stride = width * BytesPerPixel;
        var line = new byte[stride + 1];

        using var output = new MemoryStream(rgb.Length / 4);

        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            for (var y = 0; y < height; y++)
            {
                line[0] = 1; // filter: Sub

                var row = y * stride;

                for (var i = 0; i < stride; i++)
                {
                    var left = i >= BytesPerPixel ? rgb[row + i - BytesPerPixel] : 0;

                    line[i + 1] = (byte)(rgb[row + i] - left);
                }

                deflate.Write(line, 0, line.Length);
            }
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        stream.Write(type);
        stream.Write(data);

        // The CRC covers the chunk type and its data, but not the length field.
        var crc = new Crc32();
        crc.Append(type);
        crc.Append(data);

        Span<byte> checksum = stackalloc byte[4];
        crc.GetCurrentHash(checksum);

        // Crc32.GetCurrentHash writes little-endian; PNG wants big-endian.
        checksum.Reverse();
        stream.Write(checksum);
    }
}
