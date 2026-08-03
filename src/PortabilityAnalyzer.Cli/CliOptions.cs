using System.Globalization;

namespace PortabilityAnalyzer.Cli;

/// <summary>
/// Opciones de linea de comandos. Parseo minimo a proposito: para produccion se recomienda
/// System.CommandLine (validacion, ayuda, autocompletado).
/// </summary>
internal sealed class CliOptions
{
    public required string InputPath { get; init; }
    public required string RulesPath { get; init; }
    public string? SchemaPath { get; init; }
    public string OutputPath { get; init; } = "portability-report.json";
    public string Format { get; init; } = "json";

    /// <summary>Si es true, se aplica <see cref="ThirdPartyFactor"/> como factor de incertidumbre.
    /// Por defecto false: los ensamblados se tratan como propios (first-party) hasta que exista
    /// una resolucion de proyecto/paquete que distinga cuales son de terceros.</summary>
    public bool AssumeThirdParty { get; init; }

    /// <summary>Factor de incertidumbre para ensamblados de terceros (solo se aplica si <see cref="AssumeThirdParty"/>).</summary>
    public double ThirdPartyFactor { get; init; } = 1.5;

    public static CliOptions? Parse(string[] args)
    {
        string? input = null, rules = null, schema = null, output = null, format = null;
        bool assumeThirdParty = false;
        double thirdPartyFactor = 1.5;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                case "--solution": input = Next(args, ref i); break;
                case "--rules": rules = Next(args, ref i); break;
                case "--schema": schema = Next(args, ref i); break;
                case "--output": output = Next(args, ref i); break;
                case "--format": format = Next(args, ref i); break;

                case "--assume-third-party":
                    assumeThirdParty = true;
                    break;

                case "--third-party-factor":
                    var raw = Next(args, ref i);
                    if (raw is null ||
                        !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out thirdPartyFactor) ||
                        thirdPartyFactor <= 0)
                        return null; // factor ausente o invalido: se muestra el uso.
                    assumeThirdParty = true; // pasar un factor implica quererlo aplicar.
                    break;

                // Se ignoran tokens desconocidos para no romper el parseo por desalineacion.
            }
        }

        if (input is null || rules is null) return null;

        return new CliOptions
        {
            InputPath = input,
            RulesPath = rules,
            SchemaPath = schema,
            OutputPath = output ?? "portability-report.json",
            Format = format ?? "json",
            AssumeThirdParty = assumeThirdParty,
            ThirdPartyFactor = thirdPartyFactor
        };
    }

    /// <summary>Devuelve el valor que sigue a una opcion y avanza el indice; null si no hay valor.</summary>
    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

    public static void PrintUsage() =>
        Console.WriteLine(
            "Uso: PortabilityAnalyzer.Cli --path <dir|dll> --rules <catalogo.json> " +
            "[--schema <schema.json>] [--output <salida>] [--format json|markdown] " +
            "[--assume-third-party] [--third-party-factor <n>]" + Environment.NewLine +
            "  --assume-third-party      Aplica un factor de incertidumbre (x1.5 por defecto) a todos los ensamblados." + Environment.NewLine +
            "  --third-party-factor <n>  Fija el factor (>0) e implica --assume-third-party.");
}
