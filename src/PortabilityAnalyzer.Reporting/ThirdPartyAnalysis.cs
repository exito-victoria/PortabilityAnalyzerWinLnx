using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Una DLL nativa de la que depende (via P/Invoke) un ensamblado de terceros.</summary>
public sealed record NativeDependency(string Dll, int Sites, bool IsWindowsSystem);

/// <summary>Una API Windows GESTIONADA (no P/Invoke) que llama un ensamblado de terceros: el tipo/atributo
/// concreto de .NET dependiente de Windows (p. ej. RegistryKey, WindowsIdentity, EventLog), su categoria,
/// el nº de sitios de llamada y la alternativa multiplataforma (ES e inglés).</summary>
public sealed record WindowsManagedApi(string Categoria, string Api, int Sites, string AlternativaLinux, string? AlternativaLinuxEn = null)
{
    /// <summary>Alternativa en el idioma pedido (EN si hay traducción; si no, ES).</summary>
    public string Alternative(Lang lang) =>
        lang == Lang.En && !string.IsNullOrWhiteSpace(AlternativaLinuxEn) ? AlternativaLinuxEn! : AlternativaLinux;
}

/// <summary>Perfil de dependencias del SO de un ensamblado de terceros (sin fuentes).</summary>
public sealed record ThirdPartyProfile(
    string Assembly,
    Severity MaxSeverity,
    bool HasBlocker,
    IReadOnlyList<NativeDependency> NativeDeps,
    IReadOnlyList<WindowsManagedApi> WindowsApis,
    int WindowsApiRules,
    string? SuggestedReplacement,
    string? SuggestedReplacementEn = null)
{
    /// <summary>Reemplazo sugerido en el idioma pedido (EN si hay traducción; si no, ES).</summary>
    public string? SuggestedReplacementFor(Lang lang) =>
        lang == Lang.En && !string.IsNullOrWhiteSpace(SuggestedReplacementEn) ? SuggestedReplacementEn : SuggestedReplacement;
}

/// <summary>
/// Analisis en profundidad de los ensamblados de terceros: como no se controla su codigo ni su build,
/// se inventarian sus dependencias nativas del SO (P/Invoke) y sus APIs Windows gestionadas, se marca el
/// riesgo y se sugiere un reemplazo multiplataforma cuando se conoce.
/// </summary>
public static class ThirdPartyAnalysis
{
    // DLLs nativas del sistema Windows conocidas (el resto se trata como nativa de terceros).
    private static readonly HashSet<string> WindowsSystemDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32.dll", "user32.dll", "gdi32.dll", "advapi32.dll", "ole32.dll", "oleaut32.dll",
        "shell32.dll", "comctl32.dll", "comdlg32.dll", "crypt32.dll", "ntdll.dll", "ws2_32.dll",
        "winmm.dll", "secur32.dll", "netapi32.dll", "setupapi.dll", "wtsapi32.dll", "dwmapi.dll",
        "uxtheme.dll", "gdiplus.dll", "msvcrt.dll", "iphlpapi.dll", "version.dll", "psapi.dll",
        "userenv.dll", "wininet.dll", "urlmon.dll", "rpcrt4.dll", "cfgmgr32.dll", "powrprof.dll"
    };

    // Reemplazos multiplataforma conocidos por nombre de ensamblado de terceros (ES / EN).
    private static readonly Dictionary<string, (string Es, string En)> KnownReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Oracle.DataAccess"] = ("Oracle.ManagedDataAccess.Core (ODP.NET gestionado, multiplataforma)",
                                 "Oracle.ManagedDataAccess.Core (managed ODP.NET, cross-platform)"),
        ["Oracle.ManagedDataAccess"] = ("Oracle.ManagedDataAccess.Core (variante multiplataforma)",
                                        "Oracle.ManagedDataAccess.Core (cross-platform variant)"),
        ["System.Data.SqlClient"] = ("Microsoft.Data.SqlClient (multiplataforma)",
                                     "Microsoft.Data.SqlClient (cross-platform)")
    };

    public static IReadOnlyList<ThirdPartyProfile> Analyze(AnalysisReport report)
    {
        var profiles = new List<ThirdPartyProfile>();

        foreach (var asm in report.Assemblies.Where(a =>
                     a.IsThirdParty && a.Classification.Kind == AssemblyKind.Managed && a.Findings.Count > 0))
        {
            // Dependencias nativas: DLLs objetivo de P/Invoke (confirmadas y de senal debil), por sitio de llamada.
            var native = asm.Findings
                .Where(f => string.Equals(f.Categoria, "PInvoke", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(f.Evidencia))
                .GroupBy(f => NormalizeDll(f.Evidencia!))
                .Select(g => new NativeDependency(
                    g.Key,
                    g.Select(f => (f.Type, f.Method)).Distinct().Count(),
                    WindowsSystemDlls.Contains(g.Key)))
                .OrderByDescending(d => d.Sites)
                .ToList();

            // APIs Windows GESTIONADAS (no P/Invoke) concretas: se agrupan por la API/tipo dependiente de
            // Windows (Evidencia; si falta, el tipo o la regla), con su categoria, nº de sitios y alternativa.
            var winApis = asm.ConfirmedFindings()
                .Where(f => !string.Equals(f.Categoria, "PInvoke", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => (f.Categoria, Api: ApiName(f)))
                .Select(g => new WindowsManagedApi(
                    g.Key.Categoria,
                    g.Key.Api,
                    g.Select(f => (f.Type, f.Method)).Distinct().Count(),
                    g.Select(f => f.AlternativaLinux).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? string.Empty,
                    g.Select(f => f.AlternativaLinuxEn).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))))
                .OrderBy(a => a.Categoria).ThenByDescending(a => a.Sites)
                .ToList();

            var winApiRules = asm.ConfirmedFindings()
                .Where(f => !string.Equals(f.Categoria, "PInvoke", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.RuleId)
                .Distinct()
                .Count();

            KnownReplacements.TryGetValue(asm.Classification.Name, out var replacement);

            profiles.Add(new ThirdPartyProfile(
                asm.Classification.Name, asm.MaxSeverity, asm.HasBlocker, native, winApis, winApiRules,
                replacement.Es, replacement.En));
        }

        return profiles.OrderByDescending(p => p.MaxSeverity).ThenByDescending(p => p.NativeDeps.Count).ToList();
    }

    public static string DependencyKind(NativeDependency d, Lang lang = Lang.Es) =>
        d.IsWindowsSystem
            ? Loc.T(lang, "Sistema Windows", "Windows system")
            : Loc.T(lang, "Nativa de terceros (verificar disponibilidad multiplataforma)", "Third-party native (check cross-platform availability)");

    /// <summary>Nombre concreto de la API/tipo Windows del hallazgo: prioriza la evidencia (tipo, atributo o
    /// API detectada); si falta, cae al tipo declarante y por ultimo a la regla.</summary>
    private static string ApiName(Finding f)
    {
        if (!string.IsNullOrWhiteSpace(f.Evidencia)) return f.Evidencia!.Trim();
        if (!string.IsNullOrWhiteSpace(f.Type)) return f.Type!.Trim();
        return f.RuleId;
    }

    private static string NormalizeDll(string s)
    {
        var d = s.Trim().ToLowerInvariant();
        if (!d.EndsWith(".dll", StringComparison.Ordinal)) d += ".dll";
        return d;
    }
}
