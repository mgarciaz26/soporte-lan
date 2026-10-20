using System.Buffers.Binary;

namespace SoporteLan.Agent.Net;

internal enum MsgType : byte
{
    AuthRequest = 0x01,
    AuthResult = 0x02,
    ScreenInfo = 0x03,
    Frame = 0x04,
    Cursor = 0x05,

    MouseMove = 0x10,
    MouseButton = 0x11,
    MouseWheel = 0x12,
    Key = 0x13,
    Clipboard = 0x14,
    SelectMonitor = 0x15,

    Ping = 0x20,
    Pong = 0x21,
}

/// <summary>Framing binario común a agente, bridge y visor.</summary>
internal static class Wire
{
    public const int MaxPayload = 32 * 1024 * 1024;

    public static async Task WriteMessageAsync(
        Stream stream, MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(MsgType Type, byte[] Payload)?> ReadMessageAsync(
        Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
        if (length > MaxPayload)
            throw new InvalidDataException($"Payload de {length} bytes fuera de rango.");

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null;

        return ((MsgType)header[0], payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (chunk == 0) return false;
            read += chunk;
        }

        return true;
    }
}
