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

        // Mantener juntas las clases parciales: si un fichero comparte un tipo con un fichero Windows,
        // tambien va a Windows (evita partir p. ej. Form1.cs + Form1.Designer.cs entre proyectos).
        var winExpanded = ExpandByPartialTypes(allCs, winRelFiles);

        var winFiles = allCs.Where(f => winExpanded.Contains(f.Rel)).ToList();
        var portableFiles = allCs.Where(f => !winExpanded.Contains(f.Rel)).ToList();

        RecreateDir(multiDir);
        RecreateDir(winDir);

        foreach (var f in portableFiles)
            CopyWithHeader(f.Abs, System.IO.Path.Combine(multiDir, f.Rel),
                $"[SCAFFOLD generado] Proyecto {multiName} (net8.0, multiplataforma). Revisar SPLIT-NOTES-{baseName}.md.");
        foreach (var f in winFiles)
            CopyWithHeader(f.Abs, System.IO.Path.Combine(winDir, f.Rel),
                $"[SCAFFOLD generado] Proyecto {winName} (net8.0-windows). Revisar SPLIT-NOTES-{baseName}.md.");

        var (packages, useWpf, useWinForms) = ReadCsproj(projectDir);

        WriteCsproj(System.IO.Path.Combine(multiDir, multiName + ".csproj"), "net8.0", packages, false, false, null);
        WriteCsproj(System.IO.Path.Combine(winDir, winName + ".csproj"), "net8.0-windows", packages, useWpf, useWinForms,
            $"..\\{multiName}\\{multiName}.csproj");

        var crossRefs = FindCrossReferences(portableFiles, winFiles);

        var manual = new List<string>();
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

    private static void CopyWithHeader(string source, string target, string header)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var content = File.ReadAllText(source);
        File.WriteAllText(target, $"// {header}{Environment.NewLine}{content}");
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

    /// <summary>Expande el conjunto de ficheros Windows para no separar clases parciales entre proyectos.</summary>
    private static HashSet<string> ExpandByPartialTypes(List<(string Abs, string Rel)> allCs, HashSet<string> winSet)
    {
        var typeFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var f in allCs)
        {
            try
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(f.Abs)).GetRoot();
                foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (!typeFiles.TryGetValue(t.Identifier.Text, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        typeFiles[t.Identifier.Text] = set;
                    }
                    set.Add(f.Rel);
                }
            }
            catch { /* ignorar */ }
        }

        var result = new HashSet<string>(winSet, StringComparer.OrdinalIgnoreCase);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var files in typeFiles.Values)
            {
                if (files.Count < 2 || !files.Any(result.Contains)) continue;
                foreach (var file in files)
                    if (result.Add(file)) changed = true;
            }
        }
        return result;
    }

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
