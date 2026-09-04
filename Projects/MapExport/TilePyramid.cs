using System;
using System.Diagnostics;
using System.IO;

namespace Server.Tools.MapExport;

/// <summary>
///     Renders one facet to a pyramid of PNG tiles.
///     <para>
///         The scheme is a plain pixel pyramid, not a slippy/Mercator one: there is no projection
///         involved, the world is already a flat grid. At the deepest level one pixel is one game
///         tile; each level up halves both dimensions. Level 0 is the whole facet in a single
///         tile. Files land at <c>&lt;out&gt;/&lt;facet&gt;/&lt;z&gt;/&lt;x&gt;/&lt;y&gt;.png</c>,
///         with x the tile column and y the tile row from the top-left of the map.
///     </para>
///     <para>
///         The editor upscales past the deepest level with nearest-neighbour rather than the tool
///         rendering finer levels. Radar colour is one flat colour per game tile, so magnifying is
///         lossless - generating a 2x level would cost four times the disk for no extra detail.
///     </para>
/// </summary>
internal static class TilePyramid
{
    private const int BytesPerPixel = 3;

    /// <summary>Colour used outside the map, where a tile at the edge is not fully covered.</summary>
    private const byte Void = 0;

    public static void Render(Map map, RadarColors radar, string outputRoot, int tileSize)
    {
        var stopwatch = Stopwatch.StartNew();

        var image = RenderFacet(map, radar);
        var levels = LevelCount(map.Width, map.Height, tileSize);

        Console.WriteLine(
            $"  {map.Name}: {map.Width}x{map.Height}, {levels + 1} zoom levels (0-{levels}), rendered in {stopwatch.Elapsed.TotalSeconds:F1}s"
        );

        var written = 0;
        var facetRoot = Path.Combine(outputRoot, map.Name);

        for (var z = levels; z >= 0; z--)
        {
            written += WriteLevel(image, Path.Combine(facetRoot, z.ToString()), tileSize);

            if (z > 0)
            {
                image = Downsample(image);
            }
        }

        stopwatch.Stop();

        var bytes = DirectorySize(facetRoot);

        Console.WriteLine(
            $"  {map.Name}: {written} tiles, {bytes / 1024.0 / 1024.0:F1} MB, total {stopwatch.Elapsed.TotalSeconds:F1}s"
        );
    }

    /// <summary>
    ///     Number of times the facet has to be halved before it fits in a single tile. That count
    ///     is also the deepest zoom level, since level 0 is the single-tile overview.
    /// </summary>
    private static int LevelCount(int width, int height, int tileSize)
    {
        var levels = 0;

        while (width > tileSize || height > tileSize)
        {
            width = (width + 1) / 2;
            height = (height + 1) / 2;
            levels++;
        }

        return levels;
    }

    /// <summary>
    ///     Paints the whole facet at one pixel per game tile.
    ///     <para>
    ///         Iterates by 8x8 block rather than by tile: <c>GetLandBlock</c> and
    ///         <c>GetStaticBlock</c> are the real unit of work, and fetching them once per 64
    ///         pixels instead of once per pixel is the difference between minutes and seconds.
    ///     </para>
    /// </summary>
    private static Image RenderFacet(Map map, RadarColors radar)
    {
        var width = map.Width;
        var height = map.Height;
        var pixels = new byte[width * height * BytesPerPixel];
        var tiles = map.Tiles;

        var blockWidth = width >> 3;
        var blockHeight = height >> 3;

        for (var bx = 0; bx < blockWidth; bx++)
        {
            for (var by = 0; by < blockHeight; by++)
            {
                var land = tiles.GetLandBlock(bx, by);
                var statics = tiles.GetStaticBlock(bx, by);

                for (var ty = 0; ty < 8; ty++)
                {
                    var row = ((by << 3) + ty) * width;

                    for (var tx = 0; tx < 8; tx++)
                    {
                        var tile = land[(ty << 3) + tx];
                        var color = radar.Land(tile.ID);

                        // The client paints the topmost thing on the cell. Take the highest static
                        // standing at or above the ground; ">=" rather than ">" so that among
                        // statics at one height the last in file order wins, which is the one drawn
                        // on top.
                        var column = statics[tx][ty];
                        var top = tile.Z;
                        var topId = -1;

                        for (var i = 0; i < column.Length; i++)
                        {
                            if (column[i].Z >= top)
                            {
                                top = column[i].Z;
                                topId = column[i].ID;
                            }
                        }

                        if (topId >= 0)
                        {
                            color = radar.Static(topId);
                        }

                        var offset = (row + (bx << 3) + tx) * BytesPerPixel;

                        pixels[offset] = (byte)(color >> 16);
                        pixels[offset + 1] = (byte)(color >> 8);
                        pixels[offset + 2] = (byte)color;
                    }
                }
            }
        }

        return new Image(pixels, width, height);
    }

    private static int WriteLevel(Image image, string levelRoot, int tileSize)
    {
        var columns = (image.Width + tileSize - 1) / tileSize;
        var rows = (image.Height + tileSize - 1) / tileSize;
        var tile = new byte[tileSize * tileSize * BytesPerPixel];
        var written = 0;

        for (var tx = 0; tx < columns; tx++)
        {
            var columnRoot = Path.Combine(levelRoot, tx.ToString());
            Directory.CreateDirectory(columnRoot);

            for (var ty = 0; ty < rows; ty++)
            {
                CopyTile(image, tile, tx * tileSize, ty * tileSize, tileSize);

                PngWriter.Write(Path.Combine(columnRoot, $"{ty}.png"), tile, tileSize, tileSize);
                written++;
            }
        }

        return written;
    }

    /// <summary>
    ///     Copies one tile-sized window out of the image, padding with <see cref="Void" /> where the
    ///     window runs off the edge - the map is not a whole number of tiles wide in general.
    /// </summary>
    private static void CopyTile(Image image, byte[] tile, int x0, int y0, int tileSize)
    {
        Array.Clear(tile);

        var copyWidth = Math.Min(tileSize, image.Width - x0);
        var copyHeight = Math.Min(tileSize, image.Height - y0);

        for (var y = 0; y < copyHeight; y++)
        {
            var source = ((y0 + y) * image.Width + x0) * BytesPerPixel;
            var destination = y * tileSize * BytesPerPixel;

            Array.Copy(image.Pixels, source, tile, destination, copyWidth * BytesPerPixel);
        }
    }

    /// <summary>
    ///     Halves the image with a 2x2 box average. Averaging rather than dropping pixels matters:
    ///     a one-tile-wide road or river disappears entirely under nearest-neighbour, and those are
    ///     exactly the landmarks used to line a zone up at low zoom.
    /// </summary>
    private static Image Downsample(Image image)
    {
        var width = (image.Width + 1) / 2;
        var height = (image.Height + 1) / 2;
        var pixels = new byte[width * height * BytesPerPixel];

        for (var y = 0; y < height; y++)
        {
            var sourceY = y * 2;
            var secondY = Math.Min(sourceY + 1, image.Height - 1);

            for (var x = 0; x < width; x++)
            {
                var sourceX = x * 2;
                var secondX = Math.Min(sourceX + 1, image.Width - 1);

                var a = (sourceY * image.Width + sourceX) * BytesPerPixel;
                var b = (sourceY * image.Width + secondX) * BytesPerPixel;
                var c = (secondY * image.Width + sourceX) * BytesPerPixel;
                var d = (secondY * image.Width + secondX) * BytesPerPixel;

                var offset = (y * width + x) * BytesPerPixel;

                for (var channel = 0; channel < BytesPerPixel; channel++)
                {
                    var sum = image.Pixels[a + channel]
                              + image.Pixels[b + channel]
                              + image.Pixels[c + channel]
                              + image.Pixels[d + channel];

                    pixels[offset + channel] = (byte)(sum >> 2);
                }
            }
        }

        return new Image(pixels, width, height);
    }

    private static long DirectorySize(string path)
    {
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(path, "*.png", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private sealed class Image
    {
        public Image(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        public byte[] Pixels { get; }

        public int Width { get; }

        public int Height { get; }
    }
}
