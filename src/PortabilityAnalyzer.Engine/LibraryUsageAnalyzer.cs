using System.Text.RegularExpressions;
using Mono.Cecil;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Construye el inventario de paquetes NuGet de la solución y valida CONTRA EL CÓDIGO si cada uno se usa
/// realmente. El criterio es CONSERVADOR: un paquete solo se marca como candidato a quitar cuando hay
/// evidencia positiva de no-uso (su ensamblado no aparece en el IL de la salida compilada ni su namespace
/// en el código fuente); ante cualquier duda (proyecto sin compilar, paquete sin restaurar, meta-paquete,
/// uso por reflexión/DI) se deja como "no verificable" para no proponer una eliminación insegura.
///
/// Señales usadas:
///   1) IL de la salida compilada (bin) de los proyectos propios, leído con Mono.Cecil: qué ensamblados
///      referencia realmente el binario (AssemblyReferences).
///   2) directivas <c>using</c> del código fuente (.cs) de cada proyecto.
/// El puente paquete -> ensamblados/namespaces que aporta se obtiene de la caché global de NuGet
/// (%USERPROFILE%\.nuget\packages o $NUGET_PACKAGES), porque el Id del paquete no siempre coincide con el
/// nombre del ensamblado (p. ej. Oracle.ManagedDataAccess.Core aporta Oracle.ManagedDataAccess.dll).
/// </summary>
public static class LibraryUsageAnalyzer
{
    // <PackageReference Include="X" [Version="Y"] [PrivateAssets="all"] [IncludeAssets=..] [ExcludeAssets=..] .../>
    // o su forma con hijos. Capturamos el bloque completo (self-closing o con cierre) para inspeccionar atributos/hijos.
    private static readonly Regex PackageRefBlock = new(
        "<PackageReference\\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</PackageReference>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex IncludeAttr = new("Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionAttr = new("Version\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionChild = new("<Version>\\s*([^<]+?)\\s*</Version>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PrivateAssetsAttr = new("PrivateAssets\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PrivateAssetsChild = new("<PrivateAssets>\\s*([^<]+?)\\s*</PrivateAssets>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExcludeAssetsAttr = new("ExcludeAssets\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <PackageVersion Include="X" Version="Y" /> (Central Package Management: Directory.Packages.props).
    private static readonly Regex PackageVersionLine = new(
        "<PackageVersion\\b[^>]*?Include\\s*=\\s*\"([^\"]+)\"[^>]*?Version\\s*=\\s*\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AssemblyNameElement = new(
        "<AssemblyName>\\s*([^<]+?)\\s*</AssemblyName>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UsingDirective = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Paquetes de solo-desarrollo (analizadores, SDK de test, generadores): no se referencian en runtime por
    // diseño, así que su ausencia en el IL NO los convierte en candidatos a quitar.
    private static readonly string[] DevOnlyPrefixes =
    {
        "Microsoft.NET.Test.Sdk", "xunit.runner", "coverlet.", "NUnit3TestAdapter", "MSTest.TestAdapter",
        "StyleCop.", "Microsoft.CodeAnalysis.NetAnalyzers", "Microsoft.CodeAnalysis.Analyzers",
        "Roslynator.", "SonarAnalyzer.", "Meziantou.Analyzer", "AsyncFixer",
        "Microsoft.SourceLink.", "Nerdbank.GitVersioning", "GitVersion.", "Microsoft.VisualStudio.Threading.Analyzers"
    };

    // Preferencia de TFM al elegir la carpeta lib/ de un paquete en la caché (de más a menos afín a net8).
    private static readonly string[] TfmPreference =
    {
        "net8.0-windows", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0",
        "netcoreapp3.1", "netstandard1.6", "net48", "net472"
    };

    /// <summary>
    /// Inventario completo: por cada PackageReference distinto (Id) de los proyectos, su versión (resolviendo
    /// Central Package Management), su clasificación multiplataforma (catálogo) y su USO real validado contra el código.
    /// </summary>
    public static IReadOnlyList<ReferencedLibrary> Build(IReadOnlyList<(string Name, string Dir)> projects)
    {
        var cacheRoot = ResolveNuGetCacheRoot();

        // 1) Recolectar PackageReferences por proyecto (Id -> {versiones, proyectos, buildOnly}).
        var pkgs = new Dictionary<string, PackageAggregate>(StringComparer.OrdinalIgnoreCase);
        var cpmCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var projectUsings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // proyecto -> namespaces en using
        var projectRefAsm = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // proyecto -> ensamblados referenciados en IL

        foreach (var (name, dir) in projects)
        {
            var csproj = SafeFirstCsproj(dir);
            if (csproj is null) continue;
            string text;
            try { text = File.ReadAllText(csproj); } catch { continue; }

            var cpm = ResolveCentralPackageVersions(dir, cpmCache);
            projectUsings[name] = CollectUsings(dir);
            projectRefAsm[name] = ReadOutputAssemblyReferences(dir, ResolveOutputAssemblyName(text, csproj));

            foreach (Match m in PackageRefBlock.Matches(text))
            {
                var attrs = m.Groups["attrs"].Value;
                var body = m.Groups["body"].Success ? m.Groups["body"].Value : string.Empty;
                var inc = IncludeAttr.Match(attrs);
                if (!inc.Success) continue;
                var id = inc.Groups[1].Value.Trim();
                if (id.Length == 0) continue;

                var version = VersionAttr.Match(attrs) is { Success: true } va ? va.Groups[1].Value.Trim()
                    : VersionChild.Match(body) is { Success: true } vc ? vc.Groups[1].Value.Trim()
                    : cpm.TryGetValue(id, out var cv) ? cv
                    : null;

                if (!pkgs.TryGetValue(id, out var agg)) { agg = new PackageAggregate(); pkgs[id] = agg; }
                if (!string.IsNullOrWhiteSpace(version)) agg.Versions.Add(version!);
                agg.Projects.Add(name);
                if (IsBuildOnly(id, attrs, body)) agg.BuildOnly = true;
            }
        }

        // 2) Clasificar y calcular uso por paquete.
        return pkgs
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var id = kv.Key;
                var agg = kv.Value;
                var version = agg.Versions.Count > 0 ? string.Join(", ", agg.Versions.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) : null;
                var baseLib = LibraryReplacements.Classify(id, version);

                var (usage, evEs, evEn) = ComputeUsage(id, agg, cacheRoot, projectUsings, projectRefAsm);
                return baseLib with
                {
                    Usage = usage,
                    Projects = agg.Projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                    UsageEvidenceEs = evEs,
                    UsageEvidenceEn = evEn
                };
            })
            .ToList();
    }

    private sealed class PackageAggregate
    {
        public SortedSet<string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool BuildOnly { get; set; }
    }

    /// <summary>Decide el USO de un paquete cruzando la caché (ensamblados/namespaces que aporta) con el IL
    /// de la salida y los <c>using</c> del fuente. Conservador: sin evidencia clara -> NoVerificable.</summary>
    private static (LibraryUsage Usage, string? Es, string? En) ComputeUsage(
        string id, PackageAggregate agg, string? cacheRoot,
        IReadOnlyDictionary<string, HashSet<string>> projectUsings,
        IReadOnlyDictionary<string, HashSet<string>> projectRefAsm)
    {
        if (agg.BuildOnly)
            return (LibraryUsage.SoloBuild,
                "Dependencia de solo desarrollo (analizador / SDK de pruebas / PrivateAssets): no se referencia en tiempo de ejecución. No es eliminable por este motivo.",
                "Development-only dependency (analyzer / test SDK / PrivateAssets): not referenced at runtime. Not removable on that basis.");

        var (providedAsm, providedNs) = ResolvePackageAssets(id, agg.Versions, cacheRoot);

        // ¿Alguna salida compilada de un proyecto que declara el paquete referencia uno de sus ensamblados?
        foreach (var proj in agg.Projects)
            if (projectRefAsm.TryGetValue(proj, out var refs))
                foreach (var a in providedAsm)
                    if (refs.Contains(a))
                        return (LibraryUsage.Usada,
                            $"Referenciado en el IL de {proj} (ensamblado {a}).",
                            $"Referenced in the IL of {proj} (assembly {a}).");

        // ¿Algún using del fuente cae dentro de un namespace que aporta el paquete?
        foreach (var proj in agg.Projects)
            if (projectUsings.TryGetValue(proj, out var usings))
                foreach (var ns in providedNs)
                    if (usings.Any(u => NamespaceMatches(u, ns)))
                        return (LibraryUsage.Usada,
                            $"Namespace {ns} usado en el código fuente de {proj}.",
                            $"Namespace {ns} used in the source of {proj}.");

        // Sin evidencia de uso: distinguir "candidato a quitar" de "no verificable".
        var anyOutput = agg.Projects.Any(p => projectRefAsm.TryGetValue(p, out var r) && r.Count > 0);
        if (providedAsm.Count == 0 && providedNs.Count == 0)
            return (LibraryUsage.NoVerificable,
                "No se pudieron resolver los ensamblados del paquete (no restaurado, meta-paquete o FrameworkReference). Se asume en uso por prudencia.",
                "Could not resolve the package's assemblies (not restored, meta-package or FrameworkReference). Assumed in use to stay safe.");
        if (!anyOutput)
            return (LibraryUsage.NoVerificable,
                "Los proyectos que lo declaran no están compilados (sin salida en bin). Compilar y reanalizar para verificar el uso.",
                "The projects that declare it are not built (no output in bin). Build and re-run to verify usage.");

        return (LibraryUsage.CandidataARevisar,
            "Su ensamblado no aparece en el IL de la salida compilada ni su namespace en el código fuente. Candidato a quitar; verificar que no se use por reflexión o inyección de dependencias.",
            "Its assembly is not referenced by the compiled IL nor its namespace in the source. Candidate to remove; verify it is not used via reflection or dependency injection.");
    }

    /// <summary>True si el <c>using</c> identifica el paquete: exactamente su namespace, o un sub-namespace suyo.
    /// La dirección inversa (el using es un ancestro del namespace que aporta el paquete) solo se acepta si el
    /// using tiene al menos dos segmentos, para que un <c>using System;</c> o <c>using Microsoft;</c> no case
    /// con cualquier paquete System.*/Microsoft.* (raíces genéricas sin poder discriminante).</summary>
    private static bool NamespaceMatches(string usingNs, string providedNs) =>
        usingNs.Equals(providedNs, StringComparison.OrdinalIgnoreCase) ||
        usingNs.StartsWith(providedNs + ".", StringComparison.OrdinalIgnoreCase) ||
        (usingNs.Contains('.') && providedNs.StartsWith(usingNs + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ensamblados (nombre sin extensión) y namespaces públicos que aporta un paquete, leídos de su
    /// carpeta lib/ en la caché de NuGet. Vacío si no se puede resolver (no restaurado, meta-paquete, etc.).</summary>
    private static (HashSet<string> Asm, HashSet<string> Ns) ResolvePackageAssets(string id, IReadOnlyCollection<string> versions, string? cacheRoot)
    {
        var asm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cacheRoot is null) return (asm, ns);

        try
        {
            var pkgDir = Path.Combine(cacheRoot, id.ToLowerInvariant());
            if (!Directory.Exists(pkgDir)) return (asm, ns);

            var versionDir = PickVersionDir(pkgDir, versions);
            if (versionDir is null) return (asm, ns);

            var libDir = Path.Combine(versionDir, "lib");
            if (!Directory.Exists(libDir)) return (asm, ns);   // sin lib/ -> analizador/meta: no resoluble como runtime.

            var tfmDir = PickTfmDir(libDir);
            if (tfmDir is null) return (asm, ns);

            foreach (var dll in Directory.GetFiles(tfmDir, "*.dll"))
            {
                var stem = Path.GetFileNameWithoutExtension(dll);
                if (stem.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;
                asm.Add(stem);
                try
                {
                    using var def = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true, ReadSymbols = false });
                    // Los tipos definidos y los reexportados (facades de forwarding) aportan namespaces.
                    foreach (var n in def.MainModule.Types.Select(t => t.Namespace)
                                 .Concat(def.MainModule.ExportedTypes.Select(t => t.Namespace)))
                        if (!string.IsNullOrEmpty(n) && !IsGenericRootNamespace(n)) ns.Add(n);
                }
                catch { /* un dll ilegible no invalida el resto. */ }
            }
        }
        catch { /* degradar a vacío: se tratará como no verificable. */ }
        return (asm, ns);
    }

    /// <summary>Raíces de namespace demasiado genéricas para identificar un paquete concreto.</summary>
    private static bool IsGenericRootNamespace(string ns) =>
        ns.Equals("System", StringComparison.OrdinalIgnoreCase) ||
        ns.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static string? PickVersionDir(string pkgDir, IReadOnlyCollection<string> versions)
    {
        // Preferir una versión declarada; si no, la más alta presente en la caché.
        foreach (var v in versions)
        {
            var exact = Path.Combine(pkgDir, v.ToLowerInvariant());
            if (Directory.Exists(exact)) return exact;
        }
        return Directory.GetDirectories(pkgDir)
            .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
            .FirstOrDefault();
    }

    private static Version ParseVersion(string s)
    {
        var core = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(core, out var v) ? v : new Version(0, 0);
    }

    private static string? PickTfmDir(string libDir)
    {
        var subdirs = Directory.GetDirectories(libDir);
        if (subdirs.Length == 0) return Directory.GetFiles(libDir, "*.dll").Length > 0 ? libDir : null;
        foreach (var pref in TfmPreference)
        {
            var hit = subdirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), pref, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        // Cualquier TFM net*/netstandard* como último recurso.
        return subdirs.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>Nombres de los ensamblados referenciados por el IL de la salida compilada de un proyecto.</summary>
    private static HashSet<string> ReadOutputAssemblyReferences(string projectDir, string outputName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dll = FindOutputDll(projectDir, outputName);
            if (dll is null) return result;
            using var def = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true, ReadSymbols = false });
            foreach (var r in def.MainModule.AssemblyReferences)
                result.Add(r.Name);
        }
        catch { /* sin salida legible -> conjunto vacío (no verificable). */ }
        return result;
    }

    /// <summary>Localiza la DLL de salida de un proyecto en bin/, prefiriendo Debug y el TFM más afín a net8,
    /// y en caso de empate la más reciente.</summary>
    private static string? FindOutputDll(string projectDir, string outputName)
    {
        var bin = Path.Combine(projectDir, "bin");
        if (!Directory.Exists(bin)) return null;
        var candidates = Directory.EnumerateFiles(bin, outputName + ".dll", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}refint{Path.DirectorySeparatorChar}"))
            .ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderBy(p => p.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(TfmScore)
            .ThenByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; } })
            .First();

        static int TfmScore(string path)
        {
            for (int i = 0; i < TfmPreference.Length; i++)
                if (path.Contains($"{Path.DirectorySeparatorChar}{TfmPreference[i]}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    return i;
            return TfmPreference.Length;
        }
    }

    /// <summary>Namespaces que aparecen en directivas <c>using</c> del código fuente del proyecto (sin obj/bin).</summary>
    private static HashSet<string> CollectUsings(string projectDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var cs in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsIntermediate(cs)) continue;
                string text;
                try { text = File.ReadAllText(cs); } catch { continue; }
                foreach (Match m in UsingDirective.Matches(text))
                    result.Add(m.Groups[1].Value.Trim());
            }
        }
        catch { /* mejor esfuerzo. */ }
        return result;
    }

    private static bool IsIntermediate(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Un PackageReference es de solo build si lo marca PrivateAssets=all, ExcludeAssets excluye el
    /// runtime, o su Id está en la lista conocida de analizadores / SDK de pruebas.</summary>
    private static bool IsBuildOnly(string id, string attrs, string body)
    {
        if (DevOnlyPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;

        var privateAssets = PrivateAssetsAttr.Match(attrs) is { Success: true } pa ? pa.Groups[1].Value
            : PrivateAssetsChild.Match(body) is { Success: true } pc ? pc.Groups[1].Value : null;
        if (privateAssets is not null && privateAssets.Contains("all", StringComparison.OrdinalIgnoreCase)) return true;

        var exclude = ExcludeAssetsAttr.Match(attrs);
        if (exclude.Success)
        {
            var v = exclude.Groups[1].Value;
            if (v.Contains("all", StringComparison.OrdinalIgnoreCase) || v.Contains("runtime", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Versiones de Central Package Management (Directory.Packages.props) aplicables a un proyecto,
    /// buscando el fichero hacia arriba desde su carpeta. Cacheado por carpeta encontrada.</summary>
    private static IReadOnlyDictionary<string, string> ResolveCentralPackageVersions(
        string projectDir, Dictionary<string, IReadOnlyDictionary<string, string>> cache)
    {
        var dir = new DirectoryInfo(projectDir);
        while (dir is not null)
        {
            var props = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(props))
            {
                if (cache.TryGetValue(props, out var cached)) return cached;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (Match m in PackageVersionLine.Matches(File.ReadAllText(props)))
                        map[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
                }
                catch { /* props ilegible: sin versiones centrales. */ }
                cache[props] = map;
                return map;
            }
            dir = dir.Parent;
        }
        return EmptyMap;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();

    private static string ResolveOutputAssemblyName(string csprojText, string csprojPath)
    {
        var m = AssemblyNameElement.Match(csprojText);
        return m.Success ? m.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(csprojPath);
    }

    private static string? SafeFirstCsproj(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj").FirstOrDefault() : null; }
        catch { return null; }
    }

    private static string? ResolveNuGetCacheRoot()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var def = Path.Combine(home, ".nuget", "packages");
        return Directory.Exists(def) ? def : null;
    }
}
