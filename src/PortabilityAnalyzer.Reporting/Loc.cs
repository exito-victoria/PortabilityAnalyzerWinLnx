using System.Globalization;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Idioma de un informe.</summary>
public enum Lang { Es, En }

/// <summary>
/// Utilidades de localización de los informes. El patrón principal es <see cref="T"/> (elige entre el
/// texto español e inglés). Además localiza los valores derivados de enums (estrategia, severidad,
/// confianza, bucket de coste, categoría) y la guía de corrección del análisis de código fuente.
/// El español es el idioma por defecto, de modo que los informes existentes (Markdown, JSON) no cambian.
/// </summary>
public static class Loc
{
    /// <summary>Elige el texto según el idioma.</summary>
    public static string T(Lang lang, string es, string en) => lang == Lang.En ? en : es;

    /// <summary>Cultura para formatear números (coma en ES, punto en EN).</summary>
    public static CultureInfo Cult(Lang lang) => lang == Lang.En ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Sufijo de fichero por idioma ("" para ES, "_EN" para EN).</summary>
    public static string FileSuffix(Lang lang) => lang == Lang.En ? "_EN" : "";

    public static string Strategy(SeparationStrategy? s, Lang lang) => s switch
    {
        SeparationStrategy.Comun => T(lang, "Común", "Common"),
        SeparationStrategy.AbstraerPorPlataforma => T(lang, "Abstraer por plataforma", "Abstract per platform"),
        SeparationStrategy.ReemplazarDependencia => T(lang, "Reemplazar dependencia", "Replace dependency"),
        SeparationStrategy.RedisenoUI => T(lang, "Rediseño UI", "UI redesign"),
        _ => "—"
    };

    public static string Severity(Severity s, Lang lang) => lang == Lang.Es ? s.ToString() : s switch
    {
        Core.Severity.Bajo => "Low",
        Core.Severity.Medio => "Medium",
        Core.Severity.Alto => "High",
        Core.Severity.Bloqueante => "Blocking",
        _ => "Info"
    };

    public static string Confidence(Confidence c, Lang lang) => lang == Lang.Es ? c.ToString() : c switch
    {
        Core.Confidence.Alta => "High",
        Core.Confidence.Media => "Medium",
        _ => "Low"
    };

    public static string Bucket(CostBucket b, Lang lang) => lang == Lang.Es ? CostBuckets.Text(b) : b switch
    {
        CostBucket.NucleoComun => "Common-core adaptation",
        CostBucket.SeparacionAbstraccion => "Platform separation (abstraction)",
        CostBucket.ReemplazoDependencias => "Dependency replacement",
        CostBucket.UILinux => "UI separation (portable presentation layer)",
        CostBucket.PruebasCI => "Cross-platform testing & CI",
        _ => "Unclassified"
    };

    public static string Category(string cat, Lang lang) => lang == Lang.Es ? CategoryEs(cat) : CategoryEn(cat);

    private static string CategoryEs(string cat) => cat switch
    {
        "UI" => "Interfaz de usuario (WPF/WinForms)",
        "Database" => "Acceso a datos (Oracle/SQL nativo)",
        "Registry" => "Registro de Windows",
        "Identity" => "Identidad / autenticación de Windows",
        "PInvoke" => "Llamadas nativas (P/Invoke)",
        "COM" => "Componentes COM",
        "WMI" => "WMI (información del sistema)",
        "Cryptography" => "Criptografía (DPAPI/CNG)",
        "EventLog" => "Registro de eventos de Windows",
        "ServiceProcess" => "Servicios de Windows",
        "Threading" => "Sincronización / hilos de UI",
        _ => cat
    };

    private static string CategoryEn(string cat) => cat switch
    {
        "UI" => "User interface (WPF/WinForms)",
        "Database" => "Data access (native Oracle/SQL)",
        "Registry" => "Windows Registry",
        "Identity" => "Windows identity / authentication",
        "PInvoke" => "Native calls (P/Invoke)",
        "COM" => "COM components",
        "WMI" => "WMI (system information)",
        "Cryptography" => "Cryptography (DPAPI/CNG)",
        "EventLog" => "Windows Event Log",
        "ServiceProcess" => "Windows Services",
        "Threading" => "Synchronization / UI threads",
        _ => cat
    };

    /// <summary>Guía de corrección en INGLÉS para el análisis de código fuente (equivalente a
    /// SourceCodeAnalyzer.Fix; en español se usa el texto ya calculado del hallazgo).</summary>
    public static string FixEn(string categoria) => categoria switch
    {
        "UI" => "The UI is not portable: move the logic/ViewModels to the portable core; keep the WPF UI on Windows and leave the non-Windows UI for another team.",
        "Database" => "Migrate to Oracle.ManagedDataAccess.Client (the .Core package, cross-platform) and adapt the connection string.",
        "Registry" => "Externalize configuration (appsettings.json / IConfiguration); if it must stay on Windows, isolate it behind an ISettingsStore interface per OS.",
        "Identity" => "Replace Windows identity with a cross-platform scheme (Kerberos/GSSAPI, tokens, LDAP) behind an IUserIdentity interface.",
        "Threading" => "In the core, replace UI synchronization with async/await; STAThread/Dispatcher only at the Windows UI startup.",
        "Cryptography" => "Use the cross-platform factories (RSA.Create/Aes.Create); DPAPI does not exist outside Windows (re-encrypt secrets).",
        "WMI" => "Isolate WMI behind an interface; part of the information is already available via RuntimeInformation (portable).",
        "COM" => "COM does not exist outside Windows: abstract the service behind a portable interface or remove the dependency.",
        "EventLog" => "Move logging to a portable framework (Serilog / Microsoft.Extensions.Logging) writing to console/file.",
        "PerformanceCounter" => "Migrate to EventCounters / System.Diagnostics.Metrics (portable).",
        "ServiceProcess" => "Use Microsoft.Extensions.Hosting; the non-Windows service integration is left for another team.",
        "PlatformAttribute" => "API marked Windows-only: find a portable equivalent or guard it with OperatingSystem.IsWindows().",
        "PInvoke" => "Replace with the managed equivalent or isolate the call behind an interface (P/Invoke only on Windows; non-Windows implementation as a seam).",
        _ => "Review the usage: replace with a portable equivalent or guard by OS (OperatingSystem.IsWindows())."
    };
}
