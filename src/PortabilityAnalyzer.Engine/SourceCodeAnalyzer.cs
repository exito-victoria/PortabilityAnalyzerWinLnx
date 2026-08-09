using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Analisis a nivel de CODIGO FUENTE (Roslyn, sintactico): recorre los <c>.cs</c> de cada proyecto y
/// localiza usos de APIs propias de Windows (directivas <c>using</c>, tipos, atributos y P/Invoke),
/// indicando fichero, linea, segmento de codigo, simbolo implicado y como corregirlo para multiplataforma.
/// No requiere compilar (analisis sintactico), por lo que funciona aunque falten referencias.
/// </summary>
public sealed class SourceCodeAnalyzer
{
    private enum Kind { UsingNamespace, TypeName, Attribute }

    private sealed record Signal(string Match, Kind Kind, string Categoria);

    // Tabla curada de senales Windows a nivel de fuente (ordenada por especificidad al aplicar).
    private static readonly Signal[] Signals =
    {
        // Directivas using de espacios de nombres solo-Windows.
        new("System.Windows.Forms", Kind.UsingNamespace, "UI"),
        new("System.Windows", Kind.UsingNamespace, "UI"),
        new("System.Data.OracleClient", Kind.UsingNamespace, "Database"),
        new("System.Management", Kind.UsingNamespace, "WMI"),
        new("System.DirectoryServices", Kind.UsingNamespace, "Identity"),
        new("System.ServiceProcess", Kind.UsingNamespace, "ServiceProcess"),
        new("System.Speech", Kind.UsingNamespace, "Misc"),
        new("System.Messaging", Kind.UsingNamespace, "Misc"),
        new("Microsoft.Office.Interop", Kind.UsingNamespace, "COM"),
        new("Microsoft.Win32", Kind.UsingNamespace, "Registry"),

        // Tipos solo-Windows (por nombre simple).
        new("OracleConnection", Kind.TypeName, "Database"),
        new("OracleCommand", Kind.TypeName, "Database"),
        new("OracleDataAdapter", Kind.TypeName, "Database"),
        new("OracleDataReader", Kind.TypeName, "Database"),
        new("Registry", Kind.TypeName, "Registry"),
        new("RegistryKey", Kind.TypeName, "Registry"),
        new("WindowsIdentity", Kind.TypeName, "Identity"),
        new("WindowsPrincipal", Kind.TypeName, "Identity"),
        new("WindowsImpersonationContext", Kind.TypeName, "Identity"),
        new("Dispatcher", Kind.TypeName, "Threading"),
        new("ManagementObject", Kind.TypeName, "WMI"),
        new("ManagementObjectSearcher", Kind.TypeName, "WMI"),
        new("ManagementClass", Kind.TypeName, "WMI"),
        new("ProtectedData", Kind.TypeName, "Cryptography"),
        new("RSACryptoServiceProvider", Kind.TypeName, "Cryptography"),
        new("CngKey", Kind.TypeName, "Cryptography"),
        new("RSACng", Kind.TypeName, "Cryptography"),
        new("ECDsaCng", Kind.TypeName, "Cryptography"),
        new("EventLog", Kind.TypeName, "EventLog"),
        new("PerformanceCounter", Kind.TypeName, "PerformanceCounter"),
        new("ServiceController", Kind.TypeName, "ServiceProcess"),
        new("SystemEvents", Kind.TypeName, "Misc"),

        // Atributos.
        new("DllImport", Kind.Attribute, "PInvoke"),
        new("STAThread", Kind.Attribute, "Threading"),
        new("ComImport", Kind.Attribute, "COM"),
        new("SupportedOSPlatform", Kind.Attribute, "PlatformAttribute")
    };

    private static readonly HashSet<string> TypeNames =
        Signals.Where(s => s.Kind == Kind.TypeName).Select(s => s.Match).ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<SourceFinding> AnalyzeProjects(IEnumerable<(string Name, string Dir)> projects)
    {
        var all = new List<SourceFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, dir) in projects)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedOrBuild(file, dir)) continue;

                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }

                var tree = CSharpSyntaxTree.ParseText(text);
                var sourceText = tree.GetText();
                var root = tree.GetRoot();
                var rel = Path.GetRelativePath(dir, file);

                foreach (var node in root.DescendantNodes())
                {
                    (Signal, string)? f = node switch
                    {
                        UsingDirectiveSyntax u => FromUsing(u),
                        AttributeSyntax a => FromAttribute(a),
                        IdentifierNameSyntax id when TypeNames.Contains(id.Identifier.Text) => FromType(id),
                        _ => null
                    };
                    if (f is null) continue;

                    var (signal, symbol) = f.Value;
                    var line = node.GetLocation().GetLineSpan().StartLinePosition.Line; // 0-based
                    var key = $"{rel}|{line}|{symbol}";
                    if (!seen.Add(key)) continue;

                    all.Add(new SourceFinding
                    {
                        Project = name,
                        File = rel,
                        Line = line + 1,
                        Kind = KindText(signal.Kind),
                        Symbol = symbol,
                        Categoria = signal.Categoria,
                        Clase = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text,
                        Metodo = ContainingMember(node),
                        Segmento = LineText(sourceText, line),
                        ComoCorregir = Fix(signal.Categoria)
                    });
                }
            }
        }

        return all
            .OrderBy(f => f.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .ToList();
    }

    private static (Signal, string)? FromUsing(UsingDirectiveSyntax u)
    {
        var ns = u.Name?.ToString();
        if (ns is null) return null;
        // La senal mas especifica (Match mas largo) gana.
        var s = Signals.Where(x => x.Kind == Kind.UsingNamespace && ns.StartsWith(x.Match, StringComparison.Ordinal))
                       .OrderByDescending(x => x.Match.Length)
                       .FirstOrDefault();
        return s is null ? null : (s, ns);
    }

    private static (Signal, string)? FromAttribute(AttributeSyntax a)
    {
        var attrName = a.Name.ToString();
        var s = Signals.FirstOrDefault(x => x.Kind == Kind.Attribute &&
                                            (attrName == x.Match || attrName.EndsWith("." + x.Match, StringComparison.Ordinal) ||
                                             attrName == x.Match + "Attribute" || attrName.EndsWith("." + x.Match + "Attribute", StringComparison.Ordinal)));
        if (s is null) return null;

        if (s.Categoria == "PlatformAttribute")
        {
            // Solo si el argumento indica "windows".
            var arg = a.ArgumentList?.Arguments.FirstOrDefault()?.ToString() ?? string.Empty;
            if (arg.IndexOf("windows", StringComparison.OrdinalIgnoreCase) < 0) return null;
        }

        var symbol = s.Match;
        if (s.Categoria == "PInvoke")
        {
            var dll = (a.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
            symbol = string.IsNullOrEmpty(dll) ? "DllImport" : $"DllImport(\"{dll}\")";
        }
        return (s, symbol);
    }

    private static (Signal, string)? FromType(IdentifierNameSyntax id)
    {
        var s = Signals.First(x => x.Kind == Kind.TypeName && x.Match == id.Identifier.Text);
        return (s, id.Identifier.Text);
    }

    private static string? ContainingMember(SyntaxNode node)
    {
        foreach (var anc in node.Ancestors())
        {
            switch (anc)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case ConstructorDeclarationSyntax c: return c.Identifier.Text + " (.ctor)";
                case PropertyDeclarationSyntax p: return p.Identifier.Text + " (prop)";
            }
        }
        return null;
    }

    private static string LineText(Microsoft.CodeAnalysis.Text.SourceText text, int line)
    {
        if (line < 0 || line >= text.Lines.Count) return string.Empty;
        var s = text.Lines[line].ToString().Trim();
        return s.Length <= 200 ? s : s[..200] + "...";
    }

    private static bool IsGeneratedOrBuild(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(seg => seg.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                   seg.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static string KindText(Kind k) => k switch
    {
        Kind.UsingNamespace => "using",
        Kind.Attribute => "Atributo",
        _ => "Tipo"
    };

    private static string Fix(string categoria) => categoria switch
    {
        "UI" => "La UI no es portable: separar la lógica al core y reimplementar la UI de Linux con Avalonia (MVVM), reutilizando ViewModels.",
        "Database" => "Migrar a Oracle.ManagedDataAccess.Client (paquete .Core, multiplataforma) y adaptar la cadena de conexión.",
        "Registry" => "Externalizar la configuración (appsettings.json / IConfiguration); si debe seguir en Windows, aislar tras una interfaz ISettingsStore por SO.",
        "Identity" => "Sustituir la identidad de Windows por un esquema multiplataforma (Kerberos/GSSAPI, tokens, LDAP) tras una interfaz IUserIdentity.",
        "Threading" => "En el core, reemplazar la sincronización con UI por async/await; STAThread/Dispatcher solo en el arranque de la UI Windows.",
        "Cryptography" => "Usar las factorías multiplataforma (RSA.Create/Aes.Create); DPAPI no existe en Linux (re-cifrar los secretos).",
        "WMI" => "Sustituir WMI por /proc, /sys o librerías del sistema, tras una abstracción por SO.",
        "COM" => "COM no existe en Linux: abstraer el servicio tras una interfaz multiplataforma o eliminar la dependencia.",
        "EventLog" => "Migrar el logging a un framework multiplataforma (Serilog / Microsoft.Extensions.Logging) con salida a fichero/syslog.",
        "PerformanceCounter" => "Migrar a EventCounters / System.Diagnostics.Metrics (multiplataforma).",
        "ServiceProcess" => "Usar Microsoft.Extensions.Hosting; en Linux integrar con systemd en vez de servicios de Windows.",
        "PlatformAttribute" => "API marcada solo-Windows: buscar equivalente multiplataforma o aislar con OperatingSystem.IsWindows().",
        "PInvoke" => "Sustituir por la API gestionada equivalente o aislar la llamada tras una interfaz (P/Invoke solo en Windows; alternativa en Linux).",
        _ => "Revisar el uso: sustituir por un equivalente multiplataforma o proteger por SO (OperatingSystem.IsWindows())."
    };
}
