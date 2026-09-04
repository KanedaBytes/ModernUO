using System;
using System.IO;

namespace Server.Tools.MapExport;

/// <summary>
///     <c>radarcol.mul</c>: the colour the client paints each tile with on the world map. One
///     16-bit ARGB1555 entry per tile id - land ids first, then item ids offset by 0x4000.
///     <para>
///         Nothing in the server reads this file, so this is the one piece of .mul parsing the
///         export tool has to do itself.
///     </para>
///     <para>
///         The entry count is <b>not</b> the 65536 usually quoted. The installed file here is
///         163 768 bytes - 81 884 entries - because modern clients extended the item range. Read
///         whatever is on disk and bounds-check rather than assuming a size.
///     </para>
/// </summary>
internal sealed class RadarColors
{
    /// <summary>Where the item ids start. Land occupies 0x0000-0x3FFF.</summary>
    private const int ItemOffset = 0x4000;

    private readonly uint[] _colors;

    private RadarColors(uint[] colors) => _colors = colors;

    public int Count => _colors.Length;

    public static RadarColors Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var count = bytes.Length / 2;
        var colors = new uint[count];

        for (var i = 0; i < count; i++)
        {
            // Little-endian ARGB1555. The alpha bit is meaningless here - the client treats these
            // as opaque - so only the five-bit channels are read.
            var packed = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));

            var r = (packed >> 10) & 0x1F;
            var g = (packed >> 5) & 0x1F;
            var b = packed & 0x1F;

            // 5 bits to 8: replicate the high bits into the low ones so 31 maps to 255 rather
            // than 248, and the ramp stays even.
            colors[i] = (uint)((Expand(r) << 16) | (Expand(g) << 8) | Expand(b));
        }

        return new RadarColors(colors);
    }

    public uint Land(int id) => (uint)id < (uint)_colors.Length ? _colors[id] : 0;

    public uint Static(int id)
    {
        var index = ItemOffset + id;

        return (uint)index < (uint)_colors.Length ? _colors[index] : 0;
    }

    private static int Expand(int fiveBit) => (fiveBit << 3) | (fiveBit >> 2);
}
