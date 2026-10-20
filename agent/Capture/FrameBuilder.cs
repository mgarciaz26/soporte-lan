using System.Buffers.Binary;
using System.IO.Compression;

namespace SoporteLan.Agent.Capture;

/// <summary>
/// Convierte dos capturas consecutivas en un FRAME con solo los tiles que
/// cambiaron. Los tiles sólidos se codifican en 3 bytes y el resto se manda
/// en RGB y se comprime con zlib.
/// </summary>
internal sealed class FrameBuilder
{
    private readonly int _tile;
    private readonly MemoryStream _compressed = new(1 << 20);

    private byte[] _previous = [];
    private byte[] _raw = new byte[1 << 20];
    private int _width;
    private int _height;
    private uint _frameId;

    public FrameBuilder(int tileSize) => _tile = tileSize;

    /// <summary>Olvida el frame anterior: el siguiente envío será un keyframe completo.</summary>
    public void Reset()
    {
        _previous = [];
        _width = 0;
        _height = 0;
    }

    /// <summary>Devuelve el payload comprimido, o null si la pantalla no cambió.</summary>
    public MemoryStream? Build(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var reset = width != _width || height != _height || _previous.Length != width * height * 4;
        if (reset)
        {
            _previous = GC.AllocateUninitializedArray<byte>(width * height * 4);
            _width = width;
            _height = height;
        }

        var tilesX = (width + _tile - 1) / _tile;
        var tilesY = (height + _tile - 1) / _tile;
        var capacity = 6 + tilesX * tilesY * (9 + _tile * _tile * 3);
        if (_raw.Length < capacity) _raw = GC.AllocateUninitializedArray<byte>(capacity);

        var raw = _raw.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(raw, _frameId);

        var position = 6;
        var count = 0;

        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                var x = tx * _tile;
                var y = ty * _tile;
                var w = Math.Min(_tile, width - x);
                var h = Math.Min(_tile, height - y);

                if (!reset && !TileChanged(bgra, width, x, y, w, h)) continue;

                var solid = TrySolidColor(bgra, width, x, y, w, h, out var rgb);

                BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(position), (ushort)x);
                BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(position + 2), (ushort)y);
                BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(position + 4), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(position + 6), (ushort)h);
                raw[position + 8] = solid ? (byte)0 : (byte)1;
                position += 9;

                if (solid)
                {
                    raw[position++] = (byte)(rgb >> 16);
                    raw[position++] = (byte)(rgb >> 8);
                    raw[position++] = (byte)rgb;
                }
                else
                {
                    position = CopyRgb(bgra, width, x, y, w, h, raw, position);
                }

                count++;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(4), (ushort)count);

        if (count == 0) return null;

        _frameId++;
        bgra.CopyTo(_previous);

        _compressed.Position = 0;
        _compressed.SetLength(0);
        using (var zlib = new ZLibStream(_compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw[..position]);
        }

        _compressed.Position = 0;
        return _compressed;
    }

    private bool TileChanged(ReadOnlySpan<byte> bgra, int stride, int x, int y, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            var offset = ((y + row) * stride + x) * 4;
            if (!bgra.Slice(offset, w * 4).SequenceEqual(_previous.AsSpan(offset, w * 4)))
                return true;
        }

        return false;
    }

    private static bool TrySolidColor(
        ReadOnlySpan<byte> bgra, int stride, int x, int y, int w, int h, out uint rgb)
    {
        var index = (y * stride + x) * 4;
        var b = bgra[index];
        var g = bgra[index + 1];
        var r = bgra[index + 2];

        for (var row = 0; row < h; row++)
        {
            var line = bgra.Slice(((y + row) * stride + x) * 4, w * 4);
            for (var i = 0; i < line.Length; i += 4)
            {
                if (line[i] != b || line[i + 1] != g || line[i + 2] != r)
                {
                    rgb = 0;
                    return false;
                }
            }
        }

        rgb = (uint)((r << 16) | (g << 8) | b);
        return true;
    }

    private static int CopyRgb(
        ReadOnlySpan<byte> bgra, int stride, int x, int y, int w, int h, Span<byte> destination, int position)
    {
        for (var row = 0; row < h; row++)
        {
            var source = bgra.Slice(((y + row) * stride + x) * 4, w * 4);
            for (var column = 0; column < w; column++)
            {
                var offset = column * 4;
                destination[position++] = source[offset + 2];
                destination[position++] = source[offset + 1];
                destination[position++] = source[offset];
            }
        }

        return position;
    }
}
