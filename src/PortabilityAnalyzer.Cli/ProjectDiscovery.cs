using System.Text.RegularExpressions;

namespace PortabilityAnalyzer.Cli;

/// <summary>Ensamblado descubierto y su origen (propio del codigo vs paquete de terceros).</summary>
internal sealed record AssemblyRef(string Path, bool IsThirdParty);

/// <summary>Descubre los ensamblados a analizar y determina cuales son de terceros.</summary>
internal interface IProjectDiscovery
{
    IReadOnlyList<AssemblyRef> Discover(string solutionPath);
}

/// <summary>
/// Descubrimiento basado en la solucion: lee los <c>.csproj</c> referenciados por el <c>.sln</c>,
/// deriva los nombres de ensamblado propios (first-party) y clasifica como de terceros cualquier DLL
/// de las carpetas <c>bin</c> cuyo nombre no corresponda a un proyecto de la solucion (tipicamente
/// paquetes NuGet copiados a la salida). Asi el factor de incertidumbre de terceros se aplica solo
/// donde procede, en lugar de a todos los ensamblados por igual.
/// </summary>
internal sealed class SolutionProjectDiscovery : IProjectDiscovery
{
    // Project("{TypeGuid}") = "Nombre", "ruta\Proyecto.csproj", "{ProjectGuid}"
    private static readonly Regex ProjectLine = new(
        "^Project\\(\"\\{[0-9A-Fa-f-]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AssemblyNameElement = new(
        "<AssemblyName>\\s*([^<]+?)\\s*</AssemblyName>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<AssemblyRef> Discover(string solutionPath)
    {
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException($"No se encuentra la solucion: {solutionPath}", solutionPath);

        var solutionDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(solutionPath))!;

        // Directorios de los .csproj referenciados por el .sln y sus nombres de ensamblado.
        var firstPartyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectDirs = new List<string>();

        foreach (Match m in ProjectLine.Matches(File.ReadAllText(solutionPath)))
        {
            var relative = m.Groups[1].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar);
            if (!relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue; // carpetas de solucion u otros tipos de proyecto.

            var csprojPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, relative));
            projectDirs.Add(System.IO.Path.GetDirectoryName(csprojPath)!);
            firstPartyNames.Add(ResolveAssemblyName(csprojPath));
        }

        // Se escanean SOLO las carpetas bin de los proyectos referenciados (no todo el arbol de la
        // solucion): asi se ignoran salidas ajenas o copias sueltas que no forman parte del .sln.
        var assemblies = projectDirs
            .Select(dir => System.IO.Path.Combine(dir, "bin"))
            .Where(Directory.Exists)
            .SelectMany(AssemblyDiscovery.Discover)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return assemblies
            .Select(p => new AssemblyRef(
                p,
                IsThirdParty: !firstPartyNames.Contains(System.IO.Path.GetFileNameWithoutExtension(p))))
            .ToList();
    }

    /// <summary>Nombre de ensamblado de un proyecto (elemento AssemblyName o, en su defecto, el nombre
    /// del fichero .csproj).</summary>
    private static string ResolveAssemblyName(string csprojPath)
    {
        if (File.Exists(csprojPath))
        {
            var match = AssemblyNameElement.Match(File.ReadAllText(csprojPath));
            if (match.Success)
                return match.Groups[1].Value;
        }
        return System.IO.Path.GetFileNameWithoutExtension(csprojPath);
    }
}
