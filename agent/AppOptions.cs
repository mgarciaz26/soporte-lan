namespace SoporteLan.Agent;

internal sealed record AppOptions
{
    public int Port { get; init; } = 50000;
    public int DiscoveryPort { get; init; } = 47820;
    public int Fps { get; init; } = 15;
    public int TileSize { get; init; } = 64;
    public string? FixedPin { get; init; }
    public TimeSpan PinLifetime { get; init; } = TimeSpan.FromSeconds(60);

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();

        for (var i = 0; i < args.Length; i++)
        {
            string? Take()
            {
                if (i + 1 >= args.Length) return null;
                return args[++i];
            }

            switch (args[i])
            {
                case "--port" when int.TryParse(Take(), out var port) && port is > 0 and < 65536:
                    options = options with { Port = port };
                    break;

                case "--discovery-port" when int.TryParse(Take(), out var discovery) && discovery is > 0 and < 65536:
                    options = options with { DiscoveryPort = discovery };
                    break;

                case "--fps" when int.TryParse(Take(), out var fps) && fps is > 0 and <= 60:
                    options = options with { Fps = fps };
                    break;

                case "--tile" when int.TryParse(Take(), out var tile) && tile is >= 16 and <= 256:
                    options = options with { TileSize = tile };
                    break;

                case "--pin":
                    var pin = Take();
                    if (pin is { Length: >= 4 and <= 12 } && pin.All(char.IsAsciiDigit))
                        options = options with { FixedPin = pin };
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            soporte-agent [opciones]

              --port <n>             Puerto TCP del agente (por defecto 50000)
              --discovery-port <n>   Puerto UDP de descubrimiento (por defecto 47820)
              --fps <n>              Fotogramas máximos por segundo (por defecto 15)
              --tile <n>             Tamaño de tile en píxeles (por defecto 64)
              --pin <digitos>        Fija el PIN (solo para pruebas)
              --quiet                Arranca sin mostrar la tarjeta del PIN
              --preview              Muestra la tarjeta del PIN al arrancar
              --console              Abre una consola con los registros (desarrollo)
              --help                 Muestra esta ayuda
            """);
    }
}
