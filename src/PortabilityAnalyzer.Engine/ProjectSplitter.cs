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

        // Todos los ficheros del proyecto (codigo y CONTENIDO: xaml/resx/imagenes/config...), excluyendo
        // obj/bin y el propio .csproj (se regenera). Antes solo se copiaban los .cs, con lo que un WPF
        // quedaba roto (el .xaml.cs sin su .xaml). Ahora se copia y clasifica tambien el contenido.
        var allFiles = Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories)
            .Where(p => !IsObjBin(p, projectDir))
            .Where(p => !p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(p => !IsVsJunk(p))
            .Select(p => (Abs: p, Rel: System.IO.Path.GetRelativePath(projectDir, p)))
            .ToList();

        var codeFiles = allFiles.Where(f => f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var contentFiles = allFiles.Where(f => !f.Rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

        // El code-behind de un XAML (*.xaml.cs) va SIEMPRE a Windows (WPF): se anade a la semilla.
        foreach (var f in codeFiles)
            if (f.Rel.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
                winRelFiles.Add(f.Rel);

        // El PUNTO DE ENTRADA (fichero con Main o con top-level statements) va al proyecto Windows, que es
        // el que conserva el OutputType ejecutable; asi el exe no queda sin Main (CS5001).
        foreach (var f in codeFiles)
            if (IsEntryPoint(f.Abs))
                winRelFiles.Add(f.Rel);

        // Propaga la marca Windows por clases PARCIALES (mismo tipo en varios ficheros) y por HERENCIA
        // (una clase que deriva de un tipo que quedo en Windows tambien va a Windows). Asi, p. ej., los
        // ViewModels que heredan de una base que usa System.Windows/Dispatcher no se quedan en Multi.
        var winExpanded = ExpandWindowsSet(codeFiles, winRelFiles);

        var winFiles = codeFiles.Where(f => winExpanded.Contains(f.Rel)).ToList();
        var portableFiles = codeFiles.Where(f => !winExpanded.Contains(f.Rel)).ToList();

        // Clasificar el contenido: el XAML es Windows; el resto sigue al .cs de su mismo nombre (p. ej.
        // Form1.resx con Form1.cs) y, si es huerfano, se decide por extension (UI -> Windows; resto -> Multi).
        var (winContent, multiContent) = ClassifyContent(contentFiles, winFiles, portableFiles);
        var hasXaml = winContent.Any(f => f.Rel.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));

        // SALVAGUARDA: nunca recrear (borrar) una carpeta de salida que colisione con el proyecto original
        // (evita perder el codigo fuente si --output apunta dentro de la solucion).
        if (PathConflictsWith(multiDir, projectDir) || PathConflictsWith(winDir, projectDir))
            throw new InvalidOperationException(
                $"La carpeta de salida del split colisiona con el proyecto original '{projectDir}'. " +
                "Elige un --output fuera de la carpeta de la solucion para no arriesgar el codigo fuente.");

        RecreateDir(multiDir);
        RecreateDir(winDir);

        // El proyecto Multi tiene su PROPIO namespace raiz (<root> -> <root>Multi), como pidio el cliente
        // (p. ej. ToolsCommon -> ToolsCommonMulti). El proyecto Windows conserva el namespace original.
        var rootNs = DetectRootNamespace(codeFiles);
        var multiRootNs = rootNs is null ? null : rootNs + "Multi";
        Func<string, string>? multiTransform =
            (rootNs is null || multiRootNs is null) ? null : c => RebaseNamespace(c, rootNs, multiRootNs);

        foreach (var f in portableFiles)
            CopyWithHeader(f.Abs, System.IO.Path.Combine(multiDir, f.Rel),
                $"[SCAFFOLD generado] Proyecto {multiName} (net8.0, multiplataforma). Namespace: {multiRootNs ?? multiName}. Revisar SPLIT-NOTES-{baseName}.md.",
                multiTransform);
        // En el proyecto Windows, aislar los METODOS que usan API exclusiva de Windows con #if WINDOWS y
        // dejar el hueco no-Windows indicado (#else con stub). Asi la separacion es a nivel de metodo.
        var projFindings = findings.Where(f => string.Equals(f.Project, projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var f in winFiles)
        {
            var fileLines = projFindings
                .Where(x => string.Equals(x.File, f.Rel, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Line)
                .ToHashSet();
            Func<string, string>? winTransform = fileLines.Count > 0 ? c => IsolateWindowsMethods(c, fileLines) : null;
            CopyWithHeader(f.Abs, System.IO.Path.Combine(winDir, f.Rel),
                $"[SCAFFOLD generado] Proyecto {winName} (net8.0-windows). Metodos Windows aislados con #if WINDOWS. Revisar SPLIT-NOTES-{baseName}.md.",
                winTransform);
        }

        // El contenido (xaml/resx/recursos) se copia VERBATIM (sin cabecera // ni rebase de namespace).
        foreach (var f in multiContent) CopyRaw(f.Abs, System.IO.Path.Combine(multiDir, f.Rel));
        foreach (var f in winContent) CopyRaw(f.Abs, System.IO.Path.Combine(winDir, f.Rel));

        var (packages, useWpf, useWinForms, outputType) = ReadCsproj(projectDir);
        if (hasXaml) useWpf = true; // si hay XAML en la parte Windows, el proyecto es WPF.

        // El proyecto Windows conserva el OutputType original (exe/servicio); si tiene App.xaml
        // (ApplicationDefinition de WPF) debe ser ejecutable (WinExe), no biblioteca. El Multi es biblioteca.
        var hasAppXaml = winContent.Any(f => System.IO.Path.GetFileName(f.Rel).Equals("App.xaml", StringComparison.OrdinalIgnoreCase));
        var winOutputType = hasAppXaml && !(outputType?.Contains("Exe", StringComparison.OrdinalIgnoreCase) ?? false)
            ? "WinExe" : outputType;

        // SCAFFOLDING DE SEAMS: por cada categoria de API Windows detectada en el proyecto se genera una
        // interfaz portable (en el Multi) y su implementacion Windows real (en el proyecto Windows), con el
        // paquete NuGet necesario. Deja el cambio ESTRUCTURADO y compilable, a falta de conectarlo al codigo.
        var seamMultiNs = (multiRootNs ?? multiName) + ".Seams";
        var seamWinNs = winName + ".Seams";
        var categorias = findings
            .Where(f => string.Equals(f.Project, projectName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Categoria)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var generatedSeams = new List<SeamSpec>();
        var seamPackages = new List<string>();
        foreach (var cat in categorias)
        {
            if (!Seams.TryGetValue(cat, out var spec)) continue;
            WriteCsFile(System.IO.Path.Combine(multiDir, "Seams", $"{spec.Interfaz}.cs"), BuildSeamInterface(spec, seamMultiNs));
            WriteCsFile(System.IO.Path.Combine(winDir, "Seams", $"{spec.ImplClase}.cs"), BuildSeamWindowsImpl(spec, seamMultiNs, seamWinNs, winName));
            generatedSeams.Add(spec);
            if (spec.Package is not null && !seamPackages.Contains(spec.Package)) seamPackages.Add(spec.Package);
        }
        // Ejemplo de aislamiento por SO EN EL PROPIO CODIGO (compilacion condicional), ya implementado.
        WriteCsFile(System.IO.Path.Combine(winDir, "Portabilidad", "EjemploPorSistemaOperativo.cs"), BuildConditionalExample(seamWinNs));

        var multiHasAsmInfo = portableFiles.Any(f => IsAssemblyInfo(f.Rel));
        var winHasAsmInfo = winFiles.Any(f => IsAssemblyInfo(f.Rel));
        var winPackages = packages.Concat(seamPackages).ToList();

        WriteCsproj(System.IO.Path.Combine(multiDir, multiName + ".csproj"), "net8.0", packages, false, false, null,
            outputType: null, generateAssemblyInfo: !multiHasAsmInfo);
        WriteCsproj(System.IO.Path.Combine(winDir, winName + ".csproj"), "net8.0-windows", winPackages, useWpf, useWinForms,
            $"..\\{multiName}\\{multiName}.csproj", winOutputType, generateAssemblyInfo: !winHasAsmInfo);

        // Para que el codigo Windows resuelva por nombre simple los tipos movidos al Multi y los seams,
        // se genera un GlobalUsings.cs en el proyecto Windows con esos namespaces.
        var movedNamespaces = rootNs is null ? new List<string>()
            : RebasedNamespacesOf(portableFiles, rootNs, multiRootNs!);
        if (generatedSeams.Count > 0 && !movedNamespaces.Contains(seamMultiNs)) movedNamespaces.Add(seamMultiNs);
        if (movedNamespaces.Count > 0)
            WriteGlobalUsings(System.IO.Path.Combine(winDir, "GlobalUsings.cs"), movedNamespaces);

        var crossRefs = FindCrossReferences(portableFiles, winFiles);

        var manual = new List<string>();
        if (rootNs is not null)
            manual.Add($"Namespace separado: el proyecto {multiName} usa el namespace '{multiRootNs}' (el original '{rootNs}' se conserva en {winName}). Se ha generado GlobalUsings.cs en {winName} para resolver los tipos movidos; revisar referencias totalmente cualificadas que sigan usando '{rootNs}.'.");
        if (crossRefs.Count > 0)
            manual.Add($"{crossRefs.Count} referencia(s) de codigo portable a tipos que quedaron en {winName} (Windows): introducir una interfaz/abstraccion en {multiName} e implementarla en {winName}.");
        if (winContent.Count > 0 || multiContent.Count > 0)
            manual.Add($"Contenido copiado (xaml/resx/recursos): {winContent.Count} a {winName} y {multiContent.Count} a {multiName}. El XAML y su code-behind van juntos a {winName}; revisar los recursos huerfanos clasificados por extension.");
        if (winContent.Concat(multiContent).Any(f => f.Rel.EndsWith(".settings", StringComparison.OrdinalIgnoreCase)))
            manual.Add("Se detecto Properties/Settings (proyecto clasico): el Settings.Designer.cs requiere el paquete System.Configuration.ConfigurationManager; anadirlo o migrar la configuracion a IConfiguration (appsettings.json).");
        manual.Add($"Separacion por metodo: en {winName} los metodos que usan API de Windows se han envuelto en #if WINDOWS con un stub #else (hueco no-Windows). Completar la rama #else para Linux u otros SO, o extraer el metodo tras un seam.");
        manual.Add($"Revisar las PackageReference de {multiName}: eliminar las que sean solo-Windows.");
        manual.Add($"Si el proyecto original referenciaba a OTROS proyectos de la solucion (ProjectReference), anadir esas referencias a {multiName}/{winName} segun donde encaje cada uso (el scaffold solo enlaza {winName} -> {multiName}).");
        manual.Add("El scaffold es un punto de partida: compilar cada proyecto y resolver los errores de referencias que queden.");

        WriteSplitNotes(System.IO.Path.Combine(outputDir, $"SPLIT-NOTES-{baseName}.md"),
            projectName, multiName, winName, portableFiles.Count, winFiles.Count, crossRefs, manual,
            generatedSeams, seamMultiNs, seamWinNs);

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

    /// <summary>True si <paramref name="target"/> es la misma carpeta que <paramref name="other"/>, o una
    /// esta contenida en la otra. Se usa para no borrar/escribir sobre el codigo fuente original.</summary>
    private static bool PathConflictsWith(string target, string other)
    {
        var t = System.IO.Path.GetFullPath(target).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var o = System.IO.Path.GetFullPath(other).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (string.Equals(t, o, StringComparison.OrdinalIgnoreCase)) return true;
        var sep = System.IO.Path.DirectorySeparatorChar;
        return t.StartsWith(o + sep, StringComparison.OrdinalIgnoreCase)
            || o.StartsWith(t + sep, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyWithHeader(string source, string target, string header, Func<string, string>? transform)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var content = File.ReadAllText(source);
        if (transform is not null) content = transform(content);
        // UTF-8 con BOM: no degradar los acentos del fichero original al copiarlo/transformarlo.
        File.WriteAllText(target, $"// {header}{Environment.NewLine}{content}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Aisla los METODOS que usan API de Windows: envuelve su cuerpo en <c>#if WINDOWS ... #else
    /// (stub no-Windows) ... #endif</c>. Un metodo se considera Windows si alguna linea con hallazgo cae
    /// dentro de su rango. Solo toca metodos (no propiedades/constructores) y ante cualquier fallo de
    /// parseo deja el fichero intacto.</summary>
    private static string IsolateWindowsMethods(string content, HashSet<int> findingLines)
    {
        try
        {
            var root = CSharpSyntaxTree.ParseText(content).GetRoot();
            var targets = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => (m.Body is not null || m.ExpressionBody is not null) && MethodSpanHasLine(m, findingLines))
                .ToList();
            if (targets.Count == 0) return content;
            var newRoot = root.ReplaceNodes(targets, (orig, _) => WrapWindowsMethod(orig));
            return newRoot.ToFullString();
        }
        catch { return content; }
    }

    private static bool MethodSpanHasLine(MethodDeclarationSyntax m, HashSet<int> lines)
    {
        var span = m.GetLocation().GetLineSpan();
        int start = span.StartLinePosition.Line + 1, end = span.EndLinePosition.Line + 1;
        foreach (var l in lines) if (l >= start && l <= end) return true;
        return false;
    }

    private static MethodDeclarationSyntax WrapWindowsMethod(MethodDeclarationSyntax m)
    {
        string inner;
        if (m.Body is not null)
            inner = string.Concat(m.Body.Statements.Select(s => s.ToFullString()));
        else
        {
            var expr = m.ExpressionBody!.Expression.ToFullString().Trim();
            var isVoid = m.ReturnType is PredefinedTypeSyntax pts && pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
            inner = isVoid ? expr + ";" : "return " + expr + ";";
        }

        var name = m.Identifier.Text;
        var text =
            "{\r\n#if WINDOWS\r\n" + inner + "\r\n#else\r\n" +
            "            // TODO: implementacion no-Windows (Linux u otros SO) de " + name + "().\r\n" +
            "            throw new System.PlatformNotSupportedException(\"" + name + " usa API exclusiva de Windows; falta la implementacion no-Windows.\");\r\n" +
            "#endif\r\n}";
        if (SyntaxFactory.ParseStatement(text) is not BlockSyntax block) return m;
        return m.WithBody(block)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }

    /// <summary>Copia un fichero de contenido (xaml/resx/imagen/config...) tal cual, sin modificarlo.</summary>
    private static void CopyRaw(string source, string target)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    /// <summary>Reparte los ficheros de contenido entre Windows y Multi: el XAML es siempre Windows; el
    /// resto sigue al codigo de su mismo nombre (Form1.resx con Form1.cs) y, si es huerfano, por extension
    /// (recursos de UI -> Windows; el resto -> Multi).</summary>
    private static (List<(string Abs, string Rel)> Win, List<(string Abs, string Rel)> Multi) ClassifyContent(
        List<(string Abs, string Rel)> content,
        List<(string Abs, string Rel)> winCode,
        List<(string Abs, string Rel)> portableCode)
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

    /// <summary>Clave de agrupacion por carpeta + nombre hasta el primer punto: Form1.cs, Form1.Designer.cs
    /// y Form1.resx comparten clave "dir|Form1"; MainWindow.xaml.cs -> "dir|MainWindow".</summary>
    private static string StemKey(string rel)
    {
        var dir = System.IO.Path.GetDirectoryName(rel) ?? string.Empty;
        var name = System.IO.Path.GetFileName(rel);
        var dot = name.IndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        return dir + "|" + stem;
    }

    private static bool IsWindowsContent(string rel)
    {
        var ext = System.IO.Path.GetExtension(rel).ToLowerInvariant();
        return ext is ".xaml" or ".resx" or ".settings" or ".resources" or ".baml" or ".config"
            or ".png" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".gif" or ".cur";
    }

    /// <summary>Artefactos de Visual Studio/usuario que no deben copiarse al scaffold.</summary>
    private static bool IsVsJunk(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dtbcache.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True si el fichero es un AssemblyInfo.cs clasico (choca con GenerateAssemblyInfo del SDK).</summary>
    private static bool IsAssemblyInfo(string rel) =>
        System.IO.Path.GetFileName(rel).Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>True si el fichero contiene el punto de entrada: un metodo <c>static Main</c> o top-level statements.</summary>
    private static bool IsEntryPoint(string absPath)
    {
        try
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(absPath)).GetRoot();
            if (root.DescendantNodes().OfType<GlobalStatementSyntax>().Any()) return true;
            return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Any(m => m.Identifier.Text == "Main" && m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));
        }
        catch { return false; }
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

    private static (IReadOnlyList<string> Packages, bool UseWpf, bool UseWinForms, string? OutputType) ReadCsproj(string projectDir)
    {
        var csproj = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj is null) return (Array.Empty<string>(), false, false, null);

        var text = File.ReadAllText(csproj);
        var packages = PackageRef.Matches(text).Select(m => m.Value.Trim()).Distinct().ToList();
        var useWpf = Regex.IsMatch(text, "<UseWPF>\\s*true", RegexOptions.IgnoreCase) ||
                     text.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase);
        var useWinForms = Regex.IsMatch(text, "<UseWindowsForms>\\s*true", RegexOptions.IgnoreCase) ||
                          text.Contains("System.Windows.Forms", StringComparison.OrdinalIgnoreCase);
        var outputType = Regex.Match(text, "<OutputType>\\s*([^<]+?)\\s*</OutputType>", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups[1].Value.Trim() : null;
        return (packages, useWpf, useWinForms, outputType);
    }

    private static void WriteCsproj(string path, string tfm, IReadOnlyList<string> packages, bool useWpf, bool useWinForms, string? projectReference, string? outputType = null, bool generateAssemblyInfo = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
        if (!string.IsNullOrWhiteSpace(outputType)) sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        // Si se copia un AssemblyInfo.cs clasico, evitar que el SDK autogenere atributos (evita CS0579).
        if (!generateAssemblyInfo) sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
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
        int portable, int windows, IReadOnlyList<string> crossRefs, IReadOnlyList<string> manual,
        IReadOnlyList<SeamSpec> seams, string seamMultiNs, string seamWinNs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# División de {original} — guía de finalización");
        sb.AppendLine();
        sb.AppendLine("> Objetivo: dejar cada proyecto **listo y funcional a falta de pruebas**. Aquí tienes, paso a paso y con código, lo que queda por hacer.");
        sb.AppendLine();

        sb.AppendLine("## Qué se ha generado");
        sb.AppendLine($"- **{multiName}** (net8.0, portable): {portable} fichero(s) de código" + (seams.Count > 0 ? " + interfaces `Seams` portables." : "."));
        sb.AppendLine($"- **{winName}** (net8.0-windows): {windows} fichero(s) de código" + (seams.Count > 0 ? " + implementaciones Windows de los seams (con sus paquetes NuGet)" : "") + " + `GlobalUsings.cs` + `Portabilidad/EjemploPorSistemaOperativo.cs`.");
        if (seams.Count > 0)
        {
            sb.AppendLine("- **Seams generados** (interfaz portable ↔ implementación Windows):");
            foreach (var s in seams) sb.AppendLine($"  - `{s.Interfaz}` ↔ `{s.ImplClase}` — {s.Titulo}");
        }
        sb.AppendLine();

        sb.AppendLine("## Paso a paso");
        sb.AppendLine($"1. **Compila `{multiName}`** (portable): no debe referenciar WPF/WinForms/Win32. Resuelve las referencias cruzadas (abajo) moviendo tipos o introduciendo interfaces.");
        sb.AppendLine($"2. **Compila `{winName}`**: ya trae los `PackageReference` necesarios y una `ProjectReference` a `{multiName}`.");
        if (seams.Count > 0)
        {
            sb.AppendLine($"3. **Registra los seams** por inyección de dependencias en el arranque de `{winName}`:");
            sb.AppendLine();
            sb.AppendLine("   ```csharp");
            foreach (var s in seams) sb.AppendLine($"   services.AddSingleton<{s.Interfaz}, {s.ImplClase}>();");
            sb.AppendLine("   ```");
            sb.AppendLine($"   (Interfaces en `{seamMultiNs}`; implementaciones en `{seamWinNs}`.)");
            sb.AppendLine($"4. **En el núcleo `{multiName}`**, sustituye los usos directos de la API de Windows por la interfaz correspondiente (ver \"Cambios por categoría\").");
            sb.AppendLine("5. **Implementación no-Windows**: la interfaz de cada seam queda lista; su implementación para otros SO se deja preparada para otro equipo.");
        }
        else
        {
            sb.AppendLine($"3. Sustituye en el núcleo los usos de la API de Windows por interfaces (seams) e impleméntalas en `{winName}`.");
        }
        sb.AppendLine("6. **Prueba** cada proyecto.");
        sb.AppendLine();

        if (seams.Count > 0)
        {
            sb.AppendLine("## Cambios por categoría (con código propuesto)");
            sb.AppendLine();
            foreach (var s in seams)
            {
                sb.AppendLine($"### {s.Titulo}");
                sb.AppendLine(s.QueCambiar);
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine("// Antes (solo Windows):");
                sb.AppendLine(s.Antes);
                sb.AppendLine($"// Después (portable, vía {s.Interfaz} inyectada):");
                sb.AppendLine(s.Despues);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        if (crossRefs.Count > 0)
        {
            sb.AppendLine("## Referencias cruzadas a resolver (introducir abstracción)");
            sb.AppendLine();
            foreach (var r in crossRefs) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        sb.AppendLine("## Aislamiento por SO en el propio código (alternativa a los seams)");
        sb.AppendLine("Para casos puntuales puedes aislar por SO sin crear una interfaz (ver `Portabilidad/EjemploPorSistemaOperativo.cs`):");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine("if (OperatingSystem.IsWindows()) { /* API de Windows */ } else { /* alternativa portable */ }");
        sb.AppendLine("// o en compilación condicional (net8.0-windows define el símbolo WINDOWS):");
        sb.AppendLine("#if WINDOWS");
        sb.AppendLine("    // solo Windows");
        sb.AppendLine("#else");
        sb.AppendLine("    // no-Windows (a cargo de otro equipo)");
        sb.AppendLine("#endif");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Otras acciones");
        sb.AppendLine();
        foreach (var m in manual) sb.AppendLine($"- {m}");
        sb.AppendLine();

        sb.AppendLine("## Checklist final");
        sb.AppendLine($"- [ ] `{multiName}` compila sin dependencias de Windows.");
        sb.AppendLine($"- [ ] `{winName}` compila.");
        if (seams.Count > 0) sb.AppendLine("- [ ] Seams registrados por inyección de dependencias.");
        sb.AppendLine("- [ ] El núcleo usa las interfaces, no la API de Windows directamente.");
        sb.AppendLine("- [ ] Pruebas superadas.");

        File.WriteAllText(path, sb.ToString());
    }

    // ------------------------------------------------------------------------------------------------
    // Scaffolding de SEAMS: interfaz portable (Multi) + implementacion Windows real (proyecto Windows).
    // ------------------------------------------------------------------------------------------------

    /// <summary>Especificacion de un seam: interfaz portable e implementacion Windows para una categoria.</summary>
    private sealed record SeamSpec(
        string Categoria, string Titulo, string Interfaz, string ImplClase,
        string InterfaceMembers, string ImplUsings, string ImplBody, string? Package,
        string QueCambiar, string Antes, string Despues);

    private static readonly Dictionary<string, SeamSpec> Seams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Registry"] = new("Registry", "Registro de Windows -> configuración portable", "ISettingsStore", "WindowsSettingsStore",
            "        string? Get(string clave);\r\n        void Set(string clave, string valor);",
            "using Microsoft.Win32;",
            "        private const string Ruta = @\"HKEY_CURRENT_USER\\Software\\{winName}\";\r\n" +
            "        public string? Get(string clave) => (string?)Registry.GetValue(Ruta, clave, defaultValue: null);\r\n" +
            "        public void Set(string clave, string valor) => Registry.SetValue(Ruta, clave, valor);",
            "    <PackageReference Include=\"Microsoft.Win32.Registry\" Version=\"5.0.0\" />",
            "Sustituir las lecturas/escrituras del Registro por la interfaz `ISettingsStore` inyectada. La implementación no-Windows puede leer de `appsettings.json`/variables de entorno.",
            "var ruta = (string?)Registry.GetValue(@\"HKCU\\Software\\MiApp\", \"Ruta\", null);",
            "var ruta = settings.Get(\"Ruta\"); // settings: ISettingsStore"),

        ["Identity"] = new("Identity", "Identidad de Windows -> abstracción de identidad", "IUserIdentity", "WindowsUserIdentity",
            "        string CurrentUserName { get; }",
            "using System.Security.Principal;",
            "        public string CurrentUserName => WindowsIdentity.GetCurrent().Name;",
            "    <PackageReference Include=\"System.Security.Principal.Windows\" Version=\"5.0.0\" />",
            "Sustituir el uso de `WindowsIdentity` por `IUserIdentity`. En no-Windows se implementa con Kerberos/tokens/LDAP o el usuario de la petición.",
            "var u = System.Security.Principal.WindowsIdentity.GetCurrent().Name;",
            "var u = identity.CurrentUserName; // identity: IUserIdentity"),

        ["Cryptography"] = new("Cryptography", "DPAPI -> protección de secretos portable", "ISecretProtector", "WindowsSecretProtector",
            "        byte[] Protect(byte[] datos);\r\n        byte[] Unprotect(byte[] datos);",
            "using System.Security.Cryptography;",
            "        public byte[] Protect(byte[] datos) => ProtectedData.Protect(datos, optionalEntropy: null, DataProtectionScope.CurrentUser);\r\n" +
            "        public byte[] Unprotect(byte[] datos) => ProtectedData.Unprotect(datos, optionalEntropy: null, DataProtectionScope.CurrentUser);",
            "    <PackageReference Include=\"System.Security.Cryptography.ProtectedData\" Version=\"8.0.0\" />",
            "Sustituir DPAPI por `ISecretProtector`. IMPORTANTE: lo cifrado con DPAPI no se puede descifrar fuera de Windows; la implementación no-Windows debe usar AES con clave de un gestor de secretos (planificar re-cifrado).",
            "var prot = ProtectedData.Protect(datos, null, DataProtectionScope.CurrentUser);",
            "var prot = protector.Protect(datos); // protector: ISecretProtector"),

        ["EventLog"] = new("EventLog", "Visor de eventos -> logging portable", "IAppEventLog", "WindowsEventLog",
            "        void Write(string mensaje);",
            "using System.Diagnostics;",
            "        public void Write(string mensaje)\r\n        {\r\n" +
            "            using var log = new EventLog(\"Application\") { Source = \"Application\" };\r\n" +
            "            log.WriteEntry(mensaje, EventLogEntryType.Information);\r\n        }",
            "    <PackageReference Include=\"System.Diagnostics.EventLog\" Version=\"8.0.0\" />",
            "Sustituir el Visor de eventos por `IAppEventLog` (o directamente por `ILogger` de Microsoft.Extensions.Logging, portable).",
            "new EventLog(\"Application\").WriteEntry(\"msg\");",
            "logger.Write(\"msg\"); // logger: IAppEventLog"),

        ["Threading"] = new("Threading", "Sincronización entre procesos -> abstracción de bloqueo", "IInterProcessLock", "WindowsInterProcessLock",
            "        System.IDisposable Acquire(string nombre);",
            "using System.Threading;",
            "        public System.IDisposable Acquire(string nombre)\r\n        {\r\n" +
            "            var mutex = new Mutex(initiallyOwned: false, @\"Global\\\" + nombre);\r\n" +
            "            mutex.WaitOne();\r\n            return new Liberacion(mutex);\r\n        }\r\n\r\n" +
            "        private sealed class Liberacion : System.IDisposable\r\n        {\r\n" +
            "            private readonly Mutex _mutex;\r\n            public Liberacion(Mutex mutex) => _mutex = mutex;\r\n" +
            "            public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }\r\n        }",
            null,
            "Sustituir los mutex/semáforos con nombre por `IInterProcessLock`. Los semáforos con nombre no son portables; en no-Windows se implementa con file lock/socket/named pipe.",
            "var m = new Mutex(false, @\"Global\\MiApp\"); m.WaitOne();",
            "using var _ = ipcLock.Acquire(\"MiApp\"); // ipcLock: IInterProcessLock"),

        ["WMI"] = new("WMI", "WMI -> información del sistema portable", "ISystemInfo", "WindowsSystemInfo",
            "        string OsDescription { get; }",
            "using System.Runtime.InteropServices;",
            "        // Para datos que hoy solo da WMI, anade el paquete System.Management y consultalo aqui.\r\n" +
            "        public string OsDescription => RuntimeInformation.OSDescription;",
            null,
            "Sustituir las consultas WMI por `ISystemInfo`. Parte de la información ya la da `RuntimeInformation` (portable); lo específico de WMI se implementa aquí en Windows.",
            "var os = new ManagementObjectSearcher(\"SELECT * FROM Win32_OperatingSystem\");",
            "var os = systemInfo.OsDescription; // systemInfo: ISystemInfo"),
    };

    private static string BuildSeamInterface(SeamSpec s, string multiNs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [SCAFFOLD generado] Seam portable (interfaz). El nucleo depende de esto, NO de la API de Windows.");
        sb.AppendLine($"namespace {multiNs}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>{s.Titulo}</summary>");
        sb.AppendLine($"    public interface {s.Interfaz}");
        sb.AppendLine("    {");
        sb.AppendLine(s.InterfaceMembers);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildSeamWindowsImpl(SeamSpec s, string multiNs, string winNs, string winName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [SCAFFOLD generado] Implementacion Windows del seam. Compila en net8.0-windows.");
        if (!string.IsNullOrEmpty(s.ImplUsings)) sb.AppendLine(s.ImplUsings);
        sb.AppendLine($"using {multiNs};");
        sb.AppendLine($"namespace {winNs}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Implementacion Windows de {s.Interfaz}.</summary>");
        sb.AppendLine($"    public sealed class {s.ImplClase} : {s.Interfaz}");
        sb.AppendLine("    {");
        sb.AppendLine(s.ImplBody.Replace("{winName}", winName));
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildConditionalExample(string winNs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// [SCAFFOLD generado] Aislamiento por SISTEMA OPERATIVO en el propio codigo (alternativa a los");
        sb.AppendLine("// seams para casos puntuales). Muestra dos tecnicas: guarda en tiempo de ejecucion y #if.");
        sb.AppendLine($"namespace {winNs}");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class EjemploPorSistemaOperativo");
        sb.AppendLine("    {");
        sb.AppendLine("        // Tecnica 1: guarda en tiempo de ejecucion (un unico binario para todos los SO).");
        sb.AppendLine("        public static string CarpetaDeDatos() =>");
        sb.AppendLine("            System.OperatingSystem.IsWindows()");
        sb.AppendLine("                ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)");
        sb.AppendLine("                : System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);");
        sb.AppendLine();
        sb.AppendLine("        // Tecnica 2: compilacion condicional. El TFM net8.0-windows define el simbolo WINDOWS.");
        sb.AppendLine("        public static string Plataforma()");
        sb.AppendLine("        {");
        sb.AppendLine("#if WINDOWS");
        sb.AppendLine("            return \"codigo especifico de Windows (solo se compila en net8.0-windows)\";");
        sb.AppendLine("#else");
        sb.AppendLine("            return \"implementacion no-Windows (a cargo de otro equipo)\";");
        sb.AppendLine("#endif");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Escribe un fichero .cs generado con UTF-8 + BOM (para que cualquier compilador lea bien los acentos).</summary>
    private static void WriteCsFile(string path, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
