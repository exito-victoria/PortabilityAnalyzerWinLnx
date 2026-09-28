using System.Text;
using System.Text.RegularExpressions;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Resuelve de forma ESTÁTICA (sin compilar) el conjunto de ficheros <c>.cs</c> que forman parte de la
/// compilación de un proyecto, para que el análisis y la estimación tengan en cuenta SOLO lo que está
/// activo y NO lo que fue removido del proyecto (ficheros/carpetas que siguen en disco pero se sacaron
/// del <c>.csproj</c>). Aproxima el conjunto de items <c>Compile</c> de MSBuild:
///   - Proyectos SDK (<c>&lt;Project Sdk="..."&gt;</c>) con items por defecto: todos los <c>.cs</c> bajo la
///     carpeta (sin <c>obj</c>/<c>bin</c>) MENOS los que quiten los <c>&lt;Compile Remove="..."&gt;</c>, más los
///     <c>&lt;Compile Include="..."&gt;</c> explícitos, aplicados en orden de documento.
///   - Proyectos legacy (no SDK) o con <c>&lt;EnableDefaultCompileItems&gt;false&lt;/EnableDefaultCompileItems&gt;</c>:
///     SOLO los ficheros de los <c>&lt;Compile Include="..."&gt;</c> (lo demás no se compila).
/// Es una aproximación (no ejecuta MSBuild), pero cubre el caso habitual de "excluir del proyecto".
/// </summary>
public static class ActiveCompileSet
{
    // <Compile Include|Remove|Update="..." ...>  (self-closing o con cierre). Capturamos los atributos.
    private static readonly Regex CompileItem = new(
        "<Compile\\b(?<attrs>[^>]*?)(?:/>|>.*?</Compile>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex IncludeAttr = new("Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RemoveAttr = new("Remove\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Detección de proyecto SDK-style: atributo Sdk en <Project> o un <Import ... Sdk="..." />.
    private static readonly Regex SdkOnProject = new("<Project\\b[^>]*\\bSdk\\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SdkImport = new("<Import\\b[^>]*\\bSdk\\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DisableDefaultItems = new(
        "<EnableDefaultCompileItems>\\s*false\\s*</EnableDefaultCompileItems>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Ficheros <c>.cs</c> activos del proyecto (rutas absolutas normalizadas). Devuelve <c>null</c> si no se
    /// puede determinar (no hay <c>.csproj</c> en la carpeta): el llamante debe entonces analizar todo por
    /// prudencia (no excluir sin evidencia).
    /// </summary>
    public static IReadOnlySet<string>? Resolve(string projectDir)
    {
        if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return null;

        string[] csprojs;
        try { csprojs = Directory.GetFiles(projectDir, "*.csproj"); } catch { return null; }
        if (csprojs.Length == 0) return null;

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in csprojs)
        {
            string text;
            try { text = File.ReadAllText(csproj); } catch { continue; }
            foreach (var f in ResolveOne(projectDir, text)) active.Add(f);
        }
        return active;
    }

    /// <summary>Ruta absoluta normalizada para comparar contra el conjunto (mismo criterio que <see cref="Resolve"/>).
    /// Si no se puede normalizar, devuelve la ruta original.</summary>
    public static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private static IEnumerable<string> ResolveOne(string projectDir, string csprojText)
    {
        var sdk = SdkOnProject.IsMatch(csprojText) || SdkImport.IsMatch(csprojText);
        var defaultItems = sdk && !DisableDefaultItems.IsMatch(csprojText);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Conjunto base: en SDK con items por defecto, todos los .cs bajo la carpeta (sin obj/bin); en legacy
        // o con EnableDefaultCompileItems=false, vacío (solo cuenta lo que se incluya explícitamente).
        if (defaultItems)
            foreach (var f in EnumerateCs(projectDir)) set.Add(f);

        // Aplicar los <Compile Include/Remove> EN ORDEN DE DOCUMENTO (Include añade, Remove quita, Update no cambia).
        foreach (Match m in CompileItem.Matches(csprojText))
        {
            var attrs = m.Groups["attrs"].Value;

            var inc = IncludeAttr.Match(attrs);
            if (inc.Success)
            {
                foreach (var f in ExpandGlob(projectDir, inc.Groups[1].Value))
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                        set.Add(f);
                continue;
            }

            var rem = RemoveAttr.Match(attrs);
            if (rem.Success)
                foreach (var f in ExpandGlob(projectDir, rem.Groups[1].Value))
                    set.Remove(f);
            // Update: no altera la pertenencia al conjunto de compilación.
        }

        return set;
    }

    /// <summary>Todos los <c>.cs</c> bajo la carpeta del proyecto salvo <c>obj</c>/<c>bin</c>, en ruta absoluta normalizada.</summary>
    private static IEnumerable<string> EnumerateCs(string projectDir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in files)
        {
            if (IsObjBin(f, projectDir)) continue;
            string full;
            try { full = Path.GetFullPath(f); } catch { continue; }
            yield return full;
        }
    }

    /// <summary>Expande un valor de <c>Include</c>/<c>Remove</c> (puede traer varios patrones separados por ';',
    /// con comodines <c>*</c>/<c>**</c>/<c>?</c> o una ruta concreta) a rutas absolutas normalizadas.</summary>
    private static IEnumerable<string> ExpandGlob(string projectDir, string pattern)
    {
        foreach (var rawPart in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart;
            var hasWildcard = part.IndexOfAny(new[] { '*', '?' }) >= 0;

            if (!hasWildcard)
            {
                // Ruta concreta (puede ser un fichero enlazado fuera de la carpeta): resolver a absoluta.
                string full;
                try { full = Path.GetFullPath(Path.Combine(projectDir, part.Replace('\\', Path.DirectorySeparatorChar))); }
                catch { continue; }
                yield return full;
                continue;
            }

            // Patrón con comodines: construir la ruta-glob absoluta (sin GetFullPath, que rechaza '*'/'?') y
            // casar contra los .cs de la carpeta del proyecto.
            var glob = IsRooted(part)
                ? part.Replace('\\', '/')
                : projectDir.Replace('\\', '/').TrimEnd('/') + "/" + part.Replace('\\', '/').TrimStart('/');
            var rx = GlobToRegex(glob);
            foreach (var f in EnumerateCs(projectDir))
                if (rx.IsMatch(f.Replace('\\', '/')))
                    yield return f;
        }
    }

    private static bool IsRooted(string p) =>
        p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("\\", StringComparison.Ordinal) ||
        (p.Length >= 2 && p[1] == ':');

    /// <summary>Convierte un glob de MSBuild (<c>**</c>, <c>*</c>, <c>?</c>) en una expresión regular sobre la
    /// ruta normalizada con '/'. <c>**/</c> = cero o más segmentos; <c>*</c> = dentro de un segmento; <c>?</c> = un carácter.</summary>
    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++; // consumir el segundo '*'
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { sb.Append("(?:.*/)?"); i++; }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    private static bool IsObjBin(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(seg => seg.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                   seg.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }
}
