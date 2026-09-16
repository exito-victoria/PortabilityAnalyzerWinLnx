using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Serilog;

namespace PortabilityAnalyzer.Engine;

/// <summary>Una API no portable (solo-Windows) descubierta en el código, candidata a REGLA nueva.</summary>
public sealed record RuleCandidate(
    string PatternKind,        // "type" | "apiCall"
    string Value,              // nombre completo del tipo, o "Tipo.Miembro"
    string MatchMode,          // "equals" | "contains"
    string Categoria,          // categoría del catálogo (enum)
    string TypeFullName,
    string? Member,
    string Namespace,
    string SampleFile,
    int SampleLine,
    string Platforms);         // resumen de SupportedOSPlatform/Unsupported para la descripción

public sealed record RuleDiscoveryResult(
    IReadOnlyList<RuleCandidate> Candidates, int ReferenceCount, int Projects, int FilesParsed, bool RefPacksFound);

/// <summary>
/// DESCUBRIMIENTO SEMÁNTICO de reglas de portabilidad. Compila el código con Roslyn + los ensamblados de
/// referencia de .NET 8 (packs NETCore.App.Ref y WindowsDesktop.App.Ref) y localiza los usos de APIs
/// anotadas como solo-Windows (<c>[SupportedOSPlatform("windows")]</c> / <c>[UnsupportedOSPlatform("linux")]</c>),
/// que es la misma señal que usa el analizador oficial CA1416. Cada API no cubierta por el catálogo se
/// propone como una regla nueva (a completar/revisar).
/// </summary>
public sealed class RuleDiscovery
{
    public RuleDiscoveryResult Discover(IReadOnlyList<(string Name, string Dir)> projects, ILogger? log = null)
    {
        var references = BuildReferences(out var refPacksFound, log);
        var byKey = new Dictionary<string, RuleCandidate>(StringComparer.Ordinal);
        int filesParsed = 0;

        var parse = new CSharpParseOptions(LanguageVersion.Latest,
            preprocessorSymbols: new[] { "WINDOWS", "NET", "NET8_0", "NET8_0_OR_GREATER", "RELEASE" });

        foreach (var (name, dir) in projects)
        {
            if (!Directory.Exists(dir)) continue;
            var csFiles = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsObjBin(p, dir))
                .ToList();
            if (csFiles.Count == 0) continue;

            var trees = new List<SyntaxTree>();
            foreach (var f in csFiles)
            {
                try { trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f)), parse, path: f)); filesParsed++; }
                catch { /* ignorar fichero ilegible */ }
            }

            var comp = CSharpCompilation.Create("disc_" + name, trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            foreach (var tree in trees)
            {
                SemanticModel model;
                try { model = comp.GetSemanticModel(tree); } catch { continue; }
                var root = tree.GetRoot();
                foreach (var node in root.DescendantNodes())
                {
                    ISymbol? sym = node switch
                    {
                        InvocationExpressionSyntax or ObjectCreationExpressionSyntax or MemberAccessExpressionSyntax
                            => model.GetSymbolInfo(node).Symbol,
                        IdentifierNameSyntax or GenericNameSyntax
                            => model.GetSymbolInfo(node).Symbol is INamedTypeSymbol t ? t : null,
                        _ => null
                    };
                    if (sym is null) continue;
                    sym = sym.OriginalDefinition;
                    // Solo APIs de biblioteca (metadatos), no el código propio.
                    if (sym.DeclaringSyntaxReferences.Length > 0) continue;

                    var (win, memberLevel, plats) = PlatformCheck(sym);
                    if (!win) continue;

                    var type = sym as INamedTypeSymbol ?? sym.ContainingType;
                    if (type is null) continue;
                    var typeFull = type.ToDisplayString();
                    var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                    var categoria = MapCategory(ns, type.Name);

                    string kind, value, matchMode; string? member = null;
                    if (memberLevel && sym is not INamedTypeSymbol)
                    {
                        member = sym.Name;
                        kind = "apiCall"; value = $"{type.Name}.{member}"; matchMode = "contains";
                    }
                    else { kind = "type"; value = typeFull; matchMode = "equals"; }

                    var key = kind + "|" + value;
                    if (byKey.ContainsKey(key)) continue;
                    var span = node.GetLocation().GetLineSpan();
                    byKey[key] = new RuleCandidate(kind, value, matchMode, categoria, typeFull, member, ns,
                        span.Path, span.StartLinePosition.Line + 1, plats);
                }
            }
        }

        var candidates = byKey.Values.OrderBy(c => c.Categoria).ThenBy(c => c.Value, StringComparer.Ordinal).ToList();
        log?.Information("Descubrimiento: {Refs} ensamblados de referencia (packs {Found}), {Files} ficheros, {Cand} APIs solo-Windows candidatas.",
            references.Count, refPacksFound ? "OK" : "NO ENCONTRADOS", filesParsed, candidates.Count);
        return new RuleDiscoveryResult(candidates, references.Count, projects.Count, filesParsed, refPacksFound);
    }

    // ---- Comprobación de plataforma (SupportedOSPlatform / UnsupportedOSPlatform) ----

    private static (bool Win, bool MemberLevel, string Plats) PlatformCheck(ISymbol sym)
    {
        var (msup, muns) = PlatAttrs(sym);
        var type = sym as INamedTypeSymbol ?? sym.ContainingType;
        var (tsup, tuns) = type is not null ? PlatAttrs(type) : (new List<string>(), new List<string>());

        if (IsWindowsOnly(msup, muns)) return (true, true, Summarize(msup, muns));
        if (IsWindowsOnly(tsup, tuns)) return (true, false, Summarize(tsup, tuns));
        return (false, false, string.Empty);
    }

    private static (List<string> Sup, List<string> Uns) PlatAttrs(ISymbol sym)
    {
        var sup = new List<string>(); var uns = new List<string>();
        foreach (var a in sym.GetAttributes())
        {
            var n = a.AttributeClass?.Name;
            if (n != "SupportedOSPlatformAttribute" && n != "UnsupportedOSPlatformAttribute") continue;
            if (a.ConstructorArguments.Length == 0) continue;
            var ca = a.ConstructorArguments[0];
            if (ca.Kind != TypedConstantKind.Primitive) continue;   // evitar args de tipo array
            if (ca.Value is not string arg || string.IsNullOrEmpty(arg)) continue;
            if (n == "SupportedOSPlatformAttribute") sup.Add(arg);
            else uns.Add(arg);
        }
        return (sup, uns);
    }

    private static bool IsWindowsOnly(List<string> sup, List<string> uns)
    {
        if (uns.Any(p => BasePlatform(p) == "linux")) return true;
        if (sup.Count > 0 && sup.Any(p => BasePlatform(p) == "windows") && !sup.Any(p => BasePlatform(p) == "linux")) return true;
        return false;
    }

    /// <summary>Nombre base de la plataforma sin versión: "windows7.0" -> "windows".</summary>
    private static string BasePlatform(string p)
    {
        int i = 0; while (i < p.Length && char.IsLetter(p[i])) i++;
        return p[..i].ToLowerInvariant();
    }

    private static string Summarize(List<string> sup, List<string> uns)
    {
        var parts = new List<string>();
        if (sup.Count > 0) parts.Add("Supported: " + string.Join(",", sup));
        if (uns.Count > 0) parts.Add("Unsupported: " + string.Join(",", uns));
        return string.Join("; ", parts);
    }

    private static string MapCategory(string ns, string typeName)
    {
        if (ns.StartsWith("Microsoft.Win32", StringComparison.Ordinal)) return "Registry";
        if (ns.StartsWith("System.Security.Cryptography", StringComparison.Ordinal)) return "Cryptography";
        if (ns.Contains("Principal", StringComparison.Ordinal) || typeName.StartsWith("Windows", StringComparison.Ordinal)) return "Identity";
        if (ns.StartsWith("System.Windows", StringComparison.Ordinal)) return "AssemblyReference";
        if (ns.StartsWith("System.Threading", StringComparison.Ordinal)) return "Threading";
        if (ns.StartsWith("System.IO", StringComparison.Ordinal)) return "FileSystem";
        if (ns.StartsWith("System.Diagnostics", StringComparison.Ordinal) && typeName.Contains("Process", StringComparison.Ordinal)) return "ProcessInvocation";
        return "PlatformAttribute";
    }

    // ---- Ensamblados de referencia (packs de .NET 8) + bin de los proyectos ----

    private static List<MetadataReference> BuildReferences(out bool refPacksFound, ILogger? log)
    {
        var refs = new List<MetadataReference>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        refPacksFound = false;
        var root = DotnetRoot();
        if (root is not null)
        {
            foreach (var pack in new[] { "Microsoft.NETCore.App.Ref", "Microsoft.WindowsDesktop.App.Ref" })
            {
                var refDir = HighestRefDir(Path.Combine(root, "packs", pack));
                if (refDir is null) continue;
                refPacksFound = true;
                foreach (var dll in Directory.EnumerateFiles(refDir, "*.dll"))
                    TryAdd(refs, added, dll);
            }
        }
        if (!refPacksFound)
            log?.Warning("No se encontraron los packs de referencia de .NET 8 (Microsoft.NETCore.App.Ref / WindowsDesktop.App.Ref). " +
                         "El descubrimiento semántico será limitado. Instala el SDK de .NET 8 o define DOTNET_ROOT.");
        return refs;
    }

    private static void TryAdd(List<MetadataReference> refs, HashSet<string> added, string dll)
    {
        var simple = Path.GetFileNameWithoutExtension(dll);
        if (!added.Add(simple)) return;
        try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { added.Remove(simple); }
    }

    private static string? DotnetRoot()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        foreach (var c in new[] { @"C:\Program Files\dotnet", @"C:\Program Files (x86)\dotnet", "/usr/share/dotnet", "/usr/lib/dotnet" })
            if (Directory.Exists(c)) return c;
        return null;
    }

    private static string? HighestRefDir(string packDir)
    {
        if (!Directory.Exists(packDir)) return null;
        var best = Directory.EnumerateDirectories(packDir)
            .Select(d => (Dir: d, Name: Path.GetFileName(d)))
            .Where(x => x.Name.StartsWith("8.", StringComparison.Ordinal))
            .Select(x => (x.Dir, Ver: ParseVersion(x.Name)))
            .OrderByDescending(x => x.Ver)
            .Select(x => x.Dir)
            .FirstOrDefault();
        if (best is null) return null;
        var refNet = Path.Combine(best, "ref", "net8.0");
        return Directory.Exists(refNet) ? refNet : null;
    }

    private static Version ParseVersion(string name)
    {
        var core = new string(name.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(core, out var v) ? v : new Version(0, 0);
    }

    private static bool IsObjBin(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }
}
