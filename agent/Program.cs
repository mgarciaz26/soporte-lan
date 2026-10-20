using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SoporteLan.Agent.Discovery;
using SoporteLan.Agent.Net;
using SoporteLan.Agent.Security;
using SoporteLan.Agent.Tray;

namespace SoporteLan.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            var attached = ConsoleHost.Attach();
            AppOptions.PrintHelp();

            if (!attached)
            {
                Console.WriteLine();
                Console.Write("Pulsa Enter para cerrar...");
                Console.ReadLine();
            }

            return 0;
        }

        var wantsConsole = args.Contains("--console");
        if (wantsConsole)
        {
            ConsoleHost.Attach();
            Log.EnableConsole();
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Log.Error("Excepción no controlada", eventArgs.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error("Tarea no observada", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        // Permite que un segundo doble clic muestre el PIN del agente ya abierto.
        using var showPinEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, "SoporteLAN.Agent.ShowPin");

        using var mutex = new Mutex(false, "SoporteLAN.Agent.SingleInstance");
        var isFirst = false;
        try
        {
            isFirst = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            isFirst = true;
        }

        if (!isFirst)
        {
            try
            {
                showPinEvent.Set();
            }
            catch
            {
                // Si la otra instancia aún no escucha, no pasa nada.
            }

            Log.Info("Ya hay una instancia del agente en ejecución; se muestra su PIN.");
            return 0;
        }

        try
        {
            var options = AppOptions.Parse(args);

            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

            var certificate = TlsIdentity.LoadOrCreate(out var fingerprint);
            var pin = new PinManager(options.PinLifetime, options.FixedPin);

            Log.Info($"Agente iniciado en {Environment.MachineName}. " +
                     $"Puerto {options.Port}, huella {Short(fingerprint)}.");

            using var cancellation = new CancellationTokenSource();

            if (wantsConsole)
            {
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
            }

            var server = new SessionServer(options, certificate, pin);
            server.Log += Log.Info;
            server.SessionStarted += remote => Log.Info($"Sesión iniciada con {remote}.");
            server.SessionEnded += () => Log.Info("Sesión finalizada.");

            using var beacon = new Beacon(options, fingerprint);
            beacon.Start(cancellation.Token);

            using var tray = new TrayIcon(
                () => new TraySnapshot(
                    Environment.MachineName,
                    PrimaryAddress(),
                    options.Port,
                    pin.Current,
                    pin.SecondsLeft,
                    server.HasActiveSession,
                    server.CurrentRemote),
                () => cancellation.Cancel());

            tray.Start();

            // Un segundo doble clic sobre el acceso muestra el PIN del agente abierto.
            _ = Task.Run(() =>
            {
                while (true)
                {
                    showPinEvent.WaitOne();
                    tray.ShowPin();
                }
            });

            var serverTask = server.RunAsync(cancellation.Token);

            // Al arrancar a mano mostramos la tarjeta; el autostart usa --quiet.
            if (!args.Contains("--quiet") || args.Contains("--preview"))
            {
                await Task.Delay(args.Contains("--preview") ? 700 : 1200).ConfigureAwait(false);
                tray.ShowPin();
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Salida normal desde el menú del icono o Ctrl+C.
            }

            tray.Dispose();
            await serverTask.ConfigureAwait(false);

            Log.Info("Agente detenido.");
            return 0;
        }
        finally
        {
            if (isFirst) mutex.ReleaseMutex();
        }
    }

    private static string Short(string fingerprint) =>
        fingerprint.Length <= 16 ? fingerprint : fingerprint[..16] + "…";

    private static string PrimaryAddress()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;
                return unicast.Address.ToString();
            }
        }

        return "127.0.0.1";
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
