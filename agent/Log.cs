using System.Diagnostics;

namespace SoporteLan.Agent;

/// <summary>Registro en archivo, ya que el agente no tiene consola.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoporteLAN",
        "agent.log");

    private static bool _console;

    // UTF-8 con BOM solo al crear el archivo: así el Bloc de notas lo abre bien.
    private static readonly System.Text.UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: true);

    public static string Location => LogPath;

    public static void EnableConsole() => _console = true;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.Message}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}";

        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);

                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 1_000_000)
                {
                    var previous = LogPath + ".old";
                    File.Move(LogPath, previous, overwrite: true);
                }

                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding);
            }
            catch
            {
                // Nunca dejar que un fallo de disco tumbe el agente.
            }

            if (_console) Console.WriteLine(line);
        }
    }
}

/// <summary>Adjunta una consola cuando se arranca con --console (desarrollo).</summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    public static bool Attach()
    {
        var attached = AttachConsole(AttachParentProcess);
        if (!attached) AllocConsole();

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Sin consola disponible: el registro en archivo sigue funcionando.
        }

        return attached;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
}
