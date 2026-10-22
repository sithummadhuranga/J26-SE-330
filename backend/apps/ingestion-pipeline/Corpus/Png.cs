// A minimal PNG writer and reader: 8-bit RGB or palette, filter 0 on every row, one IDAT, and an iCCP chunk
// with the source ICC profile. Kept minimal so it can be checked by reading: the samples go in unchanged.

using System.IO.Compression;
using System.Text;

namespace IngestionPipeline.Corpus;

public static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Write(int width, int height, byte[] samples, int channels, byte[]? palette, byte[] iccProfile)
    {
        using var o = new MemoryStream();
        o.Write(Signature);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, width);
        BigEndian(ihdr, 4, height);
        ihdr[8] = 8;                                   // bit depth
        ihdr[9] = (byte)(channels == 3 ? 2 : 3);       // colour type: 2 truecolour, 3 palette
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "iCCP", [.. Encoding.Latin1.GetBytes("ICC profile from source PDF"), 0, 0, .. Deflate(iccProfile)]);
        if (palette is not null) Chunk(o, "PLTE", palette);
        var stride = width * channels;
        var rows = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(samples, y * stride, rows, y * (stride + 1) + 1, stride);   // filter byte 0
        Chunk(o, "IDAT", Deflate(rows));
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    public static (int Width, int Height, int ColourType, byte[] Samples, byte[]? Palette, byte[] Icc) Read(byte[] png)
    {
        if (!png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        int w = 0, h = 0, type = 0;
        byte[]? plte = null, icc = null;
        using var idat = new MemoryStream();
        for (var at = 8; at < png.Length;)
        {
            var len = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var kind = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, len);
            if (Crc(png.AsSpan(at + 4, len + 4)) != (uint)((png[at + 8 + len] << 24) | (png[at + 9 + len] << 16) | (png[at + 10 + len] << 8) | png[at + 11 + len]))
                throw new InvalidDataException($"CRC mismatch in {kind}");
            switch (kind)
            {
                case "IHDR": w = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]; h = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]; type = data[9]; break;
                case "PLTE": plte = data.ToArray(); break;
                case "iCCP": icc = Inflate(data[(data.IndexOf((byte)0) + 2)..].ToArray()); break;
                case "IDAT": idat.Write(data); break;
            }
            at += 12 + len;
        }
        var rows = Inflate(idat.ToArray());
        var stride = w * (type == 2 ? 3 : 1);
        var samples = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            if (rows[y * (stride + 1)] != 0) throw new InvalidDataException("unexpected PNG filter");
            Buffer.BlockCopy(rows, y * (stride + 1) + 1, samples, y * stride, stride);
        }
        return (w, h, type, samples, plte, icc ?? []);
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        o.Write(head);
        o.Write(data);
        var crc = new byte[4];
        BigEndian(crc, 0, (int)Crc([.. head.AsSpan(4), .. data]));
        o.Write(crc);
    }

    private static void BigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    // CRC-32 as the PNG specification defines it (polynomial 0xEDB88320).
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
