using System.Text.RegularExpressions;

namespace PortabilityAnalyzer.Cli;

/// <summary>Ensamblado descubierto y su origen (propio del codigo vs paquete de terceros).</summary>
internal sealed record AssemblyRef(string Path, bool IsThirdParty);

/// <summary>Descubre los ensamblados a analizar y determina cuales son de terceros.</summary>
internal interface IProjectDiscovery
{
    IReadOnlyList<AssemblyRef> Discover(string inputPath);
}

/// <summary>
/// Descubrimiento basado en MSBuild: acepta una <b>solucion</b> (<c>.sln</c>) o un <b>proyecto</b>
/// concreto (<c>.csproj</c>). Deriva los nombres de ensamblado propios (first-party) de los proyectos
/// implicados y clasifica como de terceros cualquier DLL de las carpetas <c>bin</c> cuyo nombre no
/// corresponda a un proyecto (tipicamente paquetes NuGet copiados a la salida). Solo escanea los
/// <c>bin</c> de los proyectos implicados (no todo el arbol), y deduplica por nombre de ensamblado
/// para que una misma DLL copiada en varios <c>bin</c> se analice una sola vez.
/// </summary>
internal sealed class ProjectDiscovery : IProjectDiscovery
{
    // Project("{TypeGuid}") = "Nombre", "ruta\Proyecto.csproj", "{ProjectGuid}"
    private static readonly Regex ProjectLine = new(
        "^Project\\(\"\\{[0-9A-Fa-f-]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AssemblyNameElement = new(
        "<AssemblyName>\\s*([^<]+?)\\s*</AssemblyName>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Devuelve true si la ruta apunta a una solucion o proyecto que este descubridor maneja.</summary>
    public static bool Handles(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AssemblyRef> Discover(string inputPath)
    {
        var fullPath = System.IO.Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"No se encuentra la solucion/proyecto: {inputPath}", inputPath);

        // Los .csproj a considerar: los de la solucion, o el propio proyecto si se paso un .csproj.
        var csprojPaths = fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? ResolveProjectsFromSolution(fullPath)
            : new[] { fullPath };

        var firstPartyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectDirs = new List<string>();
        foreach (var csproj in csprojPaths)
        {
            projectDirs.Add(System.IO.Path.GetDirectoryName(csproj)!);
            firstPartyNames.Add(ResolveAssemblyName(csproj));
        }

        // Se escanean SOLO las carpetas bin de los proyectos implicados (no todo el arbol) y se
        // deduplica por nombre de ensamblado: una misma DLL en varios bin cuenta una sola vez.
        var assemblies = projectDirs
            .Select(dir => System.IO.Path.Combine(dir, "bin"))
            .Where(Directory.Exists)
            .SelectMany(AssemblyDiscovery.Discover)
            .GroupBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        return assemblies
            .Select(p => new AssemblyRef(
                p,
                IsThirdParty: !firstPartyNames.Contains(System.IO.Path.GetFileNameWithoutExtension(p))))
            .ToList();
    }

    /// <summary>Devuelve (nombre de proyecto, carpeta del proyecto) para el analisis de codigo fuente.</summary>
    public IReadOnlyList<(string Name, string Dir)> GetProjects(string inputPath)
    {
        var fullPath = System.IO.Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath)) return Array.Empty<(string, string)>();

        var csprojPaths = fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? ResolveProjectsFromSolution(fullPath)
            : new[] { fullPath };

        return csprojPaths
            .Select(c => (Name: ResolveAssemblyName(c), Dir: System.IO.Path.GetDirectoryName(c)!))
            .ToList();
    }

    /// <summary>Rutas absolutas de los .csproj referenciados por un .sln.</summary>
    private static IReadOnlyList<string> ResolveProjectsFromSolution(string solutionPath)
    {
        var solutionDir = System.IO.Path.GetDirectoryName(solutionPath)!;
        var projects = new List<string>();
        foreach (Match m in ProjectLine.Matches(File.ReadAllText(solutionPath)))
        {
            var relative = m.Groups[1].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar);
            if (!relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue; // carpetas de solucion u otros tipos de proyecto.
            projects.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, relative)));
        }
        return projects;
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
