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

    /// <summary>Fichero JSON opcional con los roles de proyecto (API obligatoria, no modificables, divisibles por UI).</summary>
    public string? RolesPath { get; init; }
    public string OutputPath { get; init; } = "portability-report.json";

    /// <summary>Formatos de salida solicitados. Una sola ejecucion puede generar varios informes
    /// (p. ej. <c>--format word,markdown</c> o <c>--format all</c>).</summary>
    public IReadOnlyList<string> Formats { get; init; } = new[] { "json" };

    /// <summary>Si es true, se aplica <see cref="ThirdPartyFactor"/> como factor de incertidumbre.
    /// Por defecto false: los ensamblados se tratan como propios (first-party) hasta que exista
    /// una resolucion de proyecto/paquete que distinga cuales son de terceros.</summary>
    public bool AssumeThirdParty { get; init; }

    /// <summary>Factor de incertidumbre para ensamblados de terceros (solo se aplica si <see cref="AssumeThirdParty"/>).</summary>
    public double ThirdPartyFactor { get; init; } = 1.5;

    /// <summary>Fraccion del esfuerzo de desarrollo que se imputa al bucket de Pruebas y CI en ambos SO.</summary>
    public double TestingFactor { get; init; } = 0.25;

    public static CliOptions? Parse(string[] args)
    {
        string? input = null, rules = null, schema = null, output = null, format = null, rolesPath = null;
        bool assumeThirdParty = false;
        double thirdPartyFactor = 1.5;
        double testingFactor = 0.25;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                case "--solution": input = Next(args, ref i); break;
                case "--rules": rules = Next(args, ref i); break;
                case "--schema": schema = Next(args, ref i); break;
                case "--roles": rolesPath = Next(args, ref i); break;
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

                case "--testing-factor":
                    var rawT = Next(args, ref i);
                    if (rawT is null ||
                        !double.TryParse(rawT, NumberStyles.Float, CultureInfo.InvariantCulture, out testingFactor) ||
                        testingFactor < 0)
                        return null; // factor ausente o invalido: se muestra el uso.
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
            RolesPath = rolesPath,
            OutputPath = output ?? "portability-report.json",
            Formats = ParseFormats(format),
            AssumeThirdParty = assumeThirdParty,
            ThirdPartyFactor = thirdPartyFactor,
            TestingFactor = testingFactor
        };
    }

    /// <summary>Convierte el valor de <c>--format</c> (lista separada por comas, o <c>all</c>) en una
    /// lista de formatos validos y sin duplicados. Si no hay ninguno valido, usa json.</summary>
    private static IReadOnlyList<string> ParseFormats(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new[] { "json" };

        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.ToLowerInvariant());

        var result = new List<string>();
        foreach (var t in tokens)
        {
            if (t == "all")
            {
                foreach (var f in new[] { "json", "markdown", "word" })
                    if (!result.Contains(f)) result.Add(f);
            }
            else if ((t is "json" or "markdown" or "word") && !result.Contains(t))
            {
                result.Add(t);
            }
        }

        return result.Count > 0 ? result : new[] { "json" };
    }

    /// <summary>Devuelve el valor que sigue a una opcion y avanza el indice; null si no hay valor.</summary>
    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

    public static void PrintUsage() =>
        Console.WriteLine(
            "Uso: PortabilityAnalyzer.Cli --path <.sln|.csproj|dir|dll> --rules <catalogo.json> " +
            "[--schema <schema.json>] [--output <salida>] [--format <lista>] " +
            "[--assume-third-party] [--third-party-factor <n>]" + Environment.NewLine +
            "  --path                    Solucion (.sln), proyecto (.csproj), directorio con DLLs o una DLL/EXE." + Environment.NewLine +
            "  --roles <roles.json>      Roles de proyecto: API obligatoria, no modificables, divisibles por UI." + Environment.NewLine +
            "  --format                  json (por defecto) | markdown | word | all, o lista separada por comas" + Environment.NewLine +
            "                            (p. ej. 'word,markdown' -> una ejecucion, dos informes; la extension" + Environment.NewLine +
            "                            de cada uno se deriva de --output cuando se piden varios)." + Environment.NewLine +
            "  --assume-third-party      Aplica un factor de incertidumbre (x1.5 por defecto) a todos los ensamblados." + Environment.NewLine +
            "  --third-party-factor <n>  Fija el factor (>0) e implica --assume-third-party." + Environment.NewLine +
            "  --testing-factor <n>      Fraccion del esfuerzo de desarrollo imputada a Pruebas y CI (por defecto 0.25).");
}
