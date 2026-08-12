using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Genera, para un proyecto con rol <c>divisiblePorUI</c>, un scaffold de DOS proyectos en la carpeta de
/// salida: <c>&lt;Nombre&gt;Multi</c> (net8.0, multiplataforma, con los ficheros portables) y
/// <c>&lt;Nombre&gt;</c> (net8.0-windows, con los ficheros que usan APIs de Windows). Granularidad por
/// fichero: un <c>.cs</c> va entero a la parte Windows si usa alguna API de Windows (segun el analisis
/// de codigo), o a la parte multiplataforma en caso contrario. Es un punto de partida (scaffold): las
/// referencias cruzadas y lo no convertible se marcan en <c>SPLIT-NOTES.md</c> y en el informe.
/// </summary>
public sealed class ProjectSplitter
{
    private static readonly Regex PackageRef =
        new("<PackageReference\\s+[^>]*?/>|<PackageReference\\s+[^>]*?>.*?</PackageReference>",
            RegexOptions.Singleline | RegexOptions.Compiled);

    public SplitResult Split(string projectName, string projectDir, IReadOnlyList<SourceFinding> findings, string outputDir)
    {
        var baseName = projectName.Replace(" ", string.Empty);
        var multiName = baseName + "Multi";
        var winName = baseName;
        var multiDir = System.IO.Path.Combine(outputDir, multiName);
        var winDir = System.IO.Path.Combine(outputDir, winName);

        var winRelFiles = findings.Where(f => string.Equals(f.Project, projectName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allCs = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsObjBin(p, projectDir))
            .Select(p => (Abs: p, Rel: System.IO.Path.GetRelativePath(projectDir, p)))
            .ToList();

        // Propaga la marca Windows por clases PARCIALES (mismo tipo en varios ficheros) y por HERENCIA
        // (una clase que deriva de un tipo que quedo en Windows tambien va a Windows). Asi, p. ej., los
        // ViewModels que heredan de una base que usa System.Windows/Dispatcher no se quedan en Multi.
        var winExpanded = ExpandWindowsSet(allCs, winRelFiles);

        var winFiles = allCs.Where(f => winExpanded.Contains(f.Rel)).ToList();
        var portableFiles = allCs.Where(f => !winExpanded.Contains(f.Rel)).ToList();

        RecreateDir(multiDir);
        RecreateDir(winDir);

        // El proyecto Multi tiene su PROPIO namespace raiz (<root> -> <root>Multi), como pidio el cliente
        // (p. ej. ToolsCommon -> ToolsCommonMulti). El proyecto Windows conserva el namespace original.
        var rootNs = DetectRootNamespace(allCs);
        var multiRootNs = rootNs is null ? null : rootNs + "Multi";
        Func<string, string>? multiTransform =
            (rootNs is null || multiRootNs is null) ? null : c => RebaseNamespace(c, rootNs, multiRootNs);

        foreach (var f in portableFiles)
            CopyWithHeader(f.Abs, System.IO.Path.Combine(multiDir, f.Rel),
                $"[SCAFFOLD generado] Proyecto {multiName} (net8.0, multiplataforma). Namespace: {multiRootNs ?? multiName}. Revisar SPLIT-NOTES-{baseName}.md.",
                multiTransform);
        foreach (var f in winFiles)
            CopyWithHeader(f.Abs, System.IO.Path.Combine(winDir, f.Rel),
                $"[SCAFFOLD generado] Proyecto {winName} (net8.0-windows). Revisar SPLIT-NOTES-{baseName}.md.",
                null);

        var (packages, useWpf, useWinForms) = ReadCsproj(projectDir);

        WriteCsproj(System.IO.Path.Combine(multiDir, multiName + ".csproj"), "net8.0", packages, false, false, null);
        WriteCsproj(System.IO.Path.Combine(winDir, winName + ".csproj"), "net8.0-windows", packages, useWpf, useWinForms,
            $"..\\{multiName}\\{multiName}.csproj");

        // Para que el codigo Windows resuelva por nombre simple los tipos que se movieron al Multi (ahora
        // en <root>Multi...), se genera un GlobalUsings.cs en el proyecto Windows con esos namespaces.
        var movedNamespaces = rootNs is null ? new List<string>()
            : RebasedNamespacesOf(portableFiles, rootNs, multiRootNs!);
        if (movedNamespaces.Count > 0)
            WriteGlobalUsings(System.IO.Path.Combine(winDir, "GlobalUsings.cs"), movedNamespaces);

        var crossRefs = FindCrossReferences(portableFiles, winFiles);

        var manual = new List<string>();
        if (rootNs is not null)
            manual.Add($"Namespace separado: el proyecto {multiName} usa el namespace '{multiRootNs}' (el original '{rootNs}' se conserva en {winName}). Se ha generado GlobalUsings.cs en {winName} para resolver los tipos movidos; revisar referencias totalmente cualificadas que sigan usando '{rootNs}.'.");
        if (crossRefs.Count > 0)
            manual.Add($"{crossRefs.Count} referencia(s) de codigo portable a tipos que quedaron en {winName} (Windows): introducir una interfaz/abstraccion en {multiName} e implementarla en {winName}.");
        manual.Add($"Revisar las PackageReference de {multiName}: eliminar las que sean solo-Windows.");
        manual.Add("El scaffold es un punto de partida: compilar cada proyecto y resolver los errores de referencias que queden.");

        WriteSplitNotes(System.IO.Path.Combine(outputDir, $"SPLIT-NOTES-{baseName}.md"),
            projectName, multiName, winName, portableFiles.Count, winFiles.Count, crossRefs, manual);

        return new SplitResult
        {
            OriginalProject = projectName,
            MultiProject = multiName,
            WindowsProject = winName,
            OutputDir = outputDir,
            PortableFiles = portableFiles.Count,
            WindowsFiles = winFiles.Count,
            CrossReferences = crossRefs,
            ManualNotes = manual
        };
    }

    private static bool IsObjBin(string path, string root)
    {
        var rel = System.IO.Path.GetRelativePath(root, path);
        return rel.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static void RecreateDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    private static void CopyWithHeader(string source, string target, string header, Func<string, string>? transform)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var content = File.ReadAllText(source);
        if (transform is not null) content = transform(content);
        File.WriteAllText(target, $"// {header}{Environment.NewLine}{content}");
    }

    /// <summary>Namespace raiz mas frecuente entre los ficheros (primer segmento). Base para el rebase del Multi.</summary>
    private static string? DetectRootNamespace(List<(string Abs, string Rel)> allCs)
    {
        var roots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in allCs)
        {
            try
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(f.Abs)).GetRoot();
                foreach (var ns in DeclaredNamespaces(root))
                {
                    var first = ns.Split('.')[0];
                    roots[first] = roots.TryGetValue(first, out var n) ? n + 1 : 1;
                }
            }
            catch { /* ignorar */ }
        }
        return roots.Count == 0 ? null : roots.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    }

    private static IEnumerable<string> DeclaredNamespaces(SyntaxNode root)
    {
        foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            yield return ns.Name.ToString();
    }

    /// <summary>Reescribe el namespace raiz (declaraciones <c>namespace</c> y directivas <c>using</c>) de
    /// <paramref name="root"/> a <paramref name="rootMulti"/>, respetando limites de palabra.</summary>
    private static string RebaseNamespace(string content, string root, string rootMulti)
    {
        var esc = Regex.Escape(root);
        content = Regex.Replace(content, $@"(\bnamespace\s+){esc}\b", $"$1{rootMulti}");
        content = Regex.Replace(content, $@"(\busing\s+(?:static\s+)?){esc}\b", $"$1{rootMulti}");
        return content;
    }

    /// <summary>Namespaces (ya rebasados a <c>root</c>Multi) declarados en los ficheros portables movidos.</summary>
    private static List<string> RebasedNamespacesOf(List<(string Abs, string Rel)> portableFiles, string root, string rootMulti)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in portableFiles)
        {
            try
            {
                var node = CSharpSyntaxTree.ParseText(File.ReadAllText(f.Abs)).GetRoot();
                foreach (var ns in DeclaredNamespaces(node))
                {
                    if (ns.Equals(root, StringComparison.Ordinal))
                        result.Add(rootMulti);
                    else if (ns.StartsWith(root + ".", StringComparison.Ordinal))
                        result.Add(rootMulti + ns[root.Length..]);
                }
            }
            catch { /* ignorar */ }
        }
        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static void WriteGlobalUsings(string path, IReadOnlyList<string> namespaces)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [SCAFFOLD generado] Resuelve por nombre simple los tipos movidos al proyecto Multi.");
        foreach (var ns in namespaces) sb.AppendLine($"global using {ns};");
        File.WriteAllText(path, sb.ToString());
    }

    private static (IReadOnlyList<string> Packages, bool UseWpf, bool UseWinForms) ReadCsproj(string projectDir)
    {
        var csproj = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj is null) return (Array.Empty<string>(), false, false);

        var text = File.ReadAllText(csproj);
        var packages = PackageRef.Matches(text).Select(m => m.Value.Trim()).Distinct().ToList();
        var useWpf = Regex.IsMatch(text, "<UseWPF>\\s*true", RegexOptions.IgnoreCase) ||
                     text.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase);
        var useWinForms = Regex.IsMatch(text, "<UseWindowsForms>\\s*true", RegexOptions.IgnoreCase) ||
                          text.Contains("System.Windows.Forms", StringComparison.OrdinalIgnoreCase);
        return (packages, useWpf, useWinForms);
    }

    private static void WriteCsproj(string path, string tfm, IReadOnlyList<string> packages, bool useWpf, bool useWinForms, string? projectReference)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        if (useWpf) sb.AppendLine("    <UseWPF>true</UseWPF>");
        if (useWinForms) sb.AppendLine("    <UseWindowsForms>true</UseWindowsForms>");
        sb.AppendLine("  </PropertyGroup>");
        if (projectReference is not null)
        {
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine($"    <ProjectReference Include=\"{projectReference}\" />");
            sb.AppendLine("  </ItemGroup>");
        }
        if (packages.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var p in packages) sb.AppendLine($"    {p}");
            sb.AppendLine("  </ItemGroup>");
        }
        sb.AppendLine("</Project>");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Expande el conjunto de ficheros Windows para (1) no separar clases parciales entre proyectos
    /// y (2) arrastrar a Windows toda clase que herede de un tipo que quedo en Windows (herencia transitiva).
    /// Itera hasta punto fijo.</summary>
    private static HashSet<string> ExpandWindowsSet(List<(string Abs, string Rel)> allCs, HashSet<string> winSet)
    {
        // Parseo unico por fichero: tipos declarados, tipos base referenciados y mapa tipo -> ficheros.
        var declaredByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var basesByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var filesByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var f in allCs)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var bases = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(f.Abs)).GetRoot();
                foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    declared.Add(t.Identifier.Text);
                    if (!filesByType.TryGetValue(t.Identifier.Text, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        filesByType[t.Identifier.Text] = set;
                    }
                    set.Add(f.Rel);

                    if (t.BaseList is not null)
                        foreach (var bt in t.BaseList.Types)
                            bases.Add(BaseName(bt.Type));
                }
            }
            catch { /* ignorar ficheros ilegibles */ }
            declaredByFile[f.Rel] = declared;
            basesByFile[f.Rel] = bases;
        }

        var result = new HashSet<string>(winSet, StringComparer.OrdinalIgnoreCase);
        bool changed = true;
        while (changed)
        {
            changed = false;

            // (1) Clases parciales: un tipo declarado en varios ficheros mantiene todos juntos.
            foreach (var files in filesByType.Values)
            {
                if (files.Count < 2 || !files.Any(result.Contains)) continue;
                foreach (var file in files)
                    if (result.Add(file)) changed = true;
            }

            // (2) Herencia: tipos declarados en ficheros ya marcados Windows.
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

    /// <summary>Nombre simple del tipo base (sin genericos ni cualificacion): <c>A.B.Foo&lt;T&gt;</c> -&gt; <c>Foo</c>.</summary>
    private static string BaseName(TypeSyntax t) => t switch
    {
        SimpleNameSyntax s => s.Identifier.Text,          // Foo, Foo<T>
        QualifiedNameSyntax q => q.Right.Identifier.Text, // Namespace.Foo -> Foo
        _ => t.ToString()
    };

    private static IReadOnlyList<string> FindCrossReferences(
        List<(string Abs, string Rel)> portableFiles, List<(string Abs, string Rel)> winFiles)
    {
        var winTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in winFiles)
        {
            try
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(f.Abs)).GetRoot();
                foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    winTypes.Add(t.Identifier.Text);
            }
            catch { /* ignorar ficheros ilegibles */ }
        }
        if (winTypes.Count == 0) return Array.Empty<string>();

        var refs = new List<string>();
        foreach (var f in portableFiles)
        {
            try
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(f.Abs)).GetRoot();
                var declared = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
                var used = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Select(id => id.Identifier.Text)
                    .Where(n => winTypes.Contains(n) && !declared.Contains(n))
                    .Distinct();
                foreach (var n in used)
                    refs.Add($"{f.Rel} usa el tipo {n} (que quedo en la parte Windows)");
            }
            catch { /* ignorar */ }
        }
        return refs.Distinct().ToList();
    }

    private static void WriteSplitNotes(string path, string original, string multiName, string winName,
        int portable, int windows, IReadOnlyList<string> crossRefs, IReadOnlyList<string> manual)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# División de {original} (scaffold)");
        sb.AppendLine();
        sb.AppendLine($"- **{multiName}** (net8.0, multiplataforma): {portable} ficheros portables.");
        sb.AppendLine($"- **{winName}** (net8.0-windows): {windows} ficheros con dependencias de Windows.");
        sb.AppendLine();
        sb.AppendLine("> Es un punto de partida generado automáticamente por fichero. Revisar y ajustar.");
        sb.AppendLine();
        if (crossRefs.Count > 0)
        {
            sb.AppendLine("## Referencias cruzadas a resolver (introducir abstracción)");
            sb.AppendLine();
            foreach (var r in crossRefs) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }
        sb.AppendLine("## Acciones manuales pendientes");
        sb.AppendLine();
        foreach (var m in manual) sb.AppendLine($"- {m}");
        File.WriteAllText(path, sb.ToString());
    }
}
