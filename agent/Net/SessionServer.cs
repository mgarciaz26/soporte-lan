using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SoporteLan.Agent.Capture;
using SoporteLan.Agent.Input;
using SoporteLan.Agent.Security;

namespace SoporteLan.Agent.Net;

internal sealed class SessionServer
{
    private readonly AppOptions _options;
    private readonly X509Certificate2 _certificate;
    private readonly PinManager _pin;

    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    // -1 = escritorio completo. Lo escribe el bucle de entrada y lo aplica el de captura.
    private int _requestedMonitor = -1;

    public SessionServer(AppOptions options, X509Certificate2 certificate, PinManager pin)
    {
        _options = options;
        _certificate = certificate;
        _pin = pin;
    }

    public event Action<string>? Log;
    public event Action<string>? SessionStarted;
    public event Action? SessionEnded;

    public bool HasActiveSession { get; private set; }
    public string? CurrentRemote { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start(16);
        Log?.Invoke($"Escuchando en el puerto {_options.Port}.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Error al aceptar conexión: {ex.Message}");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "desconocido";

        using var tcp = client;
        tcp.NoDelay = true;

        try
        {
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(20));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, handshake.Token).ConfigureAwait(false);
            }

            var pin = await ReadPinAsync(ssl, ct).ConfigureAwait(false);
            if (pin is null) return;

            if (!AllowAttempt(remote))
            {
                await SendAsync(ssl, MsgType.AuthResult, Result(false, "demasiados intentos"), ct)
                    .ConfigureAwait(false);
                Log?.Invoke($"{remote}: bloqueado por intentos repetidos.");
                return;
            }

            if (!_pin.Verify(pin))
            {
                await SendAsync(ssl, MsgType.AuthResult, Result(false, "PIN incorrecto"), ct)
                    .ConfigureAwait(false);
                Log?.Invoke($"{remote}: PIN incorrecto.");
                return;
            }

            if (!_slot.Wait(0))
            {
                await SendAsync(ssl, MsgType.AuthResult, Result(false, "sesión ocupada"), ct)
                    .ConfigureAwait(false);
                Log?.Invoke($"{remote}: rechazado, ya hay una sesión activa.");
                return;
            }

            try
            {
                await RunSessionAsync(ssl, remote, ct).ConfigureAwait(false);
            }
            finally
            {
                _slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
        catch (AuthenticationException ex)
        {
            Log?.Invoke($"{remote}: fallo de TLS ({ex.Message}).");
        }
        catch (IOException)
        {
            // El visor cerró la conexión.
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{remote}: {ex.Message}");
        }
    }

    private async Task RunSessionAsync(SslStream ssl, string remote, CancellationToken outer)
    {
        using var capturer = new ScreenCapturer();
        Volatile.Write(ref _requestedMonitor, -1);

        var monitors = MonitorLayout.Enumerate();
        var bounds = MonitorLayout.Bounds(monitors);
        capturer.SetRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        if (!capturer.Refresh())
        {
            await SendAsync(ssl, MsgType.AuthResult, Result(false, "no se pudo capturar la pantalla"), outer)
                .ConfigureAwait(false);
            return;
        }

        await SendAsync(ssl, MsgType.AuthResult, Result(true, "ok"), outer).ConfigureAwait(false);

        Log?.Invoke($"{remote}: sesión iniciada ({monitors.Length} " +
                    $"{(monitors.Length == 1 ? "pantalla" : "pantallas")}).");
        HasActiveSession = true;
        CurrentRemote = remote;
        SessionStarted?.Invoke(remote);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
            var builder = new FrameBuilder(_options.TileSize);

            var captureLoop = CaptureLoopAsync(ssl, capturer, builder, monitors, linked.Token);
            var inputLoop = InputLoopAsync(ssl, linked.Token);

            await Task.WhenAny(captureLoop, inputLoop).ConfigureAwait(false);
            linked.Cancel();

            try { await Task.WhenAll(captureLoop, inputLoop).ConfigureAwait(false); }
            catch { /* cierre esperado */ }
        }
        finally
        {
            HasActiveSession = false;
            CurrentRemote = null;
            Log?.Invoke($"{remote}: sesión finalizada.");
            SessionEnded?.Invoke();
        }
    }

    private async Task CaptureLoopAsync(
        SslStream ssl,
        ScreenCapturer capturer,
        FrameBuilder builder,
        DisplayMonitor[] monitors,
        CancellationToken ct)
    {
        var frameDelay = TimeSpan.FromMilliseconds(Math.Max(1, 1000 / _options.Fps));
        var applied = int.MinValue;
        var lastScan = DateTime.MinValue;
        var lastCursorX = int.MinValue;
        var lastCursorY = int.MinValue;
        var cursorVisible = false;

        while (!ct.IsCancellationRequested)
        {
            var requested = Volatile.Read(ref _requestedMonitor);
            var now = DateTime.UtcNow;

            // Las pantallas pueden cambiar (conectar/desconectar un monitor o
            // cambiar la resolución): se revisa una vez por segundo.
            if (requested != applied || now - lastScan >= TimeSpan.FromSeconds(1))
            {
                var fresh = MonitorLayout.Enumerate();
                lastScan = now;

                if (requested != applied || !SameLayout(monitors, fresh))
                {
                    monitors = fresh;

                    if (requested < -1 || requested >= monitors.Length)
                    {
                        requested = -1;
                        Volatile.Write(ref _requestedMonitor, -1);
                    }

                    applied = requested;

                    var bounds = requested >= 0 ? monitors[requested] : MonitorLayout.Bounds(monitors);
                    capturer.SetRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);

                    if (!capturer.Refresh())
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Al cambiar de región hay que reenviar la pantalla completa.
                    builder.Reset();
                    await SendScreenInfoAsync(ssl, capturer, monitors, requested, ct).ConfigureAwait(false);
                }
            }

            if (!capturer.Refresh())
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var frame = capturer.Capture();
            if (frame.IsEmpty)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var payload = builder.Build(frame, capturer.Width, capturer.Height);
            if (payload is not null)
            {
                var buffer = payload.GetBuffer().AsMemory(0, (int)payload.Length);
                await SendAsync(ssl, MsgType.Frame, buffer, ct).ConfigureAwait(false);
            }

            if (InputInjector.TryGetCursor(out var cursorX, out var cursorY))
            {
                var inside =
                    cursorX >= capturer.OriginX && cursorX < capturer.OriginX + capturer.Width &&
                    cursorY >= capturer.OriginY && cursorY < capturer.OriginY + capturer.Height;

                if (cursorX != lastCursorX || cursorY != lastCursorY || inside != cursorVisible)
                {
                    lastCursorX = cursorX;
                    lastCursorY = cursorY;
                    cursorVisible = inside;

                    var cursor = new byte[5];
                    if (inside)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(cursor, (short)(cursorX - capturer.OriginX));
                        BinaryPrimitives.WriteInt16LittleEndian(cursor.AsSpan(2), (short)(cursorY - capturer.OriginY));
                    }

                    cursor[4] = inside ? (byte)1 : (byte)0;
                    await SendAsync(ssl, MsgType.Cursor, cursor, ct).ConfigureAwait(false);
                }
            }

            await Task.Delay(frameDelay, ct).ConfigureAwait(false);
        }
    }

    private async Task SendScreenInfoAsync(
        Stream ssl, ScreenCapturer capturer, DisplayMonitor[] monitors, int selected, CancellationToken ct)
    {
        var payload = new byte[12 + monitors.Length * 9];
        var span = payload.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)capturer.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)capturer.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)_options.TileSize);
        BinaryPrimitives.WriteInt16LittleEndian(span[6..], (short)capturer.OriginX);
        BinaryPrimitives.WriteInt16LittleEndian(span[8..], (short)capturer.OriginY);

        payload[10] = selected < 0 ? (byte)0xFF : (byte)selected;
        payload[11] = (byte)Math.Min(monitors.Length, 255);

        var offset = 12;
        foreach (var monitor in monitors)
        {
            payload[offset] = monitor.Primary ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt16LittleEndian(span[(offset + 1)..], (short)monitor.X);
            BinaryPrimitives.WriteInt16LittleEndian(span[(offset + 3)..], (short)monitor.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 5)..], (ushort)monitor.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 7)..], (ushort)monitor.Height);
            offset += 9;
        }

        await SendAsync(ssl, MsgType.ScreenInfo, payload, ct).ConfigureAwait(false);
    }

    private static bool SameLayout(DisplayMonitor[] current, DisplayMonitor[] fresh)
    {
        if (current.Length != fresh.Length) return false;

        for (var i = 0; i < current.Length; i++)
        {
            if (current[i] != fresh[i]) return false;
        }

        return true;
    }

    private async Task InputLoopAsync(SslStream ssl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await Wire.ReadMessageAsync(ssl, ct).ConfigureAwait(false);
            if (message is null) return;

            var (type, payload) = message.Value;

            switch (type)
            {
                case MsgType.MouseMove when payload.Length >= 8:
                    InputInjector.MouseMove(
                        BinaryPrimitives.ReadInt32LittleEndian(payload),
                        BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));
                    break;

                case MsgType.MouseButton when payload.Length >= 2:
                    InputInjector.MouseButton(payload[0], payload[1] != 0);
                    break;

                case MsgType.MouseWheel when payload.Length >= 2:
                    InputInjector.MouseWheel(BinaryPrimitives.ReadInt16LittleEndian(payload));
                    break;

                case MsgType.Key when payload.Length >= 3:
                    InputInjector.Key(
                        BinaryPrimitives.ReadUInt16LittleEndian(payload),
                        payload[2] != 0);
                    break;

                case MsgType.SelectMonitor when payload.Length >= 1:
                    Volatile.Write(ref _requestedMonitor, payload[0] == 0xFF ? -1 : payload[0]);
                    break;

                case MsgType.Ping:
                    await SendAsync(ssl, MsgType.Pong, ReadOnlyMemory<byte>.Empty, ct)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task<string?> ReadPinAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var message = await Wire.ReadMessageAsync(stream, timeout.Token).ConfigureAwait(false);
        if (message is null || message.Value.Type != MsgType.AuthRequest) return null;

        return Encoding.UTF8.GetString(message.Value.Payload);
    }

    private static byte[] Result(bool ok, string message)
    {
        var text = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + text.Length];
        payload[0] = ok ? (byte)1 : (byte)0;
        text.CopyTo(payload, 1);
        return payload;
    }

    private async Task SendAsync(Stream stream, MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Wire.WriteMessageAsync(stream, type, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private bool AllowAttempt(string remote)
    {
        var now = DateTime.UtcNow;
        var attempt = _attempts.GetOrAdd(remote, _ => new Attempt { Window = now });

        lock (attempt)
        {
            if (now - attempt.Window > TimeSpan.FromMinutes(1))
            {
                attempt.Window = now;
                attempt.Count = 0;
            }

            attempt.Count++;
            return attempt.Count <= 6;
        }
    }

    private sealed class Attempt
    {
        public int Count;
        public DateTime Window;
    }
}
