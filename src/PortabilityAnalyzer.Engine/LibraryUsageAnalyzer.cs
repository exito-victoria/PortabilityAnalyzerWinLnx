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
/// Fuentes de dependencias declaradas (todas se inventarían):
///   - <c>PackageReference</c> de cada <c>.csproj</c> del proyecto.
///   - <c>PackageReference</c> heredados del <c>Directory.Build.props</c> más cercano (como MSBuild).
///   - <c>packages.config</c> (proyectos pre-SDK).
///   - Referencias directas <c>&lt;Reference&gt;</c> con <c>&lt;HintPath&gt;</c> (ensamblados externos no-NuGet,
///     p. ej. Oracle.DataAccess.dll o Interop.*), que se clasifican por el catálogo.
///
/// Señales usadas para el USO:
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

    // packages.config (proyectos pre-SDK): <package id="X" version="Y" [developmentDependency="true"] targetFramework=".."/>.
    private static readonly Regex PackageTag = new("<package\\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IdAttr = new("\\bid\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DevDependencyAttr = new("developmentDependency\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <Reference Include="Name, Version=..., Culture=..., PublicKeyToken=..."> con <HintPath>..</HintPath> (ensamblado externo).
    private static readonly Regex ReferenceBlock = new(
        "<Reference\\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</Reference>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HintPathChild = new("<HintPath>\\s*([^<]+?)\\s*</HintPath>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    /// Incluye también los paquetes de <c>Directory.Build.props</c> / <c>packages.config</c> y las referencias
    /// directas <c>&lt;Reference&gt;</c> con HintPath.
    /// </summary>
    public static IReadOnlyList<ReferencedLibrary> Build(IReadOnlyList<(string Name, string Dir)> projects)
    {
        var cacheRoot = ResolveNuGetCacheRoot();

        // Id -> agregado. Paquetes NuGet (csproj / props / packages.config) y, aparte, referencias directas.
        var pkgs = new Dictionary<string, PackageAggregate>(StringComparer.OrdinalIgnoreCase);
        var refs = new Dictionary<string, ReferenceAggregate>(StringComparer.OrdinalIgnoreCase);
        var cpmCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var propsCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);        // Directory.Build.props -> texto
        var projectUsings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // proyecto -> namespaces en using
        var projectRefAsm = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // proyecto -> ensamblados referenciados en IL

        foreach (var (name, dir) in projects)
        {
            projectUsings[name] = CollectUsings(dir);
            var cpm = ResolveCentralPackageVersions(dir, cpmCache);
            var outputRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // (a) PackageReference y <Reference> de cada .csproj del proyecto (no solo el primero); el IL de
            //     salida de cada csproj se une para validar el uso.
            foreach (var csproj in SafeAllCsproj(dir))
            {
                string text;
                try { text = File.ReadAllText(csproj); } catch { continue; }

                foreach (var a in ReadOutputAssemblyReferences(dir, ResolveOutputAssemblyName(text, csproj)))
                    outputRefs.Add(a);

                AddPackageReferences(text, name, cpm, pkgs);
                AddDirectReferences(text, name, refs);
            }

            // (b) PackageReference heredados del Directory.Build.props más cercano (como hace MSBuild).
            var propsText = ResolveDirectoryBuildProps(dir, propsCache);
            if (propsText is not null)
                AddPackageReferences(propsText, name, cpm, pkgs);

            // (c) packages.config (proyectos pre-SDK).
            AddPackagesConfig(dir, name, pkgs);

            projectRefAsm[name] = outputRefs;
        }

        // 2) Clasificar y calcular uso por paquete NuGet.
        var result = pkgs
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

        // 3) Referencias directas (<Reference> con HintPath): ensamblados externos no-NuGet. Se clasifican por el
        //    catálogo (por nombre de ensamblado) y su uso se valida contra el IL de la salida (el nombre del
        //    <Reference> ES el del ensamblado). Se omiten las que ya aparecen como paquete para no duplicar.
        foreach (var kv in refs)
        {
            var asmName = kv.Key;
            if (pkgs.ContainsKey(asmName)) continue;
            var ragg = kv.Value;
            var version = ragg.Versions.Count > 0 ? string.Join(", ", ragg.Versions.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) : null;
            var baseLib = LibraryReplacements.Classify(asmName, version);
            var (usage, evEs, evEn) = ComputeReferenceUsage(asmName, ragg, projectRefAsm);
            result.Add(baseLib with
            {
                Usage = usage,
                Projects = ragg.Projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                UsageEvidenceEs = evEs,
                UsageEvidenceEn = evEn
            });
        }

        return result
            .OrderBy(l => l.Package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class PackageAggregate
    {
        public SortedSet<string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool BuildOnly { get; set; }
    }

    private sealed class ReferenceAggregate
    {
        public SortedSet<string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Registra los <c>PackageReference</c> de un fragmento MSBuild (.csproj o Directory.Build.props)
    /// en el agregado de paquetes, resolviendo la versión (atributo, hijo o Central Package Management).</summary>
    private static void AddPackageReferences(
        string text, string projName, IReadOnlyDictionary<string, string> cpm, Dictionary<string, PackageAggregate> pkgs)
    {
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
            agg.Projects.Add(projName);
            if (IsBuildOnly(id, attrs, body)) agg.BuildOnly = true;
        }
    }

    /// <summary>Registra los paquetes de un <c>packages.config</c> (estilo pre-SDK) en el agregado. Un paquete
    /// con <c>developmentDependency="true"</c> se marca como solo-build.</summary>
    private static void AddPackagesConfig(string dir, string projName, Dictionary<string, PackageAggregate> pkgs)
    {
        string file;
        try
        {
            if (!Directory.Exists(dir)) return;
            file = Path.Combine(dir, "packages.config");
            if (!File.Exists(file)) return;
        }
        catch { return; }

        string text;
        try { text = File.ReadAllText(file); } catch { return; }

        foreach (Match m in PackageTag.Matches(text))
        {
            var tag = m.Value;
            var idm = IdAttr.Match(tag);
            if (!idm.Success) continue;
            var id = idm.Groups[1].Value.Trim();
            if (id.Length == 0) continue;

            var version = VersionAttr.Match(tag) is { Success: true } v ? v.Groups[1].Value.Trim() : null;

            if (!pkgs.TryGetValue(id, out var agg)) { agg = new PackageAggregate(); pkgs[id] = agg; }
            if (!string.IsNullOrWhiteSpace(version)) agg.Versions.Add(version!);
            agg.Projects.Add(projName);

            var dev = DevDependencyAttr.Match(tag);
            if ((dev.Success && dev.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                DevOnlyPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                agg.BuildOnly = true;
        }
    }

    /// <summary>Registra las referencias directas <c>&lt;Reference&gt;</c> que apuntan a un ensamblado externo
    /// (tienen <c>&lt;HintPath&gt;</c>). Las del framework/GAC (sin HintPath) no se inventarían. El Include puede
    /// ser un nombre completo ("Name, Version=1.2.3.4, Culture=..., PublicKeyToken=..."): se extrae nombre y versión.</summary>
    private static void AddDirectReferences(string text, string projName, Dictionary<string, ReferenceAggregate> refs)
    {
        foreach (Match m in ReferenceBlock.Matches(text))
        {
            var attrs = m.Groups["attrs"].Value;
            var body = m.Groups["body"].Success ? m.Groups["body"].Value : string.Empty;

            // Solo referencias a un ensamblado externo (con HintPath): las del framework/GAC no se inventarían.
            if (!HintPathChild.Match(body).Success) continue;

            var inc = IncludeAttr.Match(attrs);
            if (!inc.Success) continue;
            var raw = inc.Groups[1].Value.Trim();
            if (raw.Length == 0) continue;

            var parts = raw.Split(',');
            var name = parts[0].Trim();
            if (name.Length == 0) continue;

            string? version = null;
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    version = kv[1].Trim();
                    break;
                }
            }

            if (!refs.TryGetValue(name, out var agg)) { agg = new ReferenceAggregate(); refs[name] = agg; }
            if (!string.IsNullOrWhiteSpace(version)) agg.Versions.Add(version!);
            agg.Projects.Add(projName);
        }
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

    /// <summary>Decide el USO de una referencia directa (<c>&lt;Reference&gt;</c>): el nombre del Include ES el del
    /// ensamblado, así que basta comprobarlo contra el IL de la salida. Conservador ante falta de compilación.</summary>
    private static (LibraryUsage Usage, string? Es, string? En) ComputeReferenceUsage(
        string asmName, ReferenceAggregate agg, IReadOnlyDictionary<string, HashSet<string>> projectRefAsm)
    {
        foreach (var proj in agg.Projects)
            if (projectRefAsm.TryGetValue(proj, out var r) && r.Contains(asmName))
                return (LibraryUsage.Usada,
                    $"Referencia directa (<Reference>) usada en el IL de {proj} (ensamblado {asmName}).",
                    $"Direct reference (<Reference>) used in the IL of {proj} (assembly {asmName}).");

        var anyOutput = agg.Projects.Any(p => projectRefAsm.TryGetValue(p, out var r) && r.Count > 0);
        if (!anyOutput)
            return (LibraryUsage.NoVerificable,
                "Referencia directa (<Reference>): los proyectos que la declaran no están compilados. Compilar y reanalizar para verificar el uso.",
                "Direct reference (<Reference>): the declaring projects are not built. Build and re-run to verify usage.");

        return (LibraryUsage.CandidataARevisar,
            "Referencia directa (<Reference>) cuyo ensamblado no aparece en el IL de la salida compilada. Candidata a quitar; verificar reflexión o carga dinámica.",
            "Direct reference (<Reference>) whose assembly is absent from the compiled IL. Candidate to remove; verify reflection or dynamic loading.");
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
            if (!Directory.Exists(projectDir)) return result;
            // Solo ficheros ACTIVOS en la compilación (se ignoran los removidos del proyecto que siguen en disco).
            var active = ActiveCompileSet.Resolve(projectDir);
            foreach (var cs in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsIntermediate(cs)) continue;
                if (active is not null && !active.Contains(ActiveCompileSet.FullPath(cs))) continue;
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

    /// <summary>Texto del <c>Directory.Build.props</c> más cercano al proyecto (subiendo en el árbol, como hace
    /// MSBuild al importar el primero que encuentra). Null si no hay ninguno. Cacheado por fichero.</summary>
    private static string? ResolveDirectoryBuildProps(string projectDir, Dictionary<string, string?> cache)
    {
        try
        {
            var dir = new DirectoryInfo(projectDir);
            while (dir is not null)
            {
                var props = Path.Combine(dir.FullName, "Directory.Build.props");
                if (File.Exists(props))
                {
                    if (cache.TryGetValue(props, out var cached)) return cached;
                    string? text;
                    try { text = File.ReadAllText(props); } catch { text = null; }
                    cache[props] = text;
                    return text;
                }
                dir = dir.Parent;
            }
        }
        catch { /* mejor esfuerzo. */ }
        return null;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();

    private static string ResolveOutputAssemblyName(string csprojText, string csprojPath)
    {
        var m = AssemblyNameElement.Match(csprojText);
        return m.Success ? m.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(csprojPath);
    }

    private static IReadOnlyList<string> SafeAllCsproj(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj") : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
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
