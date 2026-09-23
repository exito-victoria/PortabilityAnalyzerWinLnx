using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Reescribe una solución COMPLETA a multiplataforma. A diferencia de <see cref="ProjectSplitter"/>
/// (scaffold de un proyecto), aquí se procesan TODOS los proyectos de la solución y se genera una
/// solución nueva hermana (sin tocar la original): cada proyecto se clasifica en
/// <b>Portable</b> (net8.0, sin dependencias de Windows), <b>Separable</b> (se divide en
/// <c>X.Core</c> net8.0 + <c>X.Windows</c> net8.0-windows) o <b>SoloWindows</b> (net8.0-windows), se
/// recablean todas las <c>ProjectReference</c> entre los proyectos generados y se regenera el
/// <c>.sln</c>. En los proyectos separados el <b>namespace se rebasa</b> al nuevo nombre
/// (<c>X</c> → <c>X.Core</c> / <c>X.Windows</c>) y se actualizan todas las referencias de la solución. El
/// núcleo portable queda <b>100% portable</b> y compilable en net8.0 (las dependencias de Windows se
/// resuelven con librerías/NuGets multiplataforma implementadas en el propio código, de forma transparente al
/// SO). La única excepción es la GUI WPF/WinForms, que no se migra y queda aislada en su proyecto net8.0-windows.
/// </summary>
public sealed class SolutionRewriter
{
    private static readonly Regex PackageRefLine =
        new("<PackageReference\\s+[^>]*?/>|<PackageReference\\s+[^>]*?>.*?</PackageReference>",
            RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex IncludeAttr =
        new("Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProjectReferenceInclude =
        new("<ProjectReference\\s+[^>]*?Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Paquetes NuGet exclusivos de Windows: no se añaden al núcleo portable (net8.0).</summary>
    private static readonly string[] WindowsOnlyPackageMarkers =
    {
        "System.Diagnostics.EventLog", "Microsoft.Win32.Registry", "System.Management",
        "System.DirectoryServices", "System.Security.Cryptography.ProtectedData",
        "System.Security.Principal.Windows", "System.ServiceProcess", "System.Speech",
        "System.Windows.Extensions", "Microsoft.Windows.Compatibility", "System.Drawing.Common"
    };

    // Cachés por fichero (una pasada de reescritura): evitan releer y reparsear el mismo .cs varias veces
    // (clasificación, expansión Windows, pase de seams y copia).
    private readonly Dictionary<string, string> _textCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SyntaxNode?> _rootCache = new(StringComparer.OrdinalIgnoreCase);

    private string ReadTextCached(string abs)
    {
        if (_textCache.TryGetValue(abs, out var t)) return t;
        t = File.ReadAllText(abs);
        _textCache[abs] = t;
        return t;
    }

    private SyntaxNode? RootCached(string abs)
    {
        if (_rootCache.TryGetValue(abs, out var r)) return r;
        try { r = CSharpSyntaxTree.ParseText(ReadTextCached(abs)).GetRoot(); }
        catch { r = null; }
        _rootCache[abs] = r;
        return r;
    }

    private enum Kind { Portable, Separable, WindowsOnly }

    private sealed class ProjInfo
    {
        public required string Name;
        public required string Dir;
        public required string CsprojPath;
        public IReadOnlyList<string> PackageLines = new List<string>();
        public bool UseWpf;
        public bool UseWinForms;
        public string? OutputType;
        public string? Tfm;                 // TargetFramework(s) original
        // Propiedades del PropertyGroup original que hay que conservar para no romper la compilacion.
        public string? ImplicitUsings;
        public string? Nullable;
        public string? LangVersion;
        public string? RootNamespace;
        public List<string> RefNames = new();            // referencias a otros proyectos de la solución
        public List<(string AbsCsproj, bool Portable)> ExternalRefs = new(); // referencias a proyectos externos
        public List<(string Abs, string Rel)> CodeFiles = new();     // todos los .cs (sin clasificar)
        public List<(string Abs, string Rel)> ContentFiles = new();  // no-.cs (xaml/resx/...)
        public HashSet<string> FindingFiles = new(StringComparer.OrdinalIgnoreCase); // .cs with any Windows finding
        public HashSet<string> GuiFindingFiles = new(StringComparer.OrdinalIgnoreCase); // .cs with a GUI (UI) finding
        // .cs that MUST go to the Windows side: they have a GUI finding OR a non-GUI Windows finding with no
        // cross-platform library replacement (Registry, EventLog, WMI, DPAPI, P/Invoke...). Files whose only
        // Windows finding is a clean library swap (e.g. Database -> managed driver) stay portable in .Core.
        public HashSet<string> WinDependentFiles = new(StringComparer.OrdinalIgnoreCase);
        public List<SourceFinding> Findings = new();     // this project's source findings (for marking/packages)
        public List<(string Abs, string Rel)> WinCode = new();
        public List<(string Abs, string Rel)> PortableCode = new();
        public List<(string Abs, string Rel)> WinContent = new();
        public List<(string Abs, string Rel)> PortableContent = new();
        public bool HasEntryPoint;
        public bool Excluded;             // en la lista de exclusión: se copia entero, sin separar
        public Kind Kind;

        // Identidades de salida (rellenadas tras clasificar).
        public string? CoreName;      // net8.0
        public string? WinName;       // net8.0-windows
        public string? PortableName;  // proyecto único portable
    }

    public RewriteResult Rewrite(string solutionName, IReadOnlyList<(string Name, string Dir)> projects,
        IReadOnlyList<SourceFinding> findings, string outputDir, IReadOnlyList<string>? excludeProjects = null,
        string? solutionDir = null)
    {
        var warnings = new List<string>();
        var excluded = new HashSet<string>(excludeProjects ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        // .gitignore a nivel de solución (se combina con el de cada proyecto): se leen los ficheros de
        // configuración PRIMERO para no incluir en la separación ficheros/carpetas excluidos.
        var slnIgnore = solutionDir is not null ? LoadGitignore(solutionDir) : (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<string>());

        // 1) Cargar info de cada proyecto y clasificar sus ficheros.
        var infos = new List<ProjInfo>();
        foreach (var (name, dir) in projects)
        {
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj is null) { warnings.Add($"Proyecto '{name}': no se encontró el .csproj en {dir}; se omite."); continue; }

            // SALVAGUARDA: nunca escribir dentro de una carpeta de proyecto original.
            if (PathConflictsWith(outputDir, dir))
                throw new InvalidOperationException(
                    $"La carpeta de salida '{outputDir}' colisiona con el proyecto original '{dir}'. " +
                    "Elige un --rewrite fuera de la carpeta de la solución para no arriesgar el código fuente.");

            var text = File.ReadAllText(csproj);
            var info = new ProjInfo
            {
                Name = name,
                Dir = dir,
                CsprojPath = csproj,
                PackageLines = PackageRefLine.Matches(text).Select(m => m.Value.Trim()).Distinct().ToList(),
                UseWpf = Regex.IsMatch(text, "<UseWPF>\\s*true", RegexOptions.IgnoreCase) || text.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase),
                UseWinForms = Regex.IsMatch(text, "<UseWindowsForms>\\s*true", RegexOptions.IgnoreCase) || text.Contains("System.Windows.Forms", StringComparison.OrdinalIgnoreCase),
                OutputType = Prop(text, "OutputType"),
                Tfm = Prop(text, "TargetFramework") ?? Prop(text, "TargetFrameworks"),
                ImplicitUsings = Prop(text, "ImplicitUsings"),
                Nullable = Prop(text, "Nullable"),
                LangVersion = Prop(text, "LangVersion"),
                RootNamespace = Prop(text, "RootNamespace")
            };

            // Exclusión robusta: casa por nombre de ensamblado O por nombre del fichero .csproj (ignora
            // mayúsculas). Así, aunque el <AssemblyName> difiera del nombre del proyecto, la lista funciona.
            var csprojName = Path.GetFileNameWithoutExtension(csproj);
            info.Excluded = excluded.Contains(name) || excluded.Contains(csprojName);

            // Ficheros del proyecto RESPETANDO su configuración real: se excluyen obj/bin, lo ignorado por
            // .gitignore (solución + proyecto) y lo que el .csproj no compila (<Compile Remove>,
            // <EnableDefaultCompileItems>false</...> + <Compile Include>). Lo excluido se OMITE por completo.
            var projIgnore = LoadGitignore(dir);
            var ignoreFolders = new HashSet<string>(slnIgnore.Item1, StringComparer.OrdinalIgnoreCase);
            foreach (var fld in projIgnore.Item1) ignoreFolders.Add(fld);
            var ignoreGlobs = slnIgnore.Item2.Concat(projIgnore.Item2).ToList();

            var enumerated = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !IsObjBin(p, dir) && !p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !IsVsJunk(p))
                .Select(p => (Abs: p, Rel: Path.GetRelativePath(dir, p)))
                .ToList();
            var ignoredCs = enumerated.Where(f => f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                              && IsGitIgnored(f.Rel, ignoreFolders, ignoreGlobs)).Select(f => f.Rel).ToList();
            var allFiles = enumerated.Where(f => !IsGitIgnored(f.Rel, ignoreFolders, ignoreGlobs)).ToList();

            var rawCs = allFiles.Where(f => f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
            info.CodeFiles = FilterByCompileItems(rawCs, text, out var removedCs);
            info.ContentFiles = allFiles.Where(f => !f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
            var omitted = ignoredCs.Concat(removedCs).ToList();
            if (omitted.Count > 0)
                warnings.Add($"'{name}': {omitted.Count} fichero(s) .cs excluidos de la separación por configuración del proyecto/.gitignore (no se copian): {string.Join(", ", omitted.Take(8))}{(omitted.Count > 8 ? "…" : string.Empty)}.");

            // Source findings for this project. Only the GUI (UI) findings drive the Windows side now:
            // the rest of the Windows APIs stay portable (library swap + compile-enabling package + [PORTAR] mark).
            info.Findings = findings.Where(f => string.Equals(f.Project, name, StringComparison.OrdinalIgnoreCase)).ToList();
            info.FindingFiles = info.Findings.Select(f => f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            info.GuiFindingFiles = info.Findings.Where(f => IsGuiCategory(f.Categoria)).Select(f => f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A file is Windows-bound if it has a GUI finding OR a Windows finding with no clean library swap.
            info.WinDependentFiles = info.Findings.Where(f => IsGuiCategory(f.Categoria) || !FindingIsPortableViaSwap(f))
                                                  .Select(f => f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in info.CodeFiles)
                if (IsEntryPoint(f.Abs)) { info.HasEntryPoint = true; break; }

            infos.Add(info);
        }

        // Avisar de entradas de --rewrite-exclude que no coinciden con NINGÚN proyecto (nombre mal escrito).
        foreach (var e in excluded)
            if (!infos.Any(i => string.Equals(i.Name, e, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Path.GetFileNameWithoutExtension(i.CsprojPath), e, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"--rewrite-exclude: '{e}' no coincide con ningún proyecto de la solución y se ignora. " +
                             $"Proyectos disponibles: {string.Join(", ", infos.Select(i => i.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");

        // 1b) PROPAGACIÓN TRANSITIVA de "Windows" por el grafo de tipos de TODA la solución (herencia + uso
        // de tipos, entre proyectos), sembrando desde ficheros con hallazgo y desde tipos base de WPF/WinForms.
        // Un fichero acaba en Windows si (transitivamente) necesita un tipo de Windows.
        var winTypes = ComputeWindowsTypes(infos);

        // 1c) Clasificar los ficheros de cada proyecto NO excluido usando el conjunto global de tipos Windows.
        foreach (var info in infos)
        {
            if (info.Excluded) continue; // los excluidos se copian enteros, sin separar
            var winSeed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in info.CodeFiles)
                if (FileTouchesWindows(f.Abs, f.Rel, info, winTypes)) winSeed.Add(f.Rel);
            var winSet = ExpandWindowsSet(info.CodeFiles, winSeed); // + clases parciales/herencia dentro del proyecto

            info.WinCode = info.CodeFiles.Where(f => winSet.Contains(f.Rel)).ToList();
            info.PortableCode = info.CodeFiles.Where(f => !winSet.Contains(f.Rel)).ToList();
            var (winContent, portContent) = ClassifyContent(info.ContentFiles, info.WinCode, info.PortableCode);
            info.WinContent = winContent;
            info.PortableContent = portContent;

            bool hasWin = info.WinCode.Count > 0 || info.UseWpf || info.UseWinForms;
            if (!hasWin) info.Kind = Kind.Portable;
            else if (info.PortableCode.Count > 0) info.Kind = Kind.Separable;
            else info.Kind = Kind.WindowsOnly;
        }

        // 2) Resolver referencias a proyectos de la solución (por nombre).
        var byNormalizedCsproj = infos.ToDictionary(i => Path.GetFullPath(i.CsprojPath), i => i.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            foreach (Match m in ProjectReferenceInclude.Matches(File.ReadAllText(info.CsprojPath)))
            {
                var rel = m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(info.Dir, rel));
                if (byNormalizedCsproj.TryGetValue(full, out var refName))
                {
                    if (!string.Equals(refName, info.Name, StringComparison.OrdinalIgnoreCase)) info.RefNames.Add(refName);
                }
                else if (File.Exists(full))
                {
                    // Referencia EXTERNA (proyecto fuera del conjunto analizado): se conserva apuntando a su
                    // .csproj original, con la ruta relativa recalculada desde cada proyecto generado.
                    var portable = IsPortableCsproj(full);
                    if (!info.ExternalRefs.Any(e => string.Equals(e.AbsCsproj, full, StringComparison.OrdinalIgnoreCase)))
                        info.ExternalRefs.Add((full, portable));
                    if (!portable)
                        warnings.Add($"'{info.Name}' referencia el proyecto EXTERNO '{Path.GetFileName(rel)}' (net…-windows o UI), fuera de la solución analizada: se conserva SOLO en el lado Windows. Si el núcleo lo necesita, extraer un seam.");
                }
                else
                {
                    warnings.Add($"'{info.Name}' referencia '{Path.GetFileName(rel)}', que no está en la solución ni se encuentra en disco ({rel}): la referencia no se ha podido conservar. Añádela a mano.");
                }
            }
        }

        // 3) Asignar identidades de salida.
        foreach (var info in infos)
        {
            switch (info.Kind)
            {
                case Kind.Portable: info.PortableName = info.Name; break;
                case Kind.WindowsOnly: info.WinName = info.Name; break;
                case Kind.Separable:
                    info.CoreName = info.Name + ".Core";
                    info.WinName = info.Name + ".Windows";
                    break;
            }
        }
        var infoByName = infos.ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase);

        // Referencia que debe usar un proyecto NÚCLEO/portable (net8.0) al referenciar 'origRef'.
        string? CoreSideRef(string origRef)
        {
            if (!infoByName.TryGetValue(origRef, out var r)) return null;
            return r.Kind switch
            {
                Kind.Portable => r.PortableName,
                Kind.Separable => r.CoreName,
                Kind.WindowsOnly => null,   // un núcleo portable no puede depender de un proyecto solo-Windows
                _ => null
            };
        }
        // Referencia que debe usar un proyecto WINDOWS (net8.0-windows) al referenciar 'origRef'.
        string? WinSideRef(string origRef)
        {
            if (!infoByName.TryGetValue(origRef, out var r)) return null;
            return r.Kind switch
            {
                Kind.Portable => r.PortableName,
                Kind.Separable => r.WinName,
                Kind.WindowsOnly => r.WinName,
                _ => null
            };
        }

        // 3.5) Run the seam pass for each separable project UP FRONT: its result is the final Core/Windows
        // split, which the namespace rebasing below depends on. (Also collected for the migration report.)
        var seamResults = new Dictionary<ProjInfo, SeamPassResult>();
        foreach (var info in infos)
            if (!info.Excluded && info.Kind == Kind.Separable)
                seamResults[info] = RunSeamPass(info, winTypes);

        // 3.6) Build the namespace rebaser. Only SEPARABLE projects are rebased: X.Core files get
        // 'namespace X.Core[.Sub]' and X.Windows files get 'namespace X.Windows[.Sub]'; every reference across
        // the generated solution (usings + fully-qualified names) is updated to the new namespaces.
        var splits = infos.Where(i => !i.Excluded && i.Kind == Kind.Separable)
            .Select(i => (OrigRoot: i.RootNamespace ?? i.Name, CoreRoot: i.CoreName!, WinRoot: i.WinName!)).ToList();
        var rebaser = BuildRebaser(infos, splits, seamResults);

        // 4) Preparar salida.
        RecreateDir(outputDir);
        var emitted = new List<(string Name, string RelCsproj)>();  // para el .sln
        var rewritten = new List<RewrittenProject>();
        var allSeams = new List<(string Project, string Concrete, string Interface, string Namespace)>();
        // Per entry-point output project: the seam-bearing Windows projects it can register via DI.
        var entryPointOutputs = new List<(string OutName, string OutDir, ProjInfo Info)>();

        foreach (var info in infos)
        {
            // Proyecto EXCLUIDO: se copia ENTERO (sin separar), preservando su TFM y OutputType. Sus
            // referencias a proyectos separados apuntan al lado .Windows (superset). Se corrige después.
            if (info.Excluded)
            {
                var outName = info.Name;
                var projDir = Path.Combine(outputDir, outName);
                // Not split -> own namespace preserved; references to split projects resolve to the Windows side.
                CopyCode(info.CodeFiles, info.Dir, projDir, outName, null, rebaser, NamespaceRebaser.Side.Windows, null);
                CopyContent(info.ContentFiles, projDir);
                if (info.HasEntryPoint) entryPointOutputs.Add((outName, projDir, info));
                var refs = info.RefNames.Select(WinSideRef).Where(x => x is not null).Select(x => x!).ToList();
                var relPaths = ProjRelPaths(refs).Concat(ExternalRelPaths(info.ExternalRefs, projDir, onlyPortable: false)).ToList();
                var tfm = string.IsNullOrWhiteSpace(info.Tfm) ? "net8.0-windows" : info.Tfm!;
                WriteCsproj(Path.Combine(projDir, outName + ".csproj"), tfm, info.PackageLines, info.UseWpf, info.UseWinForms,
                    info.OutputType, refs, relPaths, windowsOnlyFilter: false, source: info);
                emitted.Add((outName, $"{outName}\\{outName}.csproj"));
                rewritten.Add(new RewrittenProject(info.Name, "Excluido",
                    $"En la lista de exclusión: copiado entero sin separar (TFM {tfm})",
                    new[] { outName }, 0, info.CodeFiles.Count));
                continue;
            }

            switch (info.Kind)
            {
                case Kind.Portable:
                {
                    var outName = info.PortableName!;
                    var projDir = Path.Combine(outputDir, outName);
                    var portFiles = info.PortableCode.Concat(info.WinCode).ToList();
                    // Library-first: swap safe namespaces and mark the Windows-only APIs that remain.
                    var nsSwaps = NamespaceSwapsFor(info);
                    // Not split -> own namespace preserved; references to split projects resolve to the Core side.
                    CopyCode(portFiles, info.Dir, projDir, outName, BuildPortableOverrides(info, portFiles, nsSwaps, null),
                        rebaser, NamespaceRebaser.Side.Core, null);
                    CopyContent(info.PortableContent.Concat(info.WinContent), projDir);
                    if (UsesDataProtection(portFiles))
                        WriteGenerated(new[] { ("PortableDataProtection.cs", BuildDataProtectionShim()) }, projDir);
                    if (UsesPortableThreading(portFiles))
                        WriteGenerated(new[] { ("PortableThreading.cs", BuildPortableTimerShim()) }, projDir);
                    if (info.HasEntryPoint) entryPointOutputs.Add((outName, projDir, info));
                    var refs = info.RefNames.Select(CoreSideRef).Where(x => x is not null).Select(x => x!).ToList();
                    foreach (var rn in info.RefNames)
                        if (CoreSideRef(rn) is null && WinSideRef(rn) is not null)
                            warnings.Add($"'{outName}' (portable) referenciaba a '{rn}', que quedó solo-Windows: revisar (introducir un seam) o mantener este proyecto en net8.0-windows.");
                    var portableRelPaths = ProjRelPaths(refs).Concat(ExternalRelPaths(info.ExternalRefs, projDir, onlyPortable: false)).ToList();
                    WriteCsproj(Path.Combine(projDir, outName + ".csproj"), "net8.0", PortablePackageLines(info, portFiles), info.UseWpf, info.UseWinForms,
                        (IsExeType(info.OutputType) || info.HasEntryPoint) ? info.OutputType : null, refs, portableRelPaths, windowsOnlyFilter: false, source: info);
                    emitted.Add((outName, $"{outName}\\{outName}.csproj"));
                    var alreadyNet8 = info.Tfm is not null && !info.Tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase);
                    var portableReason = alreadyNet8
                        ? $"Ya estaba en {info.Tfm} sin dependencias de Windows: ya separado, copiado sin cambios"
                        : "Sin dependencias de Windows: retargeteado a net8.0";
                    rewritten.Add(new RewrittenProject(info.Name, "Portable", portableReason,
                        new[] { outName }, info.PortableCode.Count + info.WinCode.Count, 0));
                    break;
                }
                case Kind.WindowsOnly:
                {
                    var outName = info.WinName!;
                    var projDir = Path.Combine(outputDir, outName);
                    // Not split (whole project is Windows) -> own namespace preserved; refs to splits -> Windows side.
                    CopyCode(info.PortableCode.Concat(info.WinCode), info.Dir, projDir, outName, null, rebaser, NamespaceRebaser.Side.Windows, null);
                    CopyContent(info.PortableContent.Concat(info.WinContent), projDir);
                    if (info.HasEntryPoint) entryPointOutputs.Add((outName, projDir, info));
                    var refs = info.RefNames.Select(WinSideRef).Where(x => x is not null).Select(x => x!).ToList();
                    var winOnlyRelPaths = ProjRelPaths(refs).Concat(ExternalRelPaths(info.ExternalRefs, projDir, onlyPortable: false)).ToList();
                    WriteCsproj(Path.Combine(projDir, outName + ".csproj"), "net8.0-windows", info.PackageLines, info.UseWpf, info.UseWinForms,
                        info.OutputType, refs, winOnlyRelPaths, windowsOnlyFilter: false, source: info);
                    emitted.Add((outName, $"{outName}\\{outName}.csproj"));
                    rewritten.Add(new RewrittenProject(info.Name, "SoloWindows",
                        "Todo el proyecto depende de Windows (UI/entrada sin parte portable)",
                        new[] { outName }, 0, info.WinCode.Count));
                    break;
                }
                case Kind.Separable:
                {
                    var coreName = info.CoreName!;
                    var winName = info.WinName!;
                    var coreDir = Path.Combine(outputDir, coreName);
                    var winDir = Path.Combine(outputDir, winName);

                    // Seam pass already computed up front (the final Core/Windows split).
                    var seam = seamResults[info];
                    warnings.AddRange(seam.Warnings);
                    foreach (var s in seam.Seams) allSeams.Add((info.Name, s.Concrete, s.Interface, s.Namespace));
                    var origRoot = info.RootNamespace ?? info.Name;

                    // CORE (net8.0): portable files (with seam injection rewrites) + seam interfaces, all rebased
                    // to 'coreName'. Library-first: swap safe namespaces and mark the Windows-only APIs that remain.
                    var coreNsSwaps = NamespaceSwapsFor(info);
                    var coreOverrides = BuildPortableOverrides(info, seam.Portable, coreNsSwaps, seam.CoreOverrides);
                    CopyCode(seam.Portable, info.Dir, coreDir, coreName, coreOverrides, rebaser, NamespaceRebaser.Side.Core, coreName);
                    CopyContent(info.PortableContent, coreDir);
                    // Seam interfaces carry the concrete's original namespace -> rebase them to the Core side.
                    var coreExtra = rebaser is null
                        ? seam.CoreExtraFiles
                        : seam.CoreExtraFiles.Select(f => (f.Rel, rebaser.Rewrite(f.Content, NamespaceRebaser.Side.Core))).ToList();
                    WriteGenerated(coreExtra, coreDir);
                    if (UsesDataProtection(seam.Portable))
                        WriteGenerated(new[] { ("PortableDataProtection.cs", BuildDataProtectionShim()) }, coreDir);
                    if (UsesPortableThreading(seam.Portable))
                        WriteGenerated(new[] { ("PortableThreading.cs", BuildPortableTimerShim()) }, coreDir);
                    var coreRefs = info.RefNames.Select(CoreSideRef).Where(x => x is not null).Select(x => x!).ToList();
                    foreach (var rn in info.RefNames)
                        if (CoreSideRef(rn) is null)
                            warnings.Add($"El núcleo '{coreName}' referenciaba a '{rn}', que quedó solo-Windows: introducir un seam (interfaz) en el núcleo o mover el uso a '{winName}'.");
                    var coreRelPaths = ProjRelPaths(coreRefs).Concat(ExternalRelPaths(info.ExternalRefs, coreDir, onlyPortable: true)).ToList();
                    WriteCsproj(Path.Combine(coreDir, coreName + ".csproj"), "net8.0", PortablePackageLines(info, seam.Portable), false, false,
                        null, coreRefs, coreRelPaths, windowsOnlyFilter: false, source: info);
                    emitted.Add((coreName, $"{coreName}\\{coreName}.csproj"));

                    // WINDOWS (net8.0-windows): Windows files rebased to 'winName'. Because the project was split,
                    // a Windows file may reference (by simple name, same original namespace) a type that moved to
                    // the sibling .Core — or a seam interface that now lives there. Import every .Core namespace of
                    // THIS project so those references resolve.
                    var winPostUsings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                    if (rebaser is not null)
                    {
                        var coreNs = seam.Portable.SelectMany(f => DeclaredNamespaces(f.Abs))
                            .Concat(seam.Seams.Select(s => s.Namespace))
                            .Select(ns => rebaser.RebaseName(ns, NamespaceRebaser.Side.Core))
                            .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                        if (coreNs.Count > 0)
                            foreach (var f in seam.Win) winPostUsings[f.Rel] = coreNs;
                    }
                    CopyCode(seam.Win, info.Dir, winDir, winName, seam.WinOverrides, rebaser, NamespaceRebaser.Side.Windows, winName, winPostUsings);
                    CopyContent(info.WinContent, winDir);

                    // Si hubo seams, se GENERA el registro DI (composition root) con las implementaciones
                    // Windows (namespaces ya rebasados), para que el cableado quede hecho.
                    var winPkgs = info.PackageLines.ToList();
                    if (seam.Seams.Count > 0)
                    {
                        var regSeams = seam.Seams.Select(s => (s.Concrete, s.Interface,
                            CoreNs: rebaser?.RebaseName(s.Namespace, NamespaceRebaser.Side.Core) ?? s.Namespace,
                            WinNs: rebaser?.RebaseName(s.Namespace, NamespaceRebaser.Side.Windows) ?? s.Namespace)).ToList();
                        WriteGenerated(new[] { ("SeamRegistration.cs", BuildSeamRegistration(winName, regSeams)) }, winDir);
                        if (!winPkgs.Any(p => p.Contains("Microsoft.Extensions.DependencyInjection.Abstractions", StringComparison.OrdinalIgnoreCase)))
                            winPkgs.Add("<PackageReference Include=\"Microsoft.Extensions.DependencyInjection.Abstractions\" Version=\"8.0.2\" />");
                    }
                    if (info.HasEntryPoint) entryPointOutputs.Add((winName, winDir, info));

                    var winRefs = new List<string> { coreName };
                    winRefs.AddRange(info.RefNames.Select(WinSideRef).Where(x => x is not null).Select(x => x!));
                    var winRelPaths = new List<string> { $"..\\{coreName}\\{coreName}.csproj" };
                    winRelPaths.AddRange(ProjRelPaths(info.RefNames.Select(WinSideRef).Where(x => x is not null).Select(x => x!).ToList()));
                    winRelPaths.AddRange(ExternalRelPaths(info.ExternalRefs, winDir, onlyPortable: false));
                    WriteCsproj(Path.Combine(winDir, winName + ".csproj"), "net8.0-windows", winPkgs, info.UseWpf, info.UseWinForms,
                        (IsExeType(info.OutputType) || info.HasEntryPoint) ? (info.OutputType ?? "WinExe") : null, winRefs, winRelPaths, windowsOnlyFilter: false, source: info);
                    emitted.Add((winName, $"{winName}\\{winName}.csproj"));

                    rewritten.Add(new RewrittenProject(info.Name, "Separable",
                        $"{seam.Portable.Count} fichero(s) en el núcleo + {seam.Win.Count} con dependencias de Windows" +
                        (seam.Seams.Count > 0 ? $"; {seam.Seams.Count} seam(s) extraído(s)" : string.Empty),
                        new[] { coreName, winName }, seam.Portable.Count, seam.Win.Count));
                    break;
                }
            }
        }

        // 4a-bis) Generate the REAL abstraction layer (<Base>.Abstractions): the interfaces the report recommends
        // for the OS-specific categories present + a CROSS-PLATFORM default implementation of each + a DI extension.
        // Every generated project references it, and the CompositionRoot registers the portable defaults.
        string? abstractionsName = null;
        var relevantCats = findings.Select(f => f.Categoria).Where(AbstractionsGenerator.IsRelevantCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (relevantCats.Count > 0)
        {
            var baseName = solutionName.EndsWith("-multiplataforma", StringComparison.OrdinalIgnoreCase)
                ? solutionName[..^"-multiplataforma".Length] : solutionName;
            abstractionsName = SanitizeProjectName(baseName) + ".Abstractions";
            var absDir = Path.Combine(outputDir, abstractionsName);
            WriteGenerated(AbstractionsGenerator.Build(abstractionsName, relevantCats).Select(g => (g.Rel, g.Content)), absDir);
            // Reference the abstraction layer from every already-emitted project so the interfaces are available.
            foreach (var (_, relCsproj) in emitted)
                InjectProjectReference(Path.Combine(outputDir, relCsproj), $"..\\{abstractionsName}\\{abstractionsName}.csproj");
            emitted.Add((abstractionsName, $"{abstractionsName}\\{abstractionsName}.csproj"));
            rewritten.Add(new RewrittenProject("(capa de abstracción)", "Abstracciones",
                $"Generada con {relevantCats.Count} interfaz(es) portable(s) e implementación multiplataforma por defecto (DI): {string.Join(", ", relevantCats.OrderBy(c => c))}",
                new[] { abstractionsName }, 0, 0));
        }

        // 4b) DI wiring in generated code: in each entry-point project, generate a Composition Root that builds
        // a ServiceCollection, registers the abstraction layer + AddWindowsSeams() for every seam-bearing Windows
        // project it references and returns the provider; then invoke it from the entry point so it is verifiable.
        var seamWinProjects = seamResults.Where(kv => kv.Value.Seams.Count > 0)
            .Select(kv => kv.Key.WinName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (outName, outDir, info) in entryPointOutputs)
        {
            var reachable = new List<string>();
            if (info.Kind == Kind.Separable && seamResults.TryGetValue(info, out var ownSeam) && ownSeam.Seams.Count > 0)
                reachable.Add(info.WinName!);
            foreach (var rn in info.RefNames)
            {
                var w = WinSideRef(rn);
                if (w is not null && seamWinProjects.Contains(w)) reachable.Add(w);
            }
            reachable = reachable.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (reachable.Count == 0 && abstractionsName is null) continue; // nothing to register from this entry point

            WriteCompositionRoot(outDir, outName, reachable, abstractionsName);
            EnsureDependencyInjectionPackage(outDir, outName);
            if (!TryInvokeCompositionRoot(outDir, outName))
                warnings.Add($"'{outName}': se generó CompositionRoot.cs (AddWindowsSeams) pero no se pudo insertar " +
                             "la llamada en el punto de entrada automáticamente (p. ej. WPF con Main autogenerado). " +
                             "Invócalo desde tu arranque: var provider = CompositionRoot.Build(); (en WPF, en App.OnStartup).");
        }

        // 5) Generar el .sln, reconstruir el orden de compilación y el README de migración.
        var slnPath = Path.Combine(outputDir, solutionName + ".sln");
        WriteSolution(slnPath, emitted);
        var buildOrder = ComputeBuildOrder(outputDir, emitted);

        // 5b) Directory.Build.props: reubica obj/bin a una ruta CORTA (perfil de usuario) para evitar el límite
        // MAX_PATH (260) de Windows cuando la solución generada vive en una ruta profunda. Es la causa habitual
        // de "no se pueden cargar los proyectos" (VS falla al escribir en obj\Debug\net8.0-windows\...).
        WriteDirectoryBuildProps(outputDir, solutionName);

        // 5c) Aviso preventivo: ficheros FUENTE cuya ruta se acerca al límite (obj/bin ya van a ruta corta, pero
        // los .cs fuente viven en la carpeta de salida; si es muy profunda, VS puede fallar al cargarlos).
        WarnOnLongPaths(outputDir, warnings);

        WriteMigrationReadme(Path.Combine(outputDir, "MIGRACION.md"), solutionName, rewritten, warnings, allSeams, buildOrder, abstractionsName);

        return new RewriteResult
        {
            OutputDir = outputDir,
            SolutionFile = slnPath,
            Projects = rewritten,
            Warnings = warnings
        };
    }

    /// <summary>Writes a Directory.Build.props at the generated solution root that redirects obj/bin to a SHORT
    /// path under the user profile. The deep intermediate paths (<c>&lt;project&gt;\obj\Debug\net8.0-windows\…</c>,
    /// with long generated filenames) are the usual thing that blows past the Windows MAX_PATH (260) limit and
    /// makes Visual Studio fail to load the projects; keeping them short avoids it regardless of how deep the
    /// output solution lives. Delete this file to restore the default per-project obj/bin layout.</summary>
    private static void WriteDirectoryBuildProps(string outputDir, string solutionName)
    {
        var key = ShortHash(Path.GetFullPath(outputDir));
        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine();
        sb.AppendLine("  <!-- [Cross-platform rewrite] Keeps obj/bin paths SHORT to avoid the Windows MAX_PATH (260)");
        sb.AppendLine("       limit when this solution lives in a deep folder (otherwise VS may fail to load the");
        sb.AppendLine("       projects). Intermediate/output files go to a short path under the user profile instead of");
        sb.AppendLine("       <project>\\obj\\Debug\\net8.0-windows\\... . Delete this file to restore the default layout. -->");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <_RewriteBuildRoot>$(USERPROFILE)\\.pa-builds\\{key}</_RewriteBuildRoot>");
        sb.AppendLine("    <BaseIntermediateOutputPath>$(_RewriteBuildRoot)\\$(MSBuildProjectName)\\obj\\</BaseIntermediateOutputPath>");
        sb.AppendLine("    <BaseOutputPath>$(_RewriteBuildRoot)\\$(MSBuildProjectName)\\bin\\</BaseOutputPath>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("</Project>");
        File.WriteAllText(Path.Combine(outputDir, "Directory.Build.props"), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Short, stable 8-hex-char key derived from a string (used to give each generated solution its own
    /// short obj/bin build root without collisions).</summary>
    private static string ShortHash(string s)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h)[..8].ToLowerInvariant();
    }

    /// <summary>Adds a warning if any generated SOURCE file path is at/near the Windows MAX_PATH (260) limit, so
    /// the user can pick a shorter --rewrite root or enable long paths. (obj/bin are relocated by the generated
    /// Directory.Build.props, so only the source tree under the output folder is at risk here.)</summary>
    private static void WarnOnLongPaths(string outputDir, List<string> warnings)
    {
        const int limit = 260, warnAt = 250;
        var offenders = new List<(int Len, string Rel)>();
        int max = 0;
        foreach (var f in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
        {
            var len = Path.GetFullPath(f).Length;
            if (len > max) max = len;
            if (len >= warnAt) offenders.Add((len, Path.GetRelativePath(outputDir, f)));
        }
        if (offenders.Count == 0) return;
        var top = offenders.OrderByDescending(o => o.Len).Take(5).Select(o => $"{o.Len} car. — {o.Rel}");
        warnings.Add($"Rutas largas: {offenders.Count} fichero(s) generados alcanzan o superan {warnAt}/{limit} caracteres " +
                     $"(máximo {max}). Windows limita las rutas a {limit} y Visual Studio puede fallar al cargar los proyectos. " +
                     "Solución: elige un --rewrite MÁS CORTO (p. ej. C:\\out) o habilita rutas largas en Windows " +
                     "(HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled = 1 y reinicia Visual Studio). " +
                     $"Ejemplos: {string.Join(" | ", top)}");
    }

    // ---------------------------------------------------------------------------------------------
    // Copia de ficheros
    // ---------------------------------------------------------------------------------------------

    private void CopyCode(IEnumerable<(string Abs, string Rel)> files, string srcDir, string outDir, string outProject,
        IReadOnlyDictionary<string, string>? overrides,
        NamespaceRebaser? rebaser, NamespaceRebaser.Side side, string? rebasedRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? postRebaseUsings = null)
    {
        foreach (var f in files)
        {
            var target = Path.Combine(outDir, f.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var content = overrides is not null && overrides.TryGetValue(f.Rel, out var oc) ? oc : ReadTextCached(f.Abs);
            // Rebase namespaces/references (only touches split projects) exactly ONCE, from the original form.
            if (rebaser is not null) content = rebaser.Rewrite(content, side);
            if (postRebaseUsings is not null && postRebaseUsings.TryGetValue(f.Rel, out var extra))
                content = PrependUsings(content, extra);
            var header = rebasedRoot is null
                ? $"// [Cross-platform rewrite] Project {outProject}. Namespace preserved from the original."
                : $"// [Cross-platform rewrite] Project {outProject}. Namespace rebased to '{rebasedRoot}'.";
            File.WriteAllText(target, header + Environment.NewLine + content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    /// <summary>Prepends the given <c>using</c> directives (skipping any already present). Used to point a
    /// rebased Windows file at the seam interface namespace that now lives in its sibling <c>.Core</c>.</summary>
    private static string PrependUsings(string content, IReadOnlyList<string> namespaces)
    {
        var sb = new StringBuilder();
        foreach (var ns in namespaces)
            if (!Regex.IsMatch(content, $@"(?m)^\s*using\s+{Regex.Escape(ns)}\s*;"))
                sb.Append("using ").Append(ns).Append(';').Append(Environment.NewLine);
        return sb.Length == 0 ? content : sb.ToString() + content;
    }

    /// <summary>Namespaces declared by a .cs file (empty string for the global namespace), used to build the
    /// set of namespaces that will exist after rebasing.</summary>
    private IEnumerable<string> DeclaredNamespaces(string abs)
    {
        var root = RootCached(abs);
        if (root is null) return Array.Empty<string>();
        var list = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        if (list.Count == 0) list.Add(string.Empty);
        return list;
    }

    /// <summary>Builds the namespace rebaser for the SEPARABLE projects (X -> X.Core / X.Windows), collecting
    /// every new namespace that will exist so references can be resolved. Null if nothing is split.</summary>
    private NamespaceRebaser? BuildRebaser(List<ProjInfo> infos,
        List<(string OrigRoot, string CoreRoot, string WinRoot)> splits,
        IReadOnlyDictionary<ProjInfo, SeamPassResult> seamResults)
    {
        if (splits.Count == 0) return null;
        var pre = new NamespaceRebaser(splits, new HashSet<string>(StringComparer.Ordinal));
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            if (info.Excluded || info.Kind != Kind.Separable) continue;
            var seam = seamResults[info];
            foreach (var f in seam.Portable)
                foreach (var ns in DeclaredNamespaces(f.Abs)) existing.Add(pre.RebaseName(ns, NamespaceRebaser.Side.Core));
            foreach (var f in seam.Win)
                foreach (var ns in DeclaredNamespaces(f.Abs)) existing.Add(pre.RebaseName(ns, NamespaceRebaser.Side.Windows));
            // Seam interface files are emitted to Core under the concrete type's original namespace.
            foreach (var s in seam.Seams) existing.Add(pre.RebaseName(s.Namespace, NamespaceRebaser.Side.Core));
            existing.Add(info.CoreName!);
            existing.Add(info.WinName!);
        }
        return new NamespaceRebaser(splits, existing);
    }

    /// <summary>Generates the DI registration (composition root) that binds each seam interface (in .Core) to
    /// its Windows implementation (in .Windows), so the wiring is DONE: it only needs AddWindowsSeams() to be
    /// called from the app startup. The Composition Root generated in the entry-point project does exactly that.</summary>
    private static string BuildSeamRegistration(string winProject,
        IReadOnlyList<(string Concrete, string Interface, string CoreNs, string WinNs)> seams)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [Cross-platform rewrite] DI registration of the Windows implementations of the seams.");
        sb.AppendLine("// Call services.AddWindowsSeams() from the Composition Root (startup) of the Windows app.");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        sb.AppendLine($"namespace {winProject};");
        sb.AppendLine();
        sb.AppendLine("public static class SeamRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Registers the Windows implementations of the interfaces (seams) extracted to the core.</summary>");
        sb.AppendLine("    public static IServiceCollection AddWindowsSeams(this IServiceCollection services)");
        sb.AppendLine("    {");
        foreach (var s in seams)
        {
            var iface = string.IsNullOrEmpty(s.CoreNs) ? s.Interface : $"global::{s.CoreNs}.{s.Interface}";
            var impl = string.IsNullOrEmpty(s.WinNs) ? s.Concrete : $"global::{s.WinNs}.{s.Concrete}";
            sb.AppendLine($"        services.AddSingleton<{iface}, {impl}>();");
        }
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Generates the Composition Root of an entry-point project: builds a ServiceCollection, registers
    /// the Windows seam implementations of every reachable seam-bearing Windows project (AddWindowsSeams) and
    /// returns the provider. This is the DI wiring done in the GENERATED code (not just documented).</summary>
    private static void WriteCompositionRoot(string outDir, string nsName, IReadOnlyList<string> seamWinProjects, string? abstractionsName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [Cross-platform rewrite] Composition Root: builds the DI container, registers the portable");
        sb.AppendLine("// abstraction layer and the Windows implementations of the extracted seams. Call");
        sb.AppendLine("// CompositionRoot.Build() at application startup.");
        sb.AppendLine("using System;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        sb.AppendLine($"namespace {nsName};");
        sb.AppendLine();
        sb.AppendLine("public static class CompositionRoot");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Builds the DI container with the portable abstraction defaults and the Windows seams.</summary>");
        sb.AppendLine("    public static IServiceProvider Build()");
        sb.AppendLine("    {");
        sb.AppendLine("        var services = new ServiceCollection();");
        if (abstractionsName is not null)
            sb.AppendLine($"        global::{abstractionsName}.AbstractionsRegistration.AddPortableAbstractions(services);");
        foreach (var p in seamWinProjects)
            sb.AppendLine($"        global::{p}.SeamRegistration.AddWindowsSeams(services);");
        sb.AppendLine("        // TODO: register your application services and root type here.");
        sb.AppendLine("        return services.BuildServiceProvider();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outDir, "CompositionRoot.cs"), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Ensures the entry-point project references Microsoft.Extensions.DependencyInjection (needed for
    /// ServiceCollection/BuildServiceProvider in the generated Composition Root). Idempotent.</summary>
    private static void EnsureDependencyInjectionPackage(string outDir, string outName)
    {
        var path = Path.Combine(outDir, outName + ".csproj");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Distinguish from the ".Abstractions" package (which is a substring of the full package name).
        if (text.Contains("Include=\"Microsoft.Extensions.DependencyInjection\"", StringComparison.OrdinalIgnoreCase)) return;
        var block = "  <ItemGroup>" + Environment.NewLine +
                    "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"8.0.1\" />" + Environment.NewLine +
                    "  </ItemGroup>" + Environment.NewLine + Environment.NewLine;
        var idx = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        text = idx >= 0 ? text[..idx] + block + text[idx..] : text + Environment.NewLine + block;
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Removes characters that are not valid in a project/assembly name (whitespace) so the abstraction
    /// project name derived from the solution name is well-formed.</summary>
    private static string SanitizeProjectName(string name) =>
        new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>Adds a &lt;ProjectReference&gt; to a generated .csproj (idempotent), so every project can consume
    /// the generated abstraction layer.</summary>
    private static void InjectProjectReference(string csprojPath, string includeRel)
    {
        if (!File.Exists(csprojPath)) return;
        var text = File.ReadAllText(csprojPath);
        if (text.Contains($"Include=\"{includeRel}\"", StringComparison.OrdinalIgnoreCase)) return;
        var block = "  <ItemGroup>" + Environment.NewLine +
                    $"    <ProjectReference Include=\"{includeRel}\" />" + Environment.NewLine +
                    "  </ItemGroup>" + Environment.NewLine + Environment.NewLine;
        var idx = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        text = idx >= 0 ? text[..idx] + block + text[idx..] : text + Environment.NewLine + block;
        File.WriteAllText(csprojPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Best-effort: inserts a call to the generated CompositionRoot.Build() at the start of the entry
    /// point (classic static Main or top-level statements) so the DI wiring is exercised. Returns false if the
    /// entry point could not be located/edited safely (e.g. a WPF app whose Main is auto-generated).</summary>
    private bool TryInvokeCompositionRoot(string outDir, string nsName)
    {
        foreach (var file in Directory.EnumerateFiles(outDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("CompositionRoot.cs", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SeamRegistration.cs", StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(file); } catch { continue; }
            SyntaxNode root;
            try { root = CSharpSyntaxTree.ParseText(content).GetRoot(); } catch { continue; }

            var call = $"_ = global::{nsName}.CompositionRoot.Build();";
            // Classic static Main with a block body: insert as the first statement.
            var main = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Main"
                                  && m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && m.Body is not null);
            if (main is not null)
            {
                var stmt = SyntaxFactory.ParseStatement(call + Environment.NewLine);
                var newBody = main.Body!.WithStatements(main.Body.Statements.Insert(0, stmt));
                var newRoot = root.ReplaceNode(main.Body, newBody);
                File.WriteAllText(file, newRoot.ToFullString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return true;
            }
            // Top-level statements: insert before the first global statement.
            if (root is CompilationUnitSyntax cu && cu.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } firstGlobal)
            {
                var stmt = SyntaxFactory.GlobalStatement(SyntaxFactory.ParseStatement(call + Environment.NewLine));
                var newMembers = cu.Members.Insert(cu.Members.IndexOf(firstGlobal), stmt);
                File.WriteAllText(file, cu.WithMembers(newMembers).ToFullString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return true;
            }
        }
        return false;
    }

    /// <summary>Escribe ficheros .cs generados (p. ej. interfaces de seam) directamente en el proyecto.</summary>
    private static void WriteGenerated(IEnumerable<(string Rel, string Content)> files, string outDir)
    {
        foreach (var (rel, content) in files)
        {
            var target = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    private static void CopyContent(IEnumerable<(string Abs, string Rel)> files, string outDir)
    {
        foreach (var f in files)
        {
            var target = Path.Combine(outDir, f.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f.Abs, target, overwrite: true);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Pase de SEAMS: mantiene en el núcleo los ficheros portables que dependen de clases Windows,
    // extrayendo una interfaz e inyectándola por constructor (ver SeamWeaver).
    // ---------------------------------------------------------------------------------------------

    private sealed class SeamPassResult
    {
        public required List<(string Abs, string Rel)> Portable;
        public required List<(string Abs, string Rel)> Win;
        public Dictionary<string, string> CoreOverrides = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> WinOverrides = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Rel, string Content)> CoreExtraFiles = new();
        public List<(string Concrete, string Interface, string Namespace)> Seams = new();
        public List<string> Warnings = new();
    }

    private SeamPassResult RunSeamPass(ProjInfo info, HashSet<string> winTypes)
    {
        var res = new SeamPassResult { Portable = info.PortableCode.ToList(), Win = info.WinCode.ToList() };

        // Mapa: nombre de clase Windows -> fichero donde se declara.
        var winTypeToFile = new Dictionary<string, (string Abs, string Rel)>(StringComparer.Ordinal);
        foreach (var f in info.WinCode)
        {
            var root = RootCached(f.Abs);
            if (root is null) continue;
            foreach (var t in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                if (!winTypeToFile.ContainsKey(t.Identifier.Text)) winTypeToFile[t.Identifier.Text] = (f.Abs, f.Rel);
        }
        if (winTypeToFile.Count == 0) return res;

        var planByType = new Dictionary<string, SeamWeaver.SeamPlan?>(StringComparer.Ordinal);
        SeamWeaver.SeamPlan? PlanFor(string t)
        {
            if (planByType.TryGetValue(t, out var p)) return p;
            p = winTypeToFile.TryGetValue(t, out var wf) ? SeamWeaver.ExtractInterface(ReadTextCached(wf.Abs), t) : null;
            planByType[t] = p;
            return p;
        }

        // RECUPERACIÓN al núcleo: un fichero que quedó en Windows SOLO porque usa clases Windows del MISMO
        // proyecto de forma inyectable (campo privado `new T()`), se devuelve al núcleo extrayendo su interfaz
        // e inyectándola por constructor. Todo lo demás (hallazgo propio, herencia de base Windows, punto de
        // entrada, XAML, o uso de tipos Windows cross-project / no inyectables) se queda en Windows.
        foreach (var f in info.WinCode)
        {
            if (info.FindingFiles.Contains(f.Rel)) continue;                 // dependencia Windows propia
            if (f.Rel.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsEntryPoint(f.Abs)) continue;                                // punto de entrada
            var ft = FileTypesOf(f.Abs);
            if (ft.DerivesWinBase) continue;                                  // es un tipo Windows por herencia

            var content = ReadTextCached(f.Abs);
            // Tipos Windows (globales) que referencia este fichero, sin contar los que declara él mismo.
            var winRefs = ft.Referenced.Where(n => winTypes.Contains(n) && !ft.Declared.Contains(n)).Distinct().ToList();
            if (winRefs.Count == 0) continue; // Windows por otra razón; no se recupera
            // Solo se puede recuperar si TODAS sus referencias Windows son clases del MISMO proyecto (seamables).
            if (winRefs.Any(n => !winTypeToFile.ContainsKey(n))) continue;    // hay refs cross-project/no-clase

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            bool allExtractable = true;
            foreach (var t in winRefs) { var pl = PlanFor(t); if (pl is null) { allExtractable = false; break; } map[t] = pl.InterfaceName; }
            if (!allExtractable) continue;
            var rewritten = SeamWeaver.TryInjectConstructor(content, map);
            if (rewritten is null) continue; // no inyectable de forma segura: se queda en Windows

            // Recuperar el fichero al núcleo.
            res.Win.RemoveAll(x => string.Equals(x.Rel, f.Rel, StringComparison.OrdinalIgnoreCase));
            if (!res.Portable.Any(x => string.Equals(x.Rel, f.Rel, StringComparison.OrdinalIgnoreCase))) res.Portable.Add(f);
            res.CoreOverrides[f.Rel] = rewritten;
            foreach (var t in winRefs)
            {
                if (res.Seams.Any(s => s.Concrete == t)) continue;
                var pl = PlanFor(t)!;
                res.Seams.Add((t, pl.InterfaceName, pl.Namespace));
                res.CoreExtraFiles.Add(($"{pl.InterfaceName}.cs", pl.InterfaceSource));
                var wf = winTypeToFile[t];
                var baseContent = res.WinOverrides.TryGetValue(wf.Rel, out var oc) ? oc : ReadTextCached(wf.Abs);
                res.WinOverrides[wf.Rel] = SeamWeaver.AddBaseInterface(baseContent, t, pl.InterfaceName);
            }
        }
        return res;
    }

    // ---------------------------------------------------------------------------------------------
    // Generación de csproj / sln
    // ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> ProjRelPaths(IReadOnlyList<string> refNames) =>
        refNames.Select(n => $"..\\{n}\\{n}.csproj").ToList();

    /// <summary>Lee el valor de una propiedad simple del PropertyGroup del csproj (o null si no está).</summary>
    private static string? Prop(string csprojText, string name) =>
        Regex.Match(csprojText, $"<{name}>\\s*([^<]+?)\\s*</{name}>", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups[1].Value.Trim() : null;

    /// <summary>True si el OutputType corresponde a un ejecutable (Exe/WinExe).</summary>
    private static bool IsExeType(string? outputType) =>
        outputType is not null && outputType.Contains("Exe", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------
    // Library-first portability: swap Windows-only packages for their cross-platform equivalent, add the
    // packages that let the retargeted (net8.0) code compile, swap safe namespaces and mark the rest.
    // ---------------------------------------------------------------------------------------------

    /// <summary>net8.0 package that lets Windows-only BCL code of a given category still COMPILE (it runs on
    /// Windows and throws PlatformNotSupportedException on Linux until rewritten). Null if none is needed.</summary>
    private static (string Pkg, string Ver)? CompileEnablingPackage(string categoria) => categoria switch
    {
        "Registry" => ("Microsoft.Win32.Registry", "5.0.0"),
        "EventLog" => ("System.Diagnostics.EventLog", "8.0.0"),
        "WMI" => ("System.Management", "8.0.0"),
        "Identity" => ("System.Security.Principal.Windows", "5.0.0"),
        "PerformanceCounter" => ("System.Diagnostics.PerformanceCounter", "8.0.0"),
        "ServiceProcess" => ("System.ServiceProcess.ServiceController", "8.0.0"),
        _ => null
    };

    private static string IncludeNameOf(string packageLine)
    {
        var m = IncludeAttr.Match(packageLine);
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>Builds the PackageReference lines of a PORTABLE project: swaps Windows-only packages for their
    /// cross-platform replacement (per the curated catalog), keeps the rest, and ADDS the compile-enabling
    /// packages for the non-GUI Windows categories used by the portable files.</summary>
    private List<string> PortablePackageLines(ProjInfo info, IEnumerable<(string Abs, string Rel)> portableFiles)
    {
        var lines = new List<string>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in info.PackageLines)
        {
            var inc = IncludeNameOf(line);
            var e = inc.Length > 0 ? LibraryReplacements.Lookup(inc) : null;
            if (e is { Status: LibraryStatus.Reemplazar, Replacement: { } repl })
            {
                var ver = e.ReplacementVersion is null ? string.Empty : $" Version=\"{e.ReplacementVersion}\"";
                lines.Add($"<PackageReference Include=\"{repl}\"{ver} />");
                present.Add(repl);
            }
            else if (e is { Status: LibraryStatus.Revisar } || IsWindowsOnlyPackage(line))
            {
                // Windows-only package with no drop-in: the code that used it moved to .Windows (core stays
                // 100% portable), so the portable project drops the reference. It remains on the Windows side.
                continue;
            }
            else { lines.Add(line); if (inc.Length > 0) present.Add(inc); }
        }

        // Ensure a managed, cross-platform database driver for Database findings that stay portable in .Core
        // (only if the project doesn't already reference one). Pick the provider from the finding symbols.
        var portableRel = portableFiles.Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dbFindings = info.Findings.Where(f => portableRel.Contains(f.File)
            && string.Equals(f.Categoria, "Database", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dbFindings.Count > 0)
        {
            bool usesOracle = dbFindings.Any(f => f.Symbol.Contains("Oracle", StringComparison.OrdinalIgnoreCase));
            bool usesSql = dbFindings.Any(f => f.Symbol.Contains("Sql", StringComparison.OrdinalIgnoreCase));
            if (usesOracle && !present.Any(p => p.Contains("Oracle.ManagedDataAccess", StringComparison.OrdinalIgnoreCase)))
            { lines.Add("<PackageReference Include=\"Oracle.ManagedDataAccess.Core\" Version=\"23.5.1\" />"); present.Add("Oracle.ManagedDataAccess.Core"); }
            if (usesSql && !present.Any(p => p.Contains("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase)))
            { lines.Add("<PackageReference Include=\"Microsoft.Data.SqlClient\" Version=\"5.2.2\" />"); present.Add("Microsoft.Data.SqlClient"); }
        }

        // DPAPI made portable: the generated Portability.Security shim needs ASP.NET Core Data Protection.
        if (UsesDataProtection(portableFiles) && !present.Any(p => p.Contains("Microsoft.AspNetCore.DataProtection", StringComparison.OrdinalIgnoreCase)))
        { lines.Add("<PackageReference Include=\"Microsoft.AspNetCore.DataProtection.Extensions\" Version=\"8.0.10\" />"); present.Add("Microsoft.AspNetCore.DataProtection.Extensions"); }

        // Safety net: compile-enabling packages for any non-GUI Windows category that (unexpectedly) remains in
        // a portable file. With the "core stays 100% portable" policy these files now move to .Windows, so this
        // rarely triggers, but it keeps the core compiling if a residual finding slips through.
        var categories = info.Findings
            .Where(f => portableRel.Contains(f.File) && !IsGuiCategory(f.Categoria) && !FindingIsPortableViaSwap(f))
            .Select(f => f.Categoria).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in categories)
        {
            var pkg = CompileEnablingPackage(cat);
            if (pkg is { } p && !present.Contains(p.Pkg))
            {
                lines.Add($"<PackageReference Include=\"{p.Pkg}\" Version=\"{p.Ver}\" />");
                present.Add(p.Pkg);
            }
        }
        return lines;
    }

    /// <summary>Namespace swaps (old -> new) for the packages this project references that have a 1:1 safe
    /// replacement (e.g. Oracle.DataAccess.Client -> Oracle.ManagedDataAccess.Client).</summary>
    private Dictionary<string, string> NamespaceSwapsFor(ProjInfo info)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in info.PackageLines)
        {
            var e = LibraryReplacements.Lookup(IncludeNameOf(line));
            if (e is { NamespaceFrom: { } from, NamespaceTo: { } to }) map[from] = to;
        }
        // Database code that stays portable: swap the Windows-only / legacy provider namespace for the managed
        // one (harmless when absent: the regex only fires if the namespace actually appears in the file).
        if (info.Findings.Any(f => string.Equals(f.Categoria, "Database", StringComparison.OrdinalIgnoreCase)))
            foreach (var (from, to) in DatabaseNamespaceSwaps) map[from] = to;
        return map;
    }

    /// <summary>Applies the namespace swaps and prepends a [PORTAR] header to a portable file that still uses
    /// Windows-only APIs (compiles on net8.0, throws on Linux until rewritten with the given guidance).</summary>
    private string TransformPortable(ProjInfo info, string rel, string content, IReadOnlyDictionary<string, string> nsSwaps)
    {
        foreach (var (from, to) in nsSwaps)
            content = System.Text.RegularExpressions.Regex.Replace(content, $@"\b{System.Text.RegularExpressions.Regex.Escape(from)}\b", to);

        // Security: make Windows-only crypto portable in place (CNG/CSP -> BCL factories; DPAPI -> shim).
        content = ApplySecuritySwaps(content);

        // Threading: make the WPF DispatcherTimer portable in place (-> generated PortableTimer shim).
        content = ApplyThreadingSwaps(content);

        // Only mark [PORTAR] for findings we could NOT resolve here (i.e. not GUI and not made portable via a
        // library/BCL swap). Database/Cryptography are handled transparently above, so they are not flagged.
        var flagged = info.Findings.Where(f => string.Equals(f.File, rel, StringComparison.OrdinalIgnoreCase)
                                            && !IsGuiCategory(f.Categoria) && !FindingIsPortableViaSwap(f)).ToList();
        if (flagged.Count == 0) return content;

        var sb = new StringBuilder();
        sb.AppendLine("// [PORTAR] Este fichero usa APIs solo-Windows. Compila en net8.0 pero puede fallar en Linux");
        sb.AppendLine("// en tiempo de ejecución hasta reescribirlo con la librería/solución multiplataforma indicada:");
        foreach (var f in flagged.OrderBy(f => f.Line).Take(20))
            sb.AppendLine($"//   L{f.Line} [{f.Categoria}] {f.Symbol}: {f.ComoCorregir}");
        sb.AppendLine();
        return sb.ToString() + content;
    }

    // ---------------------------------------------------------------------------------------------
    // Security (Cryptography) cross-platform swaps + generated DPAPI replacement.
    //
    // Objetivo: mantener el código de seguridad MULTIPLATAFORMA usando librerías/BCL, de forma TRANSPARENTE
    // al SO (sin dejar nada "para otro equipo"):
    //   - CNG/CSP (RSACng, RSACryptoServiceProvider, ECDsaCng, DSACng) son solo-Windows -> se sustituyen por
    //     las factorías del BCL RSA.Create()/ECDsa.Create()/DSA.Create(), que devuelven la implementación
    //     nativa de cada SO (CNG en Windows, OpenSSL en Linux/macOS). Mismo tipo base, portable.
    //   - DPAPI (ProtectedData/DataProtectionScope) es solo-Windows -> se apunta al shim portable generado
    //     (Portability.Security.ProtectedData), respaldado por ASP.NET Core Data Protection.
    // ---------------------------------------------------------------------------------------------

    private const string PortableSecurityNamespace = "Portability.Security";

    /// <summary>CNG/CSP concrete crypto types (Windows-only) -> portable BCL factory calls.</summary>
    private static readonly (string From, string To)[] CryptoFactorySwaps =
    {
        ("new RSACng(", "RSA.Create("),
        ("new RSACryptoServiceProvider(", "RSA.Create("),
        ("new ECDsaCng(", "ECDsa.Create("),
        ("new DSACng(", "DSA.Create("),
    };

    /// <summary>Applies the cross-platform security swaps to a portable file: CNG/CSP constructors -> BCL
    /// factories, DPAPI fully-qualified names -> the generated shim, and injects <c>using
    /// Portability.Security;</c> when DPAPI is used unqualified so it resolves to the shim.</summary>
    private static string ApplySecuritySwaps(string content)
    {
        foreach (var (from, to) in CryptoFactorySwaps)
            content = content.Replace(from, to);

        // DPAPI: the Windows-only System.Security.Cryptography.ProtectedData/DataProtectionScope are provided
        // by the generated portable shim in the Portability.Security namespace.
        content = content
            .Replace("System.Security.Cryptography.ProtectedData", PortableSecurityNamespace + ".ProtectedData")
            .Replace("System.Security.Cryptography.DataProtectionScope", PortableSecurityNamespace + ".DataProtectionScope");

        bool usesDpapi = System.Text.RegularExpressions.Regex.IsMatch(content, @"\b(ProtectedData|DataProtectionScope)\b");
        bool hasUsing = System.Text.RegularExpressions.Regex.IsMatch(content, @"(?m)^\s*using\s+" +
            System.Text.RegularExpressions.Regex.Escape(PortableSecurityNamespace) + @"\s*;");
        if (usesDpapi && !hasUsing)
            content = "using " + PortableSecurityNamespace + ";" + Environment.NewLine + content;
        return content;
    }

    /// <summary>True if any of the given files uses DPAPI (ProtectedData): the project then needs the portable
    /// Data Protection shim + package.</summary>
    private bool UsesDataProtection(IEnumerable<(string Abs, string Rel)> files) =>
        files.Any(f => ReadTextCached(f.Abs).Contains("ProtectedData", StringComparison.Ordinal));

    /// <summary>Portable, OS-transparent replacement for Windows DPAPI, generated into the .Core project when
    /// the code uses ProtectedData. Backed by ASP.NET Core Data Protection (works on Windows/Linux/macOS).</summary>
    private static string BuildDataProtectionShim()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [Cross-platform rewrite] Portable, OS-transparent replacement for Windows DPAPI");
        sb.AppendLine("// (System.Security.Cryptography.ProtectedData / DataProtectionScope).");
        sb.AppendLine("//");
        sb.AppendLine("// WHY: DPAPI is Windows-only and throws PlatformNotSupportedException on Linux/macOS. The");
        sb.AppendLine("// migration goal is to keep the code cross-platform TRANSPARENTLY, using a library, with no");
        sb.AppendLine("// per-OS implementation left to anyone else.");
        sb.AppendLine("//");
        sb.AppendLine("// WHAT: this shim exposes the SAME API (a static ProtectedData with Protect/Unprotect and a");
        sb.AppendLine("// DataProtectionScope enum) so existing call sites compile and run UNCHANGED, but the");
        sb.AppendLine("// implementation is backed by ASP.NET Core Data Protection");
        sb.AppendLine("// (Microsoft.AspNetCore.DataProtection.Extensions), Microsoft's official cross-platform");
        sb.AppendLine("// data-protection stack. Keys are generated once and persisted to a per-app key ring on disk;");
        sb.AppendLine("// the payload is authenticated-encrypted (AES + HMAC) by the library on every OS.");
        sb.AppendLine("//");
        sb.AppendLine("// NOTES:");
        sb.AppendLine("//  - optionalEntropy is folded into the Data Protection \"purpose\", preserving the DPAPI rule");
        sb.AppendLine("//    that data protected with a given entropy can only be unprotected with the same entropy.");
        sb.AppendLine("//  - DataProtectionScope.LocalMachine vs CurrentUser selects a machine-wide vs per-user key ring.");
        sb.AppendLine("//  - MIGRATION CAVEAT (existing data at rest): blobs previously protected by the real Windows");
        sb.AppendLine("//    DPAPI cannot be read by this stack (different key material). Re-protect them once on Windows");
        sb.AppendLine("//    (read with the old DPAPI, write back with this shim). New data is cross-platform from now on.");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using Microsoft.AspNetCore.DataProtection;");
        sb.AppendLine();
        sb.AppendLine($"namespace {PortableSecurityNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Cross-platform equivalent of the Windows-only DataProtectionScope (source compatibility).</summary>");
        sb.AppendLine("public enum DataProtectionScope");
        sb.AppendLine("{");
        sb.AppendLine("    CurrentUser,");
        sb.AppendLine("    LocalMachine");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Drop-in, cross-platform replacement for the Windows-only ProtectedData (DPAPI),");
        sb.AppendLine("/// backed by ASP.NET Core Data Protection. Same signatures, transparent to the OS.</summary>");
        sb.AppendLine("public static class ProtectedData");
        sb.AppendLine("{");
        sb.AppendLine("    // One key-ring directory per scope. On first use the library generates the keys and persists");
        sb.AppendLine("    // them here; later runs (any OS) reuse them. Point these to a stable/mounted path in production.");
        sb.AppendLine("    private static readonly string CurrentUserKeyRing =");
        sb.AppendLine("        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), \"dataprotection-keys\");");
        sb.AppendLine("    private static readonly string LocalMachineKeyRing =");
        sb.AppendLine("        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), \"dataprotection-keys\");");
        sb.AppendLine();
        sb.AppendLine("    private static IDataProtector CreateProtector(byte[]? optionalEntropy, DataProtectionScope scope)");
        sb.AppendLine("    {");
        sb.AppendLine("        var keyRing = scope == DataProtectionScope.LocalMachine ? LocalMachineKeyRing : CurrentUserKeyRing;");
        sb.AppendLine("        Directory.CreateDirectory(keyRing);");
        sb.AppendLine("        // DataProtectionProvider.Create persists keys to the given directory and runs on every OS.");
        sb.AppendLine("        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRing));");
        sb.AppendLine("        var protector = provider.CreateProtector(\"PortableDataProtection:\" + scope);");
        sb.AppendLine("        // Fold the DPAPI optionalEntropy into the purpose chain (same isolation guarantee).");
        sb.AppendLine("        if (optionalEntropy is { Length: > 0 })");
        sb.AppendLine("            protector = protector.CreateProtector(Convert.ToBase64String(optionalEntropy));");
        sb.AppendLine("        return protector;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Cross-platform equivalent of ProtectedData.Protect (DPAPI).</summary>");
        sb.AppendLine("    public static byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(userData);");
        sb.AppendLine("        return CreateProtector(optionalEntropy, scope).Protect(userData);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Cross-platform equivalent of ProtectedData.Unprotect (DPAPI).</summary>");
        sb.AppendLine("    public static byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy, DataProtectionScope scope)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(encryptedData);");
        sb.AppendLine("        return CreateProtector(optionalEntropy, scope).Unprotect(encryptedData);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Threading (hilos/tareas) cross-platform swaps + generated portable timer.
    //
    // Objetivo: hilos y tareas paralelas/asíncronas MULTIPLATAFORMA, transparente al SO. La mayoría del
    // modelo de hilos del BCL YA es portable (Thread, Task, Parallel, async/await, ThreadPool, SemaphoreSlim,
    // System.Threading.Timer) y NO se toca. Lo específico de Windows:
    //   - WPF DispatcherTimer (System.Windows.Threading) -> se sustituye por un shim portable
    //     Portability.Threading.PortableTimer con la MISMA API (Interval/Tick/Start/Stop), respaldado por
    //     System.Timers.Timer y con marshalling al SynchronizationContext capturado. Cambio transparente.
    //   - Dispatcher.Invoke/BeginInvoke (marshalling a UI) NO se auto-reescribe (semántica de UI): esos
    //     ficheros van al lado Windows; la guía (async/await + IProgress<T>) está en el informe.
    // ---------------------------------------------------------------------------------------------

    private const string PortableThreadingNamespace = "Portability.Threading";

    /// <summary>Applies the cross-platform threading swaps to a portable file: WPF DispatcherTimer -> the
    /// generated PortableTimer, drops the (now unavailable) System.Windows.Threading using and imports the
    /// portable namespace.</summary>
    private static string ApplyThreadingSwaps(string content)
    {
        if (!content.Contains("DispatcherTimer", StringComparison.Ordinal)) return content;

        // Swap the type name everywhere (declarations, `new`, fields). PortableTimer mirrors its surface.
        content = System.Text.RegularExpressions.Regex.Replace(content, @"\bDispatcherTimer\b", "PortableTimer");
        // Remove the WPF threading using (System.Windows.Threading is not available in the portable core).
        content = System.Text.RegularExpressions.Regex.Replace(content, @"(?m)^\s*using\s+System\.Windows\.Threading\s*;\s*\r?\n", string.Empty);

        bool hasUsing = System.Text.RegularExpressions.Regex.IsMatch(content, @"(?m)^\s*using\s+" +
            System.Text.RegularExpressions.Regex.Escape(PortableThreadingNamespace) + @"\s*;");
        if (!hasUsing)
            content = "using " + PortableThreadingNamespace + ";" + Environment.NewLine + content;
        return content;
    }

    /// <summary>True if any of the given files uses the WPF DispatcherTimer: the project then needs the portable
    /// timer shim.</summary>
    private bool UsesPortableThreading(IEnumerable<(string Abs, string Rel)> files) =>
        files.Any(f => ReadTextCached(f.Abs).Contains("DispatcherTimer", StringComparison.Ordinal));

    /// <summary>Portable, OS-transparent replacement for the WPF DispatcherTimer, generated into the .Core
    /// project when the code uses it. Mirrors DispatcherTimer's surface (Interval/Tick/Start/Stop/IsEnabled)
    /// and is backed by System.Timers.Timer, marshalling the Tick to the captured SynchronizationContext.</summary>
    private static string BuildPortableTimerShim()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [Cross-platform rewrite] Portable, OS-transparent replacement for the WPF DispatcherTimer");
        sb.AppendLine("// (System.Windows.Threading.DispatcherTimer).");
        sb.AppendLine("//");
        sb.AppendLine("// WHY: DispatcherTimer belongs to WPF (Windows only) and ties the callback to the WPF");
        sb.AppendLine("// Dispatcher thread. The migration goal is cross-platform code, transparent to the OS, using");
        sb.AppendLine("// the BCL only, without any per-OS implementation.");
        sb.AppendLine("//");
        sb.AppendLine("// WHAT: this shim exposes the SAME surface (Interval, Tick, Start, Stop, IsEnabled) so existing");
        sb.AppendLine("// call sites compile and run UNCHANGED, but it is backed by System.Timers.Timer (cross-platform).");
        sb.AppendLine("// The Tick is marshalled back to the SynchronizationContext captured at construction time, so a");
        sb.AppendLine("// timer created on a UI thread still raises Tick on that thread (same behaviour as DispatcherTimer),");
        sb.AppendLine("// and off any UI it simply runs on a thread-pool thread.");
        sb.AppendLine("//");
        sb.AppendLine("// HOW TO CONFIRM/TEST: create a PortableTimer, set Interval, subscribe Tick, call Start(); verify");
        sb.AppendLine("// the Tick fires at the interval on Windows and Linux. For UI updates, ensure it is constructed on");
        sb.AppendLine("// the UI thread (so SynchronizationContext.Current is the UI context) or marshal in the handler.");
        sb.AppendLine("//");
        sb.AppendLine("// NOTE: for purely asynchronous loops prefer System.Threading.PeriodicTimer (await timer.WaitForNextTickAsync()).");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using Timers = System.Timers;");
        sb.AppendLine();
        sb.AppendLine($"namespace {PortableThreadingNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Cross-platform drop-in for the WPF DispatcherTimer (same Interval/Tick/Start/Stop API).</summary>");
        sb.AppendLine("public sealed class PortableTimer : IDisposable");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly Timers.Timer _timer = new() { AutoReset = true };");
        sb.AppendLine("    private readonly SynchronizationContext? _ctx = SynchronizationContext.Current;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Raised on each interval, marshalled to the captured SynchronizationContext when there is one.</summary>");
        sb.AppendLine("    public event EventHandler? Tick;");
        sb.AppendLine();
        sb.AppendLine("    // Absorbs the DispatcherTimer(DispatcherPriority[, Dispatcher]) overloads; the arguments are");
        sb.AppendLine("    // not needed in the portable model, so they are ignored.");
        sb.AppendLine("    public PortableTimer(params object?[] _ignored)");
        sb.AppendLine("    {");
        sb.AppendLine("        _timer.Interval = 1000;");
        sb.AppendLine("        _timer.Elapsed += (_, __) => Raise();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Interval between ticks (same semantics as DispatcherTimer.Interval).</summary>");
        sb.AppendLine("    public TimeSpan Interval");
        sb.AppendLine("    {");
        sb.AppendLine("        get => TimeSpan.FromMilliseconds(_timer.Interval);");
        sb.AppendLine("        set => _timer.Interval = value.TotalMilliseconds <= 0 ? 1 : value.TotalMilliseconds;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Whether the timer is running (same semantics as DispatcherTimer.IsEnabled).</summary>");
        sb.AppendLine("    public bool IsEnabled");
        sb.AppendLine("    {");
        sb.AppendLine("        get => _timer.Enabled;");
        sb.AppendLine("        set { if (value) Start(); else Stop(); }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void Start() => _timer.Start();");
        sb.AppendLine("    public void Stop() => _timer.Stop();");
        sb.AppendLine();
        sb.AppendLine("    private void Raise()");
        sb.AppendLine("    {");
        sb.AppendLine("        var handler = Tick;");
        sb.AppendLine("        if (handler is null) return;");
        sb.AppendLine("        if (_ctx is not null) _ctx.Post(_ => handler(this, EventArgs.Empty), null);");
        sb.AppendLine("        else handler(this, EventArgs.Empty);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void Dispose() => _timer.Dispose();");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Builds the code overrides (namespace swaps + [PORTAR] marks) for a set of portable files,
    /// on top of any base overrides (e.g. the seam-injected content).</summary>
    private Dictionary<string, string> BuildPortableOverrides(ProjInfo info, IEnumerable<(string Abs, string Rel)> files,
        IReadOnlyDictionary<string, string> nsSwaps, IReadOnlyDictionary<string, string>? baseOverrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var content = baseOverrides is not null && baseOverrides.TryGetValue(f.Rel, out var oc) ? oc : ReadTextCached(f.Abs);
            result[f.Rel] = TransformPortable(info, f.Rel, content, nsSwaps);
        }
        return result;
    }

    /// <summary>Heurística de portabilidad de un proyecto EXTERNO (no reescrito): portable si su TFM no
    /// apunta a *-windows y no usa WPF/WinForms. Si no se puede leer, se considera NO portable (conservador:
    /// no se añade al núcleo net8.0 para no arrastrar dependencias de Windows en silencio).</summary>
    private static bool IsPortableCsproj(string absCsproj)
    {
        try
        {
            var text = File.ReadAllText(absCsproj);
            if (Regex.IsMatch(text, "<UseWPF>\\s*true", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, "<UseWindowsForms>\\s*true", RegexOptions.IgnoreCase)) return false;
            var tfm = Prop(text, "TargetFramework") ?? Prop(text, "TargetFrameworks");
            if (tfm is null) return false;
            return !tfm.Split(';').Any(t => t.Trim().Contains("-windows", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Rutas relativas (estilo csproj) desde <paramref name="outProjDir"/> a los .csproj externos.
    /// Si <paramref name="onlyPortable"/> es true, solo incluye los externos portables (para el núcleo net8.0).</summary>
    private static IReadOnlyList<string> ExternalRelPaths(IEnumerable<(string AbsCsproj, bool Portable)> externals, string outProjDir, bool onlyPortable)
    {
        var list = new List<string>();
        foreach (var e in externals)
        {
            if (onlyPortable && !e.Portable) continue;
            list.Add(Path.GetRelativePath(outProjDir, e.AbsCsproj).Replace('/', '\\'));
        }
        return list;
    }

    private static bool IsWindowsOnlyPackage(string packageLine)
    {
        var inc = IncludeAttr.Match(packageLine);
        if (!inc.Success) return false;
        var name = inc.Groups[1].Value;
        return WindowsOnlyPackageMarkers.Any(mk => name.Contains(mk, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteCsproj(string path, string tfm, IReadOnlyList<string> packageLines, bool useWpf, bool useWinForms,
        string? outputType, IReadOnlyList<string> refNames, IReadOnlyList<string> refRelPaths, bool windowsOnlyFilter,
        ProjInfo? source = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
        if (!string.IsNullOrWhiteSpace(outputType)) sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
        // Se CONSERVAN las propiedades del proyecto original (si estaban) para no cambiar el comportamiento
        // de compilacion; si el original no las declaraba, no se emiten (se respeta el default del SDK).
        if (source?.ImplicitUsings is { } iu) sb.AppendLine($"    <ImplicitUsings>{iu}</ImplicitUsings>");
        if (source?.Nullable is { } nl) sb.AppendLine($"    <Nullable>{nl}</Nullable>");
        if (source?.LangVersion is { } lv) sb.AppendLine($"    <LangVersion>{lv}</LangVersion>");
        if (source?.RootNamespace is { } rns) sb.AppendLine($"    <RootNamespace>{rns}</RootNamespace>");
        if (useWpf) sb.AppendLine("    <UseWPF>true</UseWPF>");
        if (useWinForms) sb.AppendLine("    <UseWindowsForms>true</UseWindowsForms>");
        sb.AppendLine("  </PropertyGroup>");

        if (refRelPaths.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var r in refRelPaths) sb.AppendLine($"    <ProjectReference Include=\"{r}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        var pkgs = windowsOnlyFilter ? packageLines.Where(p => !IsWindowsOnlyPackage(p)).ToList() : packageLines.ToList();
        if (pkgs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var p in pkgs) sb.AppendLine($"    {p}");
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private const string SdkCsharpProjectTypeGuid = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";

    private static void WriteSolution(string path, IReadOnlyList<(string Name, string RelCsproj)> projects)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        var guids = new Dictionary<string, string>();
        foreach (var (name, rel) in projects)
        {
            var guid = DeterministicGuid(name).ToUpperInvariant();
            guids[name] = guid;
            sb.AppendLine($"Project(\"{{{SdkCsharpProjectTypeGuid}}}\") = \"{name}\", \"{rel}\", \"{{{guid}}}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var (name, _) in projects)
        {
            var g = guids[name];
            sb.AppendLine($"\t\t{{{g}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{{{g}}}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{{{g}}}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{{{g}}}.Release|Any CPU.Build.0 = Release|Any CPU");
        }
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Reconstruye el orden de compilación de la solución GENERADA: lee las ProjectReference de
    /// cada .csproj emitido, resuelve el grafo entre los proyectos generados y lo ordena por niveles
    /// topológicos (Kahn). Los del mismo nivel no dependen entre sí. Devuelve (nivel, nombre) ordenado.</summary>
    private static List<(int Level, string Name)> ComputeBuildOrder(string outputDir, IReadOnlyList<(string Name, string RelCsproj)> emitted)
    {
        var names = emitted.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rel) in emitted)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var text = File.ReadAllText(Path.Combine(outputDir, rel));
                foreach (Match m in ProjectReferenceInclude.Matches(text))
                {
                    var refName = Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'));
                    if (names.Contains(refName) && !string.Equals(refName, name, StringComparison.OrdinalIgnoreCase)) set.Add(refName);
                }
            }
            catch { /* ignorar */ }
            deps[name] = set;
        }

        var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var indegree = deps.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        foreach (var n in queue) level[n] = 0;
        var ordered = new List<string>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue(); ordered.Add(n);
            foreach (var m in deps.Where(kv => kv.Value.Contains(n)).Select(kv => kv.Key))
            {
                level[m] = Math.Max(level.TryGetValue(m, out var lv) ? lv : 0, level[n] + 1);
                if (--indegree[m] == 0) queue.Enqueue(m);
            }
        }
        // Los que queden (ciclo) se añaden al final con su nivel actual.
        foreach (var n in deps.Keys) if (!ordered.Contains(n)) { ordered.Add(n); level[n] = level.TryGetValue(n, out var lv) ? lv : 0; }
        return ordered.OrderBy(n => level[n]).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => (level[n], n)).ToList();
    }

    /// <summary>GUID estable derivado del nombre (para que el .sln sea reproducible).</summary>
    private static string DeterministicGuid(string name)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("PortabilityAnalyzer.Rewrite:" + name));
        return new Guid(hash).ToString();
    }

    private static void WriteMigrationReadme(string path, string solutionName, IReadOnlyList<RewrittenProject> projects,
        IReadOnlyList<string> warnings, IReadOnlyList<(string Project, string Concrete, string Interface, string Namespace)> seams,
        IReadOnlyList<(int Level, string Name)> buildOrder, string? abstractionsName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {solutionName}: solución reescrita a multiplataforma");
        sb.AppendLine();
        sb.AppendLine("> Cambios ya implementados por la reescritura library-first. La solución original no se ha tocado;");
        sb.AppendLine("> esta es una solución nueva, completa y separada. Este documento resume **lo que se ha hecho**.");
        sb.AppendLine();

        sb.AppendLine("## Separación realizada por proyecto");
        sb.AppendLine();
        sb.AppendLine("| Proyecto original | Resultado | Proyectos generados | Ficheros núcleo | Ficheros Windows |");
        sb.AppendLine("|---|---|---|---:|---:|");
        foreach (var p in projects)
            sb.AppendLine($"| {p.OriginalProject} | {p.Kind}: {p.Reason} | {string.Join(" + ", p.OutputProjects)} | {p.PortableFiles} | {p.WindowsFiles} |");
        sb.AppendLine();
        sb.AppendLine("- **Portable**: no tenía dependencias de Windows → quedó en un único proyecto `net8.0` (los ya `net8.0`,");
        sb.AppendLine("  p. ej. los `*Multi`, se copiaron sin cambios; los `net8.0-windows` sin dependencias reales se retargetearon).");
        sb.AppendLine("- **Separable**: se dividió en `X.Core` (`net8.0`, portable) + `X.Windows` (`net8.0-windows`). El **núcleo queda");
        sb.AppendLine("  100% portable**: solo se quedan en `.Core` las clases sin dependencia de Windows o cuya dependencia se resuelve");
        sb.AppendLine("  con un cambio de librería (p. ej. driver de BD gestionado). Toda clase con una dependencia de Windows sin");
        sb.AppendLine("  reemplazo directo (Registro, EventLog, WMI, DPAPI, P/Invoke, COM…) se mueve a `.Windows`.");
        sb.AppendLine("- **Namespaces rebasados**: los ficheros de `X.Core` declaran `namespace X.Core[.Sub]` y los de `X.Windows`");
        sb.AppendLine("  `namespace X.Windows[.Sub]`; se actualizaron todas las referencias (`using` y nombres cualificados) de la solución.");
        sb.AppendLine("- **SoloWindows**: todo el proyecto dependía de Windows (UI/punto de entrada) → quedó en `net8.0-windows`.");
        sb.AppendLine();

        sb.AppendLine("## Referencias recableadas (hecho)");
        sb.AppendLine("- Cada proyecto **`.Core`/portable** referencia solo núcleos portables (`*.Core` o proyectos portables).");
        sb.AppendLine("- Cada proyecto **`.Windows`** referencia su propio `.Core` y las partes `.Windows` de sus dependencias.");
        sb.AppendLine("- Los **paquetes NuGet solo-Windows** (EventLog, Registry, ProtectedData…) se dejaron solo en los `.Windows`.");
        sb.AppendLine("- Las **referencias a proyectos externos** a la solución se conservaron apuntando a su `.csproj` original.");
        sb.AppendLine();

        if (abstractionsName is not null)
        {
            sb.AppendLine("## Capa de abstracción generada (hecho)");
            sb.AppendLine();
            sb.AppendLine($"Se ha **generado el proyecto `{abstractionsName}`** (`net8.0`, portable) con las **interfaces** de las");
            sb.AppendLine("capacidades que dependen del SO y una **implementación multiplataforma por defecto** de cada una (funciona");
            sb.AppendLine("en Windows y Linux), registradas por **inyección de dependencias**. Todos los proyectos generados lo");
            sb.AppendLine("referencian, y el `CompositionRoot.Build()` llama a `AddPortableAbstractions()`.");
            sb.AppendLine();
            sb.AppendLine("| Interfaz | Implementación por defecto (portable) | Sustituir por (opcional) |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine("| `ISettingsStore` | `EnvironmentSettingsStore` (variables de entorno) | `Microsoft.Extensions.Configuration` (appsettings.json) |");
            sb.AppendLine("| `IUserIdentity` / `IAuthenticationService` | `EnvironmentUserIdentity` (`Environment.UserName`) | LDAP (`System.DirectoryServices.Protocols`) |");
            sb.AppendLine("| `IProcessRunner` | `ProcessRunner` (`System.Diagnostics.Process`) | — |");
            sb.AppendLine("| `INativePlatform` | `PortableNativePlatform` (BCL gestionado) | librería nativa multiplataforma según necesidad |");
            sb.AppendLine("| `IInterProcessLock` | `MutexInterProcessLock` (Mutex con nombre) | bloqueo por fichero / mecanismo del SO |");
            sb.AppendLine("| `IUserNotifier` | `ConsoleUserNotifier` (consola) | diálogo WPF/WinForms en la app Windows |");
            sb.AppendLine();
            sb.AppendLine("> Solo se generan las interfaces de las categorías detectadas en la solución. Cambia cualquier");
            sb.AppendLine("> implementación registrando la tuya en el contenedor DI después de `AddPortableAbstractions()`.");
            sb.AppendLine();
        }

        if (seams.Count > 0)
        {
            sb.AppendLine("## Seams aplicados (hecho): interfaz en el núcleo, implementación Windows y DI");
            sb.AppendLine();
            sb.AppendLine("Cada fichero del núcleo que dependía de una clase de Windows se ha **desacoplado**: se extrajo su interfaz");
            sb.AppendLine("al núcleo, la clase Windows la implementa, y el consumidor recibe la interfaz por **inyección por constructor**.");
            sb.AppendLine("Además se **generó el registro DI** (`SeamRegistration.AddWindowsSeams`) en cada proyecto `.Windows`.");
            sb.AppendLine();
            sb.AppendLine("| Interfaz (núcleo) | Implementación Windows | Proyecto |");
            sb.AppendLine("|---|---|---|");
            foreach (var s in seams)
                sb.AppendLine($"| `{s.Interface}` | `{s.Concrete}` | {s.Project} |");
            sb.AppendLine();
            sb.AppendLine("**El cableado por DI ya está hecho en el código generado**: en el proyecto de arranque se generó un");
            sb.AppendLine("`CompositionRoot.Build()` que crea el `ServiceCollection`, llama a `AddWindowsSeams()` de cada proyecto");
            sb.AppendLine("`.Windows` con seams y devuelve el proveedor; además se intentó invocarlo desde el punto de entrada");
            sb.AppendLine("(si no fue posible —p. ej. WPF con `Main` autogenerado— hay un aviso indicando dónde llamarlo).");
            sb.AppendLine();
            sb.AppendLine("Los seams son un **último recurso** (solo cuando no hay librería multiplataforma): la interfaz vive en");
            sb.AppendLine("el núcleo portable y su implementación se aporta **en el propio código**, sin dejar nada para otro equipo.");
            sb.AppendLine();
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine("## Avisos (revisar)");
            foreach (var w in warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        if (buildOrder.Count > 0)
        {
            sb.AppendLine("## Orden de compilación reconstruido (solución generada)");
            sb.AppendLine();
            sb.AppendLine("Tras separar y recablear, este es el orden topológico por `ProjectReference` (los proyectos");
            sb.AppendLine("del mismo nivel no dependen entre sí y pueden compilarse en paralelo):");
            sb.AppendLine();
            foreach (var g in buildOrder.GroupBy(x => x.Level).OrderBy(g => g.Key))
                sb.AppendLine($"{g.Key + 1}. {string.Join(", ", g.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
            sb.AppendLine();
        }

        sb.AppendLine("## Verificación sugerida");
        sb.AppendLine($"1. Abre `{solutionName}.sln` y **compila**: los núcleos `net8.0` compilan en cualquier SO; el código");
        sb.AppendLine("   Windows (WPF/Registro/P-Invoke…) compila en `net8.0-windows`.");
        sb.AppendLine("2. El `CompositionRoot.Build()` (AddPortableAbstractions + AddWindowsSeams) ya está generado y cableado en el arranque; complétalo");
        sb.AppendLine("   registrando tus servicios y el tipo raíz de la app.");
        sb.AppendLine("3. Añade un CI multiplataforma (matriz Windows + Linux) que compile los núcleos portables.");
        sb.AppendLine();
        sb.AppendLine("## Rutas largas (Windows MAX_PATH 260)");
        sb.AppendLine("- Se ha generado un **`Directory.Build.props`** que reubica `obj`/`bin` a una ruta corta bajo el perfil");
        sb.AppendLine("  de usuario (`%USERPROFILE%\\.pa-builds\\…`), para que las rutas intermedias no superen el límite de");
        sb.AppendLine("  **260 caracteres** de Windows (causa habitual de que Visual Studio **no cargue** los proyectos).");
        sb.AppendLine("  Bórralo si prefieres el `obj`/`bin` por proyecto.");
        sb.AppendLine("- Si el aviso de \"Rutas largas\" aparece, algún **fichero fuente** queda cerca del límite: mueve esta");
        sb.AppendLine("  solución a una carpeta **más corta**, o habilita rutas largas en Windows");
        sb.AppendLine("  (`HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled = 1` y reinicia Visual Studio).");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // ---------------------------------------------------------------------------------------------
    // Clasificación de ficheros (compartida en espíritu con ProjectSplitter)
    // ---------------------------------------------------------------------------------------------

    private static (List<(string Abs, string Rel)> Win, List<(string Abs, string Rel)> Multi) ClassifyContent(
        List<(string Abs, string Rel)> content, List<(string Abs, string Rel)> winCode, List<(string Abs, string Rel)> portableCode)
    {
        var winStems = winCode.Select(f => StemKey(f.Rel)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var portStems = portableCode.Select(f => StemKey(f.Rel)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var win = new List<(string, string)>();
        var multi = new List<(string, string)>();
        foreach (var f in content)
        {
            if (f.Rel.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) { win.Add(f); continue; }
            var stem = StemKey(f.Rel);
            if (winStems.Contains(stem)) win.Add(f);
            else if (portStems.Contains(stem)) multi.Add(f);
            else if (IsWindowsContent(f.Rel)) win.Add(f);
            else multi.Add(f);
        }
        return (win, multi);
    }

    private static string StemKey(string rel)
    {
        var dir = Path.GetDirectoryName(rel) ?? string.Empty;
        var name = Path.GetFileName(rel);
        var dot = name.IndexOf('.');
        return dir + "|" + (dot > 0 ? name[..dot] : name);
    }

    private static bool IsWindowsContent(string rel)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return ext is ".xaml" or ".resx" or ".settings" or ".resources" or ".baml" or ".config"
            or ".png" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".gif" or ".cur";
    }

    private static bool IsObjBin(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVsJunk(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dtbcache.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // Respetar la configuración real del proyecto: .gitignore + items de compilación del .csproj.
    // ---------------------------------------------------------------------------------------------

    private static readonly Regex CompileRemoveAttr =
        new("<Compile\\s+[^>]*?Remove\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CompileIncludeAttr =
        new("<Compile\\s+[^>]*?Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Lee un .gitignore y devuelve nombres de CARPETA a ignorar y globs de FICHERO simples (sin
    /// ruta). Conservador a propósito: solo patrones sin '/' (los típicos de artefactos), para no arriesgar
    /// omitir código fuente por reglas ancladas complejas.</summary>
    private static (HashSet<string> Folders, List<string> FileGlobs) LoadGitignore(string dir)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globs = new List<string>();
        try
        {
            var gi = Path.Combine(dir, ".gitignore");
            if (!File.Exists(gi)) return (folders, globs);
            foreach (var raw in File.ReadAllLines(gi))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("!")) continue;
                line = line.TrimStart('/').TrimEnd('/');
                if (line.Length == 0 || line.Contains('/')) continue; // solo patrones simples
                if (line.Contains('*') || line.Contains('?')) globs.Add(line);
                else folders.Add(line);
            }
        }
        catch { /* ignorar */ }
        return (folders, globs);
    }

    /// <summary>True si el fichero (ruta relativa al proyecto) debe ignorarse: alguna de sus carpetas está
    /// en la lista de carpetas ignoradas, o su nombre casa un glob de fichero ignorado.</summary>
    private static bool IsGitIgnored(string rel, HashSet<string> ignoreFolders, List<string> ignoreGlobs)
    {
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
            if (ignoreFolders.Contains(parts[i])) return true;
        var name = parts[^1];
        if (ignoreFolders.Contains(name)) return true;
        foreach (var g in ignoreGlobs)
            if (Regex.IsMatch(name, GlobToRegex(g), RegexOptions.IgnoreCase)) return true;
        return false;
    }

    /// <summary>Filtra los .cs según los items de compilación del .csproj: honra
    /// <c>&lt;EnableDefaultCompileItems&gt;false</c>/<c>&lt;EnableDefaultItems&gt;false</c>,
    /// <c>&lt;Compile Include&gt;</c> y <c>&lt;Compile Remove&gt;</c>. Devuelve los incluidos y, por
    /// referencia, los que se han excluido (para avisar).</summary>
    private static List<(string Abs, string Rel)> FilterByCompileItems(List<(string Abs, string Rel)> cs, string csprojText, out List<string> removed)
    {
        removed = new List<string>();
        var enableDefault = !(string.Equals(Prop(csprojText, "EnableDefaultCompileItems"), "false", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Prop(csprojText, "EnableDefaultItems"), "false", StringComparison.OrdinalIgnoreCase));
        var removes = CompileRemoveAttr.Matches(csprojText).Select(m => m.Groups[1].Value).ToList();
        var includes = CompileIncludeAttr.Matches(csprojText).Select(m => m.Groups[1].Value).ToList();

        var kept = new List<(string Abs, string Rel)>();
        foreach (var f in cs)
        {
            bool included = enableDefault || includes.Any(p => GlobMatch(f.Rel, p));
            bool removedByRule = removes.Any(p => GlobMatch(f.Rel, p));
            if (included && !removedByRule) kept.Add(f);
            else removed.Add(f.Rel);
        }
        return kept;
    }

    /// <summary>Casa una ruta relativa contra un glob de MSBuild (<c>**</c>, <c>*</c>, <c>?</c>), normalizando
    /// separadores. Comparación sin distinguir mayúsculas.</summary>
    private static bool GlobMatch(string rel, string pattern)
    {
        var r = rel.Replace('\\', '/');
        var p = pattern.Replace('\\', '/').TrimStart('/');
        return Regex.IsMatch(r, "^" + GlobToRegex(p) + "$", RegexOptions.IgnoreCase);
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; if (i + 1 < glob.Length && glob[i + 1] == '/') i++; }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    private bool IsEntryPoint(string absPath)
    {
        var root = RootCached(absPath);
        if (root is null) return false;
        if (root.DescendantNodes().OfType<GlobalStatementSyntax>().Any()) return true;
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "Main" && m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));
    }

    /// <summary>Expande el conjunto Windows por clases parciales (mismo tipo en varios ficheros) y por
    /// herencia (una clase que deriva de un tipo que quedó en Windows también va a Windows).</summary>
    private HashSet<string> ExpandWindowsSet(List<(string Abs, string Rel)> allCs, HashSet<string> winSet)
    {
        var declaredByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var basesByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var filesByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var f in allCs)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var bases = new HashSet<string>(StringComparer.Ordinal);
            var root = RootCached(f.Abs);
            if (root is not null)
            {
                foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    declared.Add(t.Identifier.Text);
                    if (!filesByType.TryGetValue(t.Identifier.Text, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); filesByType[t.Identifier.Text] = set; }
                    set.Add(f.Rel);
                    if (t.BaseList is not null)
                        foreach (var bt in t.BaseList.Types) bases.Add(BaseName(bt.Type));
                }
            }
            declaredByFile[f.Rel] = declared;
            basesByFile[f.Rel] = bases;
        }

        var result = new HashSet<string>(winSet, StringComparer.OrdinalIgnoreCase);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var files in filesByType.Values)
            {
                if (files.Count < 2 || !files.Any(result.Contains)) continue;
                foreach (var file in files) if (result.Add(file)) changed = true;
            }
            var winTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in result)
                if (declaredByFile.TryGetValue(rel, out var d))
                    foreach (var t in d) winTypes.Add(t);
            foreach (var f in allCs)
            {
                if (result.Contains(f.Rel)) continue;
                if (basesByFile.TryGetValue(f.Rel, out var bases) && bases.Any(winTypes.Contains))
                    if (result.Add(f.Rel)) changed = true;
            }
        }
        return result;
    }

    private static string BaseName(TypeSyntax t) => t switch
    {
        SimpleNameSyntax s => s.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        _ => t.ToString()
    };

    // ---------------------------------------------------------------------------------------------
    // Propagación transitiva de "Windows" por el grafo de tipos de TODA la solución.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Tipos base del framework que atan una clase a Windows (WPF/WinForms). Heredar de ellos
    /// (directa o transitivamente) hace que el tipo —y sus consumidores— sean de Windows.</summary>
    private static readonly HashSet<string> WindowsFrameworkBases = new(StringComparer.Ordinal)
    {
        "Window", "Form", "UserControl", "Control", "Page", "Application", "DependencyObject",
        "FrameworkElement", "ContentControl", "ContainerControl", "ScrollableControl", "CommonDialog",
        "NativeWindow", "ApplicationContext", "Freezable", "DispatcherObject", "Visual", "UIElement",
        "Dispatcher", "DrawingVisual", "HwndHost"
    };

    private readonly Dictionary<string, (HashSet<string> Declared, HashSet<string> Referenced, bool DerivesWinBase)> _fileTypes
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tipos declarados y tipos referenciados (heurística sintáctica) de un fichero .cs, con caché.</summary>
    private (HashSet<string> Declared, HashSet<string> Referenced, bool DerivesWinBase) FileTypesOf(string abs)
    {
        if (_fileTypes.TryGetValue(abs, out var cached)) return cached;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        bool derivesWinBase = false;
        var root = RootCached(abs);
        if (root is not null)
        {
            foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                declared.Add(t.Identifier.Text);
                if (t.BaseList is not null)
                    foreach (var bt in t.BaseList.Types)
                    {
                        var bn = BaseName(bt.Type);
                        referenced.Add(bn);
                        if (WindowsFrameworkBases.Contains(bn)) derivesWinBase = true;
                    }
            }
            foreach (var n in root.DescendantNodes())
            {
                switch (n)
                {
                    case QualifiedNameSyntax q when q.Parent is not QualifiedNameSyntax:
                        referenced.Add(q.Right.Identifier.Text); break;
                    case GenericNameSyntax g:
                        referenced.Add(g.Identifier.Text); break;
                    case IdentifierNameSyntax id:
                        if (id.Parent is QualifiedNameSyntax) break;                 // parte de A.B (tratado arriba)
                        if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) break; // miembro .X, no tipo
                        referenced.Add(id.Identifier.Text); break;
                }
            }
        }
        var result = (declared, referenced, derivesWinBase);
        _fileTypes[abs] = result;
        return result;
    }

    /// <summary>Conjunto de nombres de tipo (simples) que son de Windows en TODA la solución: se siembra con
    /// los ficheros con hallazgo directo y los que heredan de un tipo base de Windows, y se propaga por
    /// herencia y uso de tipos hasta punto fijo (un tipo que use un tipo Windows es también de Windows).</summary>
    private HashSet<string> ComputeWindowsTypes(List<ProjInfo> infos)
    {
        var all = new List<(string Abs, string Rel, ProjInfo Info)>();
        foreach (var info in infos)
            foreach (var f in info.CodeFiles)
                all.Add((f.Abs, f.Rel, info));

        var winTypes = new HashSet<string>(StringComparer.Ordinal);
        // Seed: files that are Windows-bound (GUI findings, WPF/WinForms base types, or a non-GUI Windows API
        // WITHOUT a clean library swap: Registry, EventLog, WMI, DPAPI, P/Invoke...). A file whose only
        // Windows finding is a clean library swap (Database -> managed driver) stays portable.
        foreach (var (abs, rel, info) in all)
        {
            var ft = FileTypesOf(abs);
            if (info.WinDependentFiles.Contains(rel) || ft.DerivesWinBase) winTypes.UnionWith(ft.Declared);
        }
        // Propagate to a fixpoint: a type that (transitively) uses a Windows type is also Windows.
        bool changed = true; int guard = 0;
        while (changed && guard++ < 100)
        {
            changed = false;
            foreach (var (abs, rel, info) in all)
            {
                var ft = FileTypesOf(abs);
                bool win = info.WinDependentFiles.Contains(rel) || ft.DerivesWinBase
                           || ft.Declared.Overlaps(winTypes) || ft.Referenced.Overlaps(winTypes);
                if (win)
                    foreach (var t in ft.Declared)
                        if (winTypes.Add(t)) changed = true;
            }
        }
        return winTypes;
    }

    /// <summary>True if the file must go to the Windows side: it is Windows-bound (a GUI finding, or a
    /// non-GUI Windows API with no clean library swap), a .xaml.cs code-behind, the entry point of a GUI app,
    /// derives from a WPF/WinForms base type, or declares/uses (transitively) a Windows type. Only files whose
    /// Windows dependency is a clean library swap (Database -> managed driver) stay portable in .Core.</summary>
    private bool FileTouchesWindows(string abs, string rel, ProjInfo info, HashSet<string> winTypes)
    {
        if (info.WinDependentFiles.Contains(rel)) return true;
        if (rel.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsEntryPoint(abs) && (info.UseWpf || info.UseWinForms)) return true; // only a GUI app's entry point
        var ft = FileTypesOf(abs);
        return ft.DerivesWinBase || ft.Declared.Overlaps(winTypes) || ft.Referenced.Overlaps(winTypes);
    }

    /// <summary>True if a source-finding category is a GUI (graphical interface) dependency (WPF/WinForms).</summary>
    private static bool IsGuiCategory(string categoria) =>
        categoria.Equals("UI", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if a Windows finding of this category can be made portable IN PLACE with a cross-platform
    /// library/BCL swap, so the file stays in the portable .Core:
    ///  - "Database": managed drivers (Oracle.ManagedDataAccess.Core / Microsoft.Data.SqlClient).
    ///  - "Cryptography": DPAPI -> ASP.NET Core Data Protection (generated shim) and CNG/CSP -> the portable
    ///    BCL factories RSA.Create()/ECDsa.Create() (see <see cref="ApplySecuritySwaps"/>).
    /// Every other Windows API (Registry, EventLog, WMI, P/Invoke, COM...) has no drop-in and moves to
    /// .Windows so the core stays 100% portable.</summary>
    private static bool IsPortableViaSwap(string categoria) =>
        categoria.Equals("Database", StringComparison.OrdinalIgnoreCase)
        || categoria.Equals("Cryptography", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finding-level version of <see cref="IsPortableViaSwap(string)"/>. Database and Cryptography are
    /// portable for the whole category; "Threading" is portable only for the swappable symbols (the WPF
    /// <c>DispatcherTimer</c> -> generated PortableTimer, and the <c>System.Windows.Threading</c> using that is
    /// dropped). A Threading finding on <c>Dispatcher</c>/<c>DispatcherObject</c> (UI-thread marshalling) is NOT
    /// swappable and keeps its file on the Windows side.</summary>
    private static bool FindingIsPortableViaSwap(SourceFinding f) =>
        IsPortableViaSwap(f.Categoria)
        || (f.Categoria.Equals("Threading", StringComparison.OrdinalIgnoreCase) && IsPortableThreadingSymbol(f.Symbol));

    /// <summary>A Threading symbol that the rewriter can make portable in place.</summary>
    private static bool IsPortableThreadingSymbol(string symbol) =>
        symbol.Contains("DispatcherTimer", StringComparison.Ordinal)
        || symbol.Equals("System.Windows.Threading", StringComparison.Ordinal);

    /// <summary>Database namespace swaps applied to portable files so the managed, cross-platform driver is
    /// used instead of the Windows-only / legacy provider (source-level, independent of the package reference).</summary>
    private static readonly (string From, string To)[] DatabaseNamespaceSwaps =
    {
        ("System.Data.OracleClient", "Oracle.ManagedDataAccess.Client"),
        ("Oracle.DataAccess.Client", "Oracle.ManagedDataAccess.Client"),
        ("System.Data.SqlClient", "Microsoft.Data.SqlClient"),
    };

    private static void RecreateDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    private static bool PathConflictsWith(string target, string other)
    {
        var t = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var o = Path.GetFullPath(other).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(t, o, StringComparison.OrdinalIgnoreCase)) return true;
        var sep = Path.DirectorySeparatorChar;
        return t.StartsWith(o + sep, StringComparison.OrdinalIgnoreCase) || o.StartsWith(t + sep, StringComparison.OrdinalIgnoreCase);
    }
}
