namespace PortabilityAnalyzer.Core;

/// <summary>Cross-platform status of a referenced library.</summary>
public enum LibraryStatus
{
    /// <summary>Already cross-platform; no change required.</summary>
    Multiplataforma,
    /// <summary>A cross-platform package replaces it (possibly with a namespace swap).</summary>
    Reemplazar,
    /// <summary>Windows-only with no drop-in replacement: needs a manual rewrite following the given guidance.</summary>
    Revisar
}

/// <summary>A library referenced by the solution/project and its cross-platform equivalent.</summary>
public sealed record ReferencedLibrary(
    string Package,
    string? Version,
    LibraryStatus Status,
    string? Replacement,          // replacement package (when applicable)
    string? ReplacementVersion,
    string NotaEs,
    string NotaEn);

/// <summary>
/// CURATED catalog of Windows-only libraries and their cross-platform equivalent (replacement package,
/// version and, when it is a safe 1:1 change, the namespace swap). Used both by the report (referenced
/// libraries section) and by the rewriter (to swap the package and the <c>using</c> in the portable project).
/// </summary>
public static class LibraryReplacements
{
    /// <summary>A catalog entry. <see cref="MatchPrefix"/> matches by prefix (e.g. Interop families).</summary>
    public sealed record Entry(
        string Match, bool MatchPrefix, LibraryStatus Status,
        string? Replacement, string? ReplacementVersion,
        string? NamespaceFrom, string? NamespaceTo,
        string NotaEs, string NotaEn);

    // Report notes (NotaEs/NotaEn) are user-facing bilingual data, kept in ES/EN on purpose.
    private static readonly Entry[] Table =
    {
        // --- Windows-only WITH a drop-in replacement (package + 1:1 namespace swap) ---
        new("Oracle.DataAccess", false, LibraryStatus.Reemplazar, "Oracle.ManagedDataAccess.Core", "23.5.1",
            "Oracle.DataAccess.Client", "Oracle.ManagedDataAccess.Client",
            "ODP.NET unmanaged (solo Windows) -> Oracle.ManagedDataAccess.Core (100% gestionado, multiplataforma).",
            "Unmanaged ODP.NET (Windows only) -> Oracle.ManagedDataAccess.Core (100% managed, cross-platform)."),
        new("Oracle.ManagedDataAccess", false, LibraryStatus.Reemplazar, "Oracle.ManagedDataAccess.Core", "23.5.1",
            null, null,
            "Variante clásica (apunta a .NET Framework) -> Oracle.ManagedDataAccess.Core para net8.0.",
            "Classic variant (targets .NET Framework) -> Oracle.ManagedDataAccess.Core for net8.0."),
        new("System.Data.SqlClient", false, LibraryStatus.Reemplazar, "Microsoft.Data.SqlClient", "5.2.2",
            "System.Data.SqlClient", "Microsoft.Data.SqlClient",
            "Cliente SQL Server heredado -> Microsoft.Data.SqlClient (multiplataforma).",
            "Legacy SQL Server client -> Microsoft.Data.SqlClient (cross-platform)."),

        // --- Windows-only WITHOUT a drop-in (manual rewrite following the guidance) ---
        new("System.Drawing.Common", false, LibraryStatus.Revisar, "SixLabors.ImageSharp", "3.1.5", null, null,
            "Fuera de Windows lanza PlatformNotSupportedException. Migrar a ImageSharp o SkiaSharp.",
            "Throws PlatformNotSupportedException off Windows. Migrate to ImageSharp or SkiaSharp."),
        new("System.Diagnostics.EventLog", false, LibraryStatus.Revisar, null, null, null, null,
            "Visor de eventos (solo Windows). Migrar el logging a Serilog / Microsoft.Extensions.Logging.",
            "Event Viewer (Windows only). Migrate logging to Serilog / Microsoft.Extensions.Logging."),
        new("Microsoft.Win32.Registry", false, LibraryStatus.Revisar, null, null, null, null,
            "Registro de Windows. Externalizar a Microsoft.Extensions.Configuration (appsettings/variables de entorno).",
            "Windows Registry. Externalize to Microsoft.Extensions.Configuration (appsettings/env vars)."),
        new("Microsoft.Win32.SystemEvents", false, LibraryStatus.Revisar, null, null, null, null,
            "Eventos del sistema de Windows. Aislar tras una interfaz; sin equivalente directo multiplataforma.",
            "Windows system events. Isolate behind an interface; no direct cross-platform equivalent."),
        new("System.Management", false, LibraryStatus.Revisar, null, null, null, null,
            "WMI (solo Windows). Parte de la info la da RuntimeInformation; el resto, aislar tras una interfaz.",
            "WMI (Windows only). Part of the info is provided by RuntimeInformation; isolate the rest behind an interface."),
        new("System.DirectoryServices", true, LibraryStatus.Revisar, "System.DirectoryServices.Protocols", "8.0.0", null, null,
            "AD nativo (Windows). Usar System.DirectoryServices.Protocols o Novell.Directory.Ldap (multiplataforma).",
            "Native AD (Windows). Use System.DirectoryServices.Protocols or Novell.Directory.Ldap (cross-platform)."),
        new("System.ServiceProcess.ServiceController", false, LibraryStatus.Revisar, null, null, null, null,
            "Servicios de Windows. Usar Microsoft.Extensions.Hosting para un host portable.",
            "Windows Services. Use Microsoft.Extensions.Hosting for a portable host."),
        new("System.Security.Cryptography.ProtectedData", false, LibraryStatus.Revisar, null, null, null, null,
            "DPAPI (solo Windows). Sustituir por AES con clave de un gestor de secretos (KMS). Planificar re-cifrado.",
            "DPAPI (Windows only). Replace with AES using a key from a secrets manager (KMS). Plan re-encryption."),
        new("Microsoft.Office.Interop", true, LibraryStatus.Revisar, "DocumentFormat.OpenXml", "3.1.0", null, null,
            "Automatización de Office (COM, Windows). Usar OpenXML SDK o ClosedXML (multiplataforma).",
            "Office automation (COM, Windows). Use the OpenXML SDK or ClosedXML (cross-platform)."),
        new("System.Speech", false, LibraryStatus.Revisar, null, null, null, null,
            "APIs de voz de Windows. Usar un servicio de voz multiplataforma (motor externo/cloud).",
            "Windows speech APIs. Use a cross-platform speech service (external/cloud engine)."),
        new("System.Messaging", false, LibraryStatus.Revisar, null, null, null, null,
            "MSMQ (solo Windows). Migrar a RabbitMQ, Azure Service Bus u otra cola multiplataforma.",
            "MSMQ (Windows only). Migrate to RabbitMQ, Azure Service Bus or another cross-platform queue."),

        // --- Known CROSS-PLATFORM (no change required) ---
        new("Oracle.ManagedDataAccess.Core", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.Data.SqlClient", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.Extensions.", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Newtonsoft.Json", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("System.Text.Json", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Serilog", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Dapper", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("AutoMapper", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("SkiaSharp", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("SixLabors.", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
    };

    /// <summary>Finds the catalog entry for a package (exact or prefix match). Null if not present.</summary>
    public static Entry? Lookup(string package)
    {
        foreach (var e in Table)
            if (e.MatchPrefix ? package.StartsWith(e.Match, StringComparison.OrdinalIgnoreCase)
                              : string.Equals(package, e.Match, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    /// <summary>Classifies a referenced package. If it is not in the catalog it is marked "Revisar" with a
    /// neutral note (probably cross-platform, but worth verifying).</summary>
    public static ReferencedLibrary Classify(string package, string? version)
    {
        var e = Lookup(package);
        if (e is null)
            return new ReferencedLibrary(package, version, LibraryStatus.Revisar, null, null,
                "Sin dato en el catálogo: probablemente multiplataforma; verificar en nuget.org (soporte net8.0 y RID no-Windows).",
                "Not in the catalog: probably cross-platform; verify on nuget.org (net8.0 support and non-Windows RID).");
        var repl = e.Status == LibraryStatus.Reemplazar ? e.Replacement : null;
        return new ReferencedLibrary(package, version, e.Status, repl, e.ReplacementVersion, e.NotaEs, e.NotaEn);
    }
}
