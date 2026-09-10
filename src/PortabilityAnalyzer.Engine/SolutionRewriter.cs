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
/// <c>.sln</c>. Los namespaces se conservan (no se rebasan) para no romper las referencias entre
/// proyectos. El núcleo portable queda compilable en net8.0; lo específico de Windows queda aislado en
/// su proyecto net8.0-windows, listo para que otro equipo aporte la implementación de otros SO.
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
        // Propiedades del PropertyGroup original que hay que conservar para no romper la compilacion.
        public string? ImplicitUsings;
        public string? Nullable;
        public string? LangVersion;
        public string? RootNamespace;
        public List<string> RefNames = new();            // referencias a otros proyectos de la solución
        public List<(string AbsCsproj, bool Portable)> ExternalRefs = new(); // referencias a proyectos externos
        public List<(string Abs, string Rel)> WinCode = new();
        public List<(string Abs, string Rel)> PortableCode = new();
        public List<(string Abs, string Rel)> WinContent = new();
        public List<(string Abs, string Rel)> PortableContent = new();
        public bool HasEntryPoint;
        public Kind Kind;

        // Identidades de salida (rellenadas tras clasificar).
        public string? CoreName;      // net8.0
        public string? WinName;       // net8.0-windows
        public string? PortableName;  // proyecto único portable
    }

    public RewriteResult Rewrite(string solutionName, IReadOnlyList<(string Name, string Dir)> projects,
        IReadOnlyList<SourceFinding> findings, string outputDir)
    {
        var warnings = new List<string>();

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
                ImplicitUsings = Prop(text, "ImplicitUsings"),
                Nullable = Prop(text, "Nullable"),
                LangVersion = Prop(text, "LangVersion"),
                RootNamespace = Prop(text, "RootNamespace")
            };

            // Ficheros del proyecto (código y contenido), excluyendo obj/bin y el .csproj.
            var allFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !IsObjBin(p, dir) && !p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !IsVsJunk(p))
                .Select(p => (Abs: p, Rel: Path.GetRelativePath(dir, p)))
                .ToList();
            var codeFiles = allFiles.Where(f => f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
            var contentFiles = allFiles.Where(f => !f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

            // Semilla de ficheros Windows: los que tienen hallazgo + code-behind de XAML + punto de entrada.
            var winSeed = findings.Where(f => string.Equals(f.Project, name, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in codeFiles)
            {
                if (f.Rel.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) winSeed.Add(f.Rel);
                if (IsEntryPoint(f.Abs)) { winSeed.Add(f.Rel); info.HasEntryPoint = true; }
            }
            var winSet = ExpandWindowsSet(codeFiles, winSeed);

            info.WinCode = codeFiles.Where(f => winSet.Contains(f.Rel)).ToList();
            info.PortableCode = codeFiles.Where(f => !winSet.Contains(f.Rel)).ToList();
            var (winContent, portContent) = ClassifyContent(contentFiles, info.WinCode, info.PortableCode);
            info.WinContent = winContent;
            info.PortableContent = portContent;

            // Clasificación del proyecto.
            bool hasWin = info.WinCode.Count > 0 || info.UseWpf || info.UseWinForms;
            if (!hasWin) { info.Kind = Kind.Portable; }
            else if (info.PortableCode.Count > 0) { info.Kind = Kind.Separable; }
            else { info.Kind = Kind.WindowsOnly; }

            infos.Add(info);
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

        // 4) Preparar salida.
        RecreateDir(outputDir);
        var emitted = new List<(string Name, string RelCsproj)>();  // para el .sln
        var rewritten = new List<RewrittenProject>();
        var allSeams = new List<(string Project, string Concrete, string Interface)>();

        foreach (var info in infos)
        {
            switch (info.Kind)
            {
                case Kind.Portable:
                {
                    var outName = info.PortableName!;
                    var projDir = Path.Combine(outputDir, outName);
                    CopyCode(info.PortableCode.Concat(info.WinCode), info.Dir, projDir, outName);
                    CopyContent(info.PortableContent.Concat(info.WinContent), projDir);
                    var refs = info.RefNames.Select(CoreSideRef).Where(x => x is not null).Select(x => x!).ToList();
                    foreach (var rn in info.RefNames)
                        if (CoreSideRef(rn) is null && WinSideRef(rn) is not null)
                            warnings.Add($"'{outName}' (portable) referenciaba a '{rn}', que quedó solo-Windows: revisar (introducir un seam) o mantener este proyecto en net8.0-windows.");
                    var portableRelPaths = ProjRelPaths(refs).Concat(ExternalRelPaths(info.ExternalRefs, projDir, onlyPortable: false)).ToList();
                    WriteCsproj(Path.Combine(projDir, outName + ".csproj"), "net8.0", info.PackageLines, info.UseWpf, info.UseWinForms,
                        (IsExeType(info.OutputType) || info.HasEntryPoint) ? info.OutputType : null, refs, portableRelPaths, windowsOnlyFilter: false, source: info);
                    emitted.Add((outName, $"{outName}\\{outName}.csproj"));
                    rewritten.Add(new RewrittenProject(info.Name, "Portable",
                        info.UseWpf || info.UseWinForms ? "Sin hallazgos Windows" : "Sin dependencias de Windows",
                        new[] { outName }, info.PortableCode.Count + info.WinCode.Count, 0));
                    break;
                }
                case Kind.WindowsOnly:
                {
                    var outName = info.WinName!;
                    var projDir = Path.Combine(outputDir, outName);
                    CopyCode(info.PortableCode.Concat(info.WinCode), info.Dir, projDir, outName);
                    CopyContent(info.PortableContent.Concat(info.WinContent), projDir);
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

                    // Pase de seams: intenta mantener en el núcleo los ficheros portables que dependen de
                    // clases Windows, extrayendo una interfaz e inyectándola por constructor.
                    var seam = RunSeamPass(info);
                    warnings.AddRange(seam.Warnings);
                    foreach (var s in seam.Seams) allSeams.Add((info.Name, s.Concrete, s.Interface));

                    // NÚCLEO (net8.0): ficheros portables (con las reescrituras de inyección) + interfaces de seam.
                    CopyCode(seam.Portable, info.Dir, coreDir, coreName, seam.CoreOverrides);
                    CopyContent(info.PortableContent, coreDir);
                    WriteGenerated(seam.CoreExtraFiles, coreDir);
                    var coreRefs = info.RefNames.Select(CoreSideRef).Where(x => x is not null).Select(x => x!).ToList();
                    foreach (var rn in info.RefNames)
                        if (CoreSideRef(rn) is null)
                            warnings.Add($"El núcleo '{coreName}' referenciaba a '{rn}', que quedó solo-Windows: introducir un seam (interfaz) en el núcleo o mover el uso a '{winName}'.");
                    var coreRelPaths = ProjRelPaths(coreRefs).Concat(ExternalRelPaths(info.ExternalRefs, coreDir, onlyPortable: true)).ToList();
                    WriteCsproj(Path.Combine(coreDir, coreName + ".csproj"), "net8.0", info.PackageLines, false, false,
                        null, coreRefs, coreRelPaths, windowsOnlyFilter: true, source: info);
                    emitted.Add((coreName, $"{coreName}\\{coreName}.csproj"));

                    // WINDOWS (net8.0-windows): ficheros Windows (algunos implementan ya la interfaz de seam)
                    // + referencia a su propio núcleo.
                    CopyCode(seam.Win, info.Dir, winDir, winName, seam.WinOverrides);
                    CopyContent(info.WinContent, winDir);
                    var winRefs = new List<string> { coreName };
                    winRefs.AddRange(info.RefNames.Select(WinSideRef).Where(x => x is not null).Select(x => x!));
                    var winRelPaths = new List<string> { $"..\\{coreName}\\{coreName}.csproj" };
                    winRelPaths.AddRange(ProjRelPaths(info.RefNames.Select(WinSideRef).Where(x => x is not null).Select(x => x!).ToList()));
                    winRelPaths.AddRange(ExternalRelPaths(info.ExternalRefs, winDir, onlyPortable: false));
                    WriteCsproj(Path.Combine(winDir, winName + ".csproj"), "net8.0-windows", info.PackageLines, info.UseWpf, info.UseWinForms,
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

        // 5) Generar el .sln y el README de migración.
        var slnPath = Path.Combine(outputDir, solutionName + ".sln");
        WriteSolution(slnPath, emitted);
        WriteMigrationReadme(Path.Combine(outputDir, "MIGRACION.md"), solutionName, rewritten, warnings, allSeams);

        return new RewriteResult
        {
            OutputDir = outputDir,
            SolutionFile = slnPath,
            Projects = rewritten,
            Warnings = warnings
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Copia de ficheros
    // ---------------------------------------------------------------------------------------------

    private void CopyCode(IEnumerable<(string Abs, string Rel)> files, string srcDir, string outDir, string outProject,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        foreach (var f in files)
        {
            var target = Path.Combine(outDir, f.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var content = overrides is not null && overrides.TryGetValue(f.Rel, out var oc) ? oc : ReadTextCached(f.Abs);
            var header = $"// [Reescritura multiplataforma] Proyecto {outProject}. Namespace conservado del original.";
            File.WriteAllText(target, header + Environment.NewLine + content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
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
        public List<(string Concrete, string Interface)> Seams = new();
        public List<string> Warnings = new();
    }

    private SeamPassResult RunSeamPass(ProjInfo info)
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

        foreach (var f in info.PortableCode)
        {
            var root = RootCached(f.Abs);
            if (root is null) continue;
            var content = ReadTextCached(f.Abs);

            var declaredHere = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
            var used = root.DescendantNodes().OfType<IdentifierNameSyntax>().Select(id => id.Identifier.Text)
                .Where(n => winTypeToFile.ContainsKey(n) && !declaredHere.Contains(n)).Distinct().ToList();
            if (used.Count == 0) continue; // sin referencia cruzada: se queda portable tal cual

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            bool allExtractable = true;
            foreach (var t in used) { var pl = PlanFor(t); if (pl is null) { allExtractable = false; break; } map[t] = pl.InterfaceName; }
            var rewritten = allExtractable ? SeamWeaver.TryInjectConstructor(content, map) : null;

            if (rewritten is null)
            {
                // No inyectable de forma segura: mover el fichero al proyecto Windows (fallback) y avisar.
                res.Portable.RemoveAll(x => string.Equals(x.Rel, f.Rel, StringComparison.OrdinalIgnoreCase));
                if (!res.Win.Any(x => string.Equals(x.Rel, f.Rel, StringComparison.OrdinalIgnoreCase))) res.Win.Add(f);
                res.Warnings.Add($"'{info.Name}': '{f.Rel}' usa tipos Windows ({string.Join(", ", used)}) de forma no inyectable automáticamente; se movió a {info.WinName}. Revisar para introducir un seam a mano.");
                continue;
            }

            res.CoreOverrides[f.Rel] = rewritten;
            foreach (var t in used)
            {
                if (res.Seams.Any(s => s.Concrete == t)) continue;
                var pl = PlanFor(t)!;
                res.Seams.Add((t, pl.InterfaceName));
                res.CoreExtraFiles.Add(($"{pl.InterfaceName}.cs", pl.InterfaceSource));
                var wf = winTypeToFile[t];
                var baseContent = res.WinOverrides.TryGetValue(wf.Rel, out var oc) ? oc : File.ReadAllText(wf.Abs);
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

    /// <summary>GUID estable derivado del nombre (para que el .sln sea reproducible).</summary>
    private static string DeterministicGuid(string name)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("PortabilityAnalyzer.Rewrite:" + name));
        return new Guid(hash).ToString();
    }

    private static void WriteMigrationReadme(string path, string solutionName, IReadOnlyList<RewrittenProject> projects,
        IReadOnlyList<string> warnings, IReadOnlyList<(string Project, string Concrete, string Interface)> seams)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {solutionName} — solución reescrita a multiplataforma");
        sb.AppendLine();
        sb.AppendLine("> Reescritura **portable-first**: el núcleo de cada proyecto queda en `net8.0` (compila en cualquier SO) y");
        sb.AppendLine("> lo específico de Windows se aísla en un proyecto `net8.0-windows`. La solución original NO se ha tocado.");
        sb.AppendLine();
        sb.AppendLine("## Proyectos generados");
        sb.AppendLine();
        sb.AppendLine("| Proyecto original | Clasificación | Proyectos generados | Portables | Windows |");
        sb.AppendLine("|---|---|---|---:|---:|");
        foreach (var p in projects)
            sb.AppendLine($"| {p.OriginalProject} | {p.Kind} | {string.Join(" + ", p.OutputProjects)} | {p.PortableFiles} | {p.WindowsFiles} |");
        sb.AppendLine();
        sb.AppendLine("**Clasificación:** *Portable* = sin dependencias de Windows (retargeteado a net8.0). ");
        sb.AppendLine("*Separable* = dividido en `X.Core` (net8.0, portable) + `X.Windows` (net8.0-windows). ");
        sb.AppendLine("*SoloWindows* = todo el proyecto depende de Windows (típicamente la UI/punto de entrada).");
        sb.AppendLine();
        sb.AppendLine("## Cómo se han recableado las referencias");
        sb.AppendLine("- Un proyecto **`.Core`/portable** solo referencia núcleos portables (`*.Core` o proyectos portables).");
        sb.AppendLine("- Un proyecto **`.Windows`** referencia su propio `.Core` y las partes `.Windows` de sus dependencias.");
        sb.AppendLine("- Los **paquetes NuGet solo-Windows** (EventLog, Registry, ProtectedData…) se han dejado únicamente en los proyectos `.Windows`.");
        sb.AppendLine();
        if (seams.Count > 0)
        {
            sb.AppendLine("## Seams extraídos automáticamente (interfaz en el núcleo ↔ implementación Windows)");
            sb.AppendLine();
            sb.AppendLine("Estos ficheros del núcleo dependían de una clase de Windows. Se extrajo una interfaz al núcleo,");
            sb.AppendLine("la clase Windows ahora la implementa, y el consumidor recibe la interfaz por **inyección de dependencias**.");
            sb.AppendLine("Falta **registrar** cada implementación en el arranque de la aplicación (proyecto `.Windows`):");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine("// Ejemplo con Microsoft.Extensions.DependencyInjection (añade el paquete si no lo tienes):");
            foreach (var s in seams)
                sb.AppendLine($"services.AddSingleton<{s.Interface}, {s.Concrete}>();   // proyecto {s.Project}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("La implementación para otros SO (Linux…) se añade creando otra clase que implemente la misma");
            sb.AppendLine("interfaz y registrándola en su lugar. El núcleo no cambia.");
            sb.AppendLine();
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine("## Avisos que requieren intervención manual");
            foreach (var w in warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }
        sb.AppendLine("## Pasos siguientes");
        sb.AppendLine($"1. Abre `{solutionName}.sln` y **compila**. Los núcleos `net8.0` deben compilar en cualquier SO.");
        sb.AppendLine("2. Donde un núcleo necesite una capacidad de Windows, introduce una **interfaz (seam)** en el núcleo e");
        sb.AppendLine("   impleméntala en el proyecto `.Windows` (inyección de dependencias). La implementación para otros SO");
        sb.AppendLine("   queda preparada para otro equipo (no se desarrolla ni se prescribe aquí).");
        sb.AppendLine("3. Verifica que el código Windows (WPF/Registro/P-Invoke…) sigue compilando en `net8.0-windows`.");
        sb.AppendLine("4. Añade pruebas que compilen el núcleo portable en un CI multiplataforma (matriz Windows + Linux).");
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
