using System.Text.RegularExpressions;
using PortabilityAnalyzer.Core;

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

    private static readonly Regex ProjectReferenceElement = new(
        "<ProjectReference\\s+[^>]*?Include\\s*=\\s*\"([^\"]+)\"",
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

    /// <summary>
    /// Resuelve el orden de compilacion de los proyectos por topologia de <c>ProjectReference</c>:
    /// un proyecto se compila despues de aquellos a los que referencia. Agrupa por niveles (los del mismo
    /// nivel no dependen entre si y podrian compilarse en paralelo) y detecta ciclos de referencia.
    /// </summary>
    public BuildOrder ResolveBuildOrder(string inputPath)
    {
        var fullPath = System.IO.Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath)) return BuildOrder.Empty;

        var csprojPaths = fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? ResolveProjectsFromSolution(fullPath)
            : new[] { fullPath };
        if (csprojPaths.Count == 0) return BuildOrder.Empty;

        // Nombre de proyecto por ruta .csproj normalizada.
        var nameByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csprojPaths)
            nameByPath[System.IO.Path.GetFullPath(c)] = ResolveAssemblyName(c);

        // Dependencias: proyecto -> proyectos que referencia DENTRO de la solucion (las externas se ignoran).
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csprojPaths)
        {
            var name = nameByPath[System.IO.Path.GetFullPath(c)];
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c))!;
            var set = deps.TryGetValue(name, out var existing) ? existing : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ProjectReferenceElement.Matches(File.ReadAllText(c)))
            {
                var relRef = m.Groups[1].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar);
                var refFull = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relRef));
                if (nameByPath.TryGetValue(refFull, out var refName) &&
                    !string.Equals(refName, name, StringComparison.OrdinalIgnoreCase))
                    set.Add(refName);
            }
            deps[name] = set;
        }

        return TopologicalBuildOrder(deps);
    }

    /// <summary>Ordena por niveles (Kahn) el grafo de dependencias; lo que quede en ciclo se reporta aparte.</summary>
    private static BuildOrder TopologicalBuildOrder(Dictionary<string, HashSet<string>> deps)
    {
        // Dependientes inversos: para cada d, quienes dependen de d.
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in deps.Keys) { indegree[n] = 0; dependents[n] = new List<string>(); }
        foreach (var (n, ds) in deps)
            foreach (var d in ds)
                if (deps.ContainsKey(d)) { indegree[n]++; dependents[d].Add(n); }

        var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        foreach (var n in queue) level[n] = 0;

        var ordered = new List<string>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            ordered.Add(n);
            foreach (var m in dependents[n])
            {
                level[m] = Math.Max(level.TryGetValue(m, out var lv) ? lv : 0, level[n] + 1);
                if (--indegree[m] == 0) queue.Enqueue(m);
            }
        }

        // Lo no ordenado (indegree > 0 residual) participa en un ciclo de referencias.
        var cycle = deps.Keys.Where(n => !ordered.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var steps = ordered
            .OrderBy(n => level[n]).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new BuildOrderStep(
                level[n], n,
                deps[n].Where(deps.ContainsKey).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        return new BuildOrder(steps, cycle.Count > 0, cycle);
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
