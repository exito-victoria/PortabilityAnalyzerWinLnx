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
        "UI" => "The GUI (WPF/WinForms) is the ONLY exception: it is not migrated. Move the logic/ViewModels to the portable core (net8.0); on Linux only those classes and methods are built, not the graphical layer. The WPF UI stays in the Windows project.",
        "Database" => "Migrate to Oracle.ManagedDataAccess.Client (the .Core package, cross-platform) and adapt the connection string.",
        "Registry" => "Externalize configuration (appsettings.json / IConfiguration); if it must stay on Windows, isolate it behind an ISettingsStore interface per OS.",
        "Identity" => "Cross-platform with a library: Environment.UserName for the local user; System.DirectoryServices.Protocols or Novell.Directory.Ldap (cross-platform LDAP) for directory. Encapsulate behind IUserIdentity with a cross-platform implementation.",
        "Threading" => "Threads/tasks: Thread, Task, Parallel, async/await, ThreadPool, SemaphoreSlim and System.Threading.Timer are ALREADY cross-platform. Windows-specific: DispatcherTimer (WPF) -> a portable timer (System.Timers.Timer / System.Threading.PeriodicTimer); the rewriter swaps it for a Portability.Threading.PortableTimer shim with the same API. Dispatcher.Invoke/BeginInvoke (UI marshalling) -> async/await + IProgress<T> or a captured SynchronizationContext. STAThread is a no-op off Windows (harmless).",
        "Cryptography" => "Cross-platform with a library/BCL, transparent to the OS: CNG/CSP -> the BCL factories RSA.Create()/ECDsa.Create()/Aes.Create(); DPAPI (ProtectedData) -> ASP.NET Core Data Protection (Microsoft.AspNetCore.DataProtection) via a portable ProtectedData shim with the same API. The rewriter applies both changes.",
        "WMI" => "Cross-platform with a library: RuntimeInformation (OS/architecture) and, for hardware/inventory, a cross-platform library (e.g. Hardware.Info). Encapsulate behind an interface with a cross-platform implementation.",
        "COM" => "COM does not exist outside Windows: abstract the service behind a portable interface or remove the dependency.",
        "EventLog" => "Move logging to a portable framework (Serilog / Microsoft.Extensions.Logging) writing to console/file.",
        "PerformanceCounter" => "Migrate to EventCounters / System.Diagnostics.Metrics (portable).",
        "ServiceProcess" => "Use Microsoft.Extensions.Hosting (cross-platform host) + Microsoft.Extensions.Hosting.Systemd (Linux) and Microsoft.Extensions.Hosting.WindowsServices (Windows): both cross-platform packages cover running as a service transparently.",
        "PlatformAttribute" => "API marked Windows-only: find a portable equivalent or guard it with OperatingSystem.IsWindows().",
        "PInvoke" => "Replace with the managed BCL equivalent (cross-platform); if none exists, use a cross-platform library/NuGet that covers it. Avoid P/Invoke to Windows DLLs.",
        _ => "Review the usage: replace with a portable equivalent or guard by OS (OperatingSystem.IsWindows())."
    };
}
