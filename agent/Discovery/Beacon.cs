using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SoporteLan.Agent.Capture;

namespace SoporteLan.Agent.Discovery;

/// <summary>
/// Anuncia el agente por UDP para que el visor lo liste sin escribir IPs.
/// No requiere configuración ni servidor central.
/// </summary>
internal sealed class Beacon : IDisposable
{
    private readonly AppOptions _options;
    private readonly string _fingerprint;
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);

    public Beacon(AppOptions options, string fingerprint)
    {
        _options = options;
        _fingerprint = fingerprint;
        _udp.EnableBroadcast = true;
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => LoopAsync(ct), ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = Encoding.UTF8.GetBytes(BuildJson());
                foreach (var address in BroadcastAddresses())
                {
                    try
                    {
                        await _udp.SendAsync(payload, new IPEndPoint(address, _options.DiscoveryPort), ct)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Una interfaz puede rechazar el broadcast; seguimos con las demás.
                    }
                }

                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Nunca debe tumbar el agente un fallo de red.
                try { await Task.Delay(5000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private string BuildJson()
    {
        var name = Escape(Environment.MachineName);
        var os = Escape(Environment.OSVersion.VersionString);
        var monitors = MonitorLayout.Enumerate().Length;

        return $"{{\"app\":\"soporte-lan\",\"v\":1,\"name\":\"{name}\"," +
               $"\"port\":{_options.Port},\"fp\":\"{_fingerprint}\",\"os\":\"{os}\"," +
               $"\"monitors\":{monitors}}}";
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (seen.Add(IPAddress.Broadcast.ToString()))
            yield return IPAddress.Broadcast;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var mask = unicast.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any)) continue;

                var ip = unicast.Address.GetAddressBytes();
                var maskBytes = mask.GetAddressBytes();
                var broadcast = new byte[4];

                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~maskBytes[i]);

                var address = new IPAddress(broadcast);
                if (seen.Add(address.ToString()))
                    yield return address;
            }
        }
    }

    public void Dispose() => _udp.Dispose();
}
