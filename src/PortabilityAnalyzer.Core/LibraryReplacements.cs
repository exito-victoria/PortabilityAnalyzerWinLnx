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

/// <summary>
/// Whether a referenced package is actually consumed by the code. Computed conservatively by
/// <c>LibraryUsageAnalyzer</c>: a package is only flagged as a removal candidate with positive evidence
/// of non-use, never on a doubt (reflection/DI usage can hide a real dependency).
/// </summary>
public enum LibraryUsage
{
    /// <summary>The package's assembly is referenced by the compiled IL, or its namespace appears in source.</summary>
    Usada,
    /// <summary>Built output resolved and the package's assembly is NOT referenced anywhere: candidate to remove (verify manually).</summary>
    CandidataARevisar,
    /// <summary>Development-only dependency (analyzer, test SDK, PrivateAssets): not referenced at runtime by design; not removable on that basis.</summary>
    SoloBuild,
    /// <summary>Could not be verified (project not built, package not restored, or a framework/meta package): treated as in-use to stay safe.</summary>
    NoVerificable
}

/// <summary>A library referenced by the solution/project and its cross-platform equivalent.</summary>
public sealed record ReferencedLibrary(
    string Package,
    string? Version,
    LibraryStatus Status,
    string? Replacement,          // replacement package (when applicable)
    string? ReplacementVersion,
    string NotaEs,
    string NotaEn)
{
    // --- Real-usage validation (additive; defaults keep older JSON/behaviour valid). ---

    /// <summary>Whether the package is actually consumed by the code (see <see cref="LibraryUsage"/>).</summary>
    public LibraryUsage Usage { get; init; } = LibraryUsage.NoVerificable;

    /// <summary>First-party projects (.csproj) that declare this PackageReference.</summary>
    public IReadOnlyList<string> Projects { get; init; } = new List<string>();

    /// <summary>Bilingual, human-facing evidence for the usage verdict (which assembly/namespace, in which project).</summary>
    public string? UsageEvidenceEs { get; init; }
    public string? UsageEvidenceEn { get; init; }
}

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
    // Curated from the Microsoft porting guidance: "Port from .NET Framework to .NET"
    // (learn.microsoft.com/dotnet/core/porting), the "Microsoft.Windows.Compatibility" package docs and the
    // per-package cross-platform notes on nuget.org / learn.microsoft.com. Statuses:
    //   Reemplazar = safe drop-in (package swap, kept 1:1, with an optional namespace swap) -> the rewriter
    //                applies it automatically. Kept minimal on purpose (only true drop-ins).
    //   Revisar    = Windows-only / not a drop-in: needs a manual rewrite, but a PROPOSED cross-platform
    //                alternative is offered (Replacement) so the report suggests where to go.
    //   Multiplataforma = already cross-platform; no change required.
    private static readonly Entry[] Table =
    {
        // --- SPECIFIC overrides: must precede the prefix rules below (Lookup returns the FIRST match) ---
        // System.DirectoryServices.Protocols IS cross-platform (LDAP at the protocol level), unlike the rest of
        // the System.DirectoryServices.* family. Source: learn.microsoft.com/dotnet/core/porting + dotnet/runtime#84831.
        new("System.DirectoryServices.Protocols", false, LibraryStatus.Multiplataforma, null, null, null, null,
            "Ya multiplataforma (LDAP a nivel de protocolo; funciona en Linux).",
            "Already cross-platform (LDAP at the protocol level; works on Linux)."),
        // AccountManagement lanza PlatformNotSupportedException fuera de Windows. Source: dotnet/runtime#84831.
        new("System.DirectoryServices.AccountManagement", false, LibraryStatus.Revisar, "System.DirectoryServices.Protocols", "8.0.0", null, null,
            "AccountManagement lanza PlatformNotSupportedException en Linux. Reescribir sobre System.DirectoryServices.Protocols o Novell.Directory.Ldap.NETStandard (multiplataforma) tras IUserIdentity.",
            "AccountManagement throws PlatformNotSupportedException on Linux. Rewrite on System.DirectoryServices.Protocols or Novell.Directory.Ldap.NETStandard (cross-platform) behind IUserIdentity."),
        // PdfSharpCore es el port multiplataforma de PdfSharp; debe ganar al prefijo "PdfSharp" de abajo.
        new("PdfSharpCore", false, LibraryStatus.Multiplataforma, null, null, null, null,
            "Ya multiplataforma (port de PdfSharp sin GDI+).",
            "Already cross-platform (PdfSharp port without GDI+)."),

        // --- Windows-only WITH a drop-in replacement (package + 1:1 namespace swap) ---
        new("Oracle.DataAccess", false, LibraryStatus.Reemplazar, "Oracle.ManagedDataAccess.Core", "23.5.1",
            "Oracle.DataAccess.Client", "Oracle.ManagedDataAccess.Client",
            "ODP.NET unmanaged (solo Windows) -> Oracle.ManagedDataAccess.Core (100% gestionado, multiplataforma).",
            "Unmanaged ODP.NET (Windows only) -> Oracle.ManagedDataAccess.Core (100% managed, cross-platform)."),
        new("Oracle.ManagedDataAccess", false, LibraryStatus.Reemplazar, "Oracle.ManagedDataAccess.Core", "23.5.1",
            null, null,
            "Variante clásica (apunta a .NET Framework) -> Oracle.ManagedDataAccess.Core para net8.0.",
            "Classic variant (targets .NET Framework) -> Oracle.ManagedDataAccess.Core for net8.0."),
        new("System.Data.OracleClient", false, LibraryStatus.Reemplazar, "Oracle.ManagedDataAccess.Core", "23.5.1",
            "System.Data.OracleClient", "Oracle.ManagedDataAccess.Client",
            "Proveedor Oracle integrado y obsoleto (solo Windows) -> Oracle.ManagedDataAccess.Core (gestionado, multiplataforma).",
            "Built-in, deprecated Oracle provider (Windows only) -> Oracle.ManagedDataAccess.Core (managed, cross-platform)."),
        new("System.Data.SqlClient", false, LibraryStatus.Reemplazar, "Microsoft.Data.SqlClient", "5.2.2",
            "System.Data.SqlClient", "Microsoft.Data.SqlClient",
            "Cliente SQL Server heredado -> Microsoft.Data.SqlClient (multiplataforma).",
            "Legacy SQL Server client -> Microsoft.Data.SqlClient (cross-platform)."),

        // --- Windows-only WITHOUT a drop-in (manual rewrite; a portable alternative is proposed) ---
        new("System.Drawing.Common", false, LibraryStatus.Revisar, "SixLabors.ImageSharp", "3.1.5", null, null,
            "Fuera de Windows lanza PlatformNotSupportedException. Migrar a ImageSharp o SkiaSharp.",
            "Throws PlatformNotSupportedException off Windows. Migrate to ImageSharp or SkiaSharp."),
        new("System.Diagnostics.EventLog", false, LibraryStatus.Revisar, "Microsoft.Extensions.Logging", "8.0.1", null, null,
            "Visor de eventos (solo Windows). Migrar el logging a Serilog / Microsoft.Extensions.Logging.",
            "Event Viewer (Windows only). Migrate logging to Serilog / Microsoft.Extensions.Logging."),
        new("System.Diagnostics.PerformanceCounter", false, LibraryStatus.Revisar, "System.Diagnostics.DiagnosticSource", "8.0.1", null, null,
            "Contadores de rendimiento (solo Windows). Migrar a EventCounters / System.Diagnostics.Metrics.",
            "Performance counters (Windows only). Migrate to EventCounters / System.Diagnostics.Metrics."),
        new("Microsoft.Win32.Registry", false, LibraryStatus.Revisar, "Microsoft.Extensions.Configuration", "8.0.0", null, null,
            "Registro de Windows. Externalizar a Microsoft.Extensions.Configuration (appsettings/variables de entorno).",
            "Windows Registry. Externalize to Microsoft.Extensions.Configuration (appsettings/env vars)."),
        new("Microsoft.Win32.SystemEvents", false, LibraryStatus.Revisar, null, null, null, null,
            "Eventos del sistema de Windows. Aislar tras una interfaz; sin equivalente directo multiplataforma.",
            "Windows system events. Isolate behind an interface; no direct cross-platform equivalent."),
        new("System.Management", false, LibraryStatus.Revisar, "System.Runtime.InteropServices.RuntimeInformation", null, null, null,
            "WMI (solo Windows). Parte de la info la da RuntimeInformation; el resto, aislar tras una interfaz.",
            "WMI (Windows only). Part of the info is provided by RuntimeInformation; isolate the rest behind an interface."),
        new("System.DirectoryServices", true, LibraryStatus.Revisar, "System.DirectoryServices.Protocols", "8.0.0", null, null,
            "AD nativo (Windows). Usar System.DirectoryServices.Protocols o Novell.Directory.Ldap (multiplataforma).",
            "Native AD (Windows). Use System.DirectoryServices.Protocols or Novell.Directory.Ldap (cross-platform)."),
        new("System.ServiceProcess.ServiceController", false, LibraryStatus.Revisar, "Microsoft.Extensions.Hosting", "8.0.1", null, null,
            "Servicios de Windows. Usar Microsoft.Extensions.Hosting (host portable) + systemd/servicio nativo.",
            "Windows Services. Use Microsoft.Extensions.Hosting (portable host) + systemd/native service."),
        new("System.Security.Cryptography.ProtectedData", false, LibraryStatus.Reemplazar, "Microsoft.AspNetCore.DataProtection.Extensions", "8.0.10", null, null,
            "DPAPI (solo Windows) -> ASP.NET Core Data Protection (Microsoft.AspNetCore.DataProtection), multiplataforma y transparente al SO. El reescritor genera un shim ProtectedData portable con la misma API.",
            "DPAPI (Windows only) -> ASP.NET Core Data Protection (Microsoft.AspNetCore.DataProtection), cross-platform and OS-transparent. The rewriter generates a portable ProtectedData shim with the same API."),
        new("System.Security.Cryptography.Cng", false, LibraryStatus.Revisar, "RSA.Create()/ECDsa.Create() (BCL)", null, null, null,
            "CNG (solo Windows) -> factorías portables del BCL RSA.Create()/ECDsa.Create()/DSA.Create() (multiplataforma, sin paquete). El reescritor cambia new RSACng()/RSACryptoServiceProvider()/ECDsaCng() por la fábrica.",
            "CNG (Windows only) -> portable BCL factories RSA.Create()/ECDsa.Create()/DSA.Create() (cross-platform, no package). The rewriter swaps new RSACng()/RSACryptoServiceProvider()/ECDsaCng() for the factory."),
        new("System.Security.AccessControl", true, LibraryStatus.Revisar, null, null, null, null,
            "ACLs de Windows (solo Windows). En Linux usar permisos POSIX (Unix file mode) tras una interfaz.",
            "Windows ACLs (Windows only). On Linux use POSIX permissions (Unix file mode) behind an interface."),
        new("System.Security.Principal.Windows", false, LibraryStatus.Revisar, null, null, null, null,
            "Identidad de Windows (solo Windows). Aislar tras una interfaz IUserIdentity con implementación por SO.",
            "Windows identity (Windows only). Isolate behind an IUserIdentity interface with a per-OS implementation."),
        new("Microsoft.Office.Interop", true, LibraryStatus.Revisar, "DocumentFormat.OpenXml", "3.1.0", null, null,
            "Automatización de Office (COM, Windows). Usar OpenXML SDK o ClosedXML (multiplataforma).",
            "Office automation (COM, Windows). Use the OpenXML SDK or ClosedXML (cross-platform)."),
        new("System.Speech", false, LibraryStatus.Revisar, null, null, null, null,
            "APIs de voz de Windows. Usar un servicio de voz multiplataforma (motor externo/cloud).",
            "Windows speech APIs. Use a cross-platform speech service (external/cloud engine)."),
        new("System.Messaging", false, LibraryStatus.Revisar, "RabbitMQ.Client", "6.8.1", null, null,
            "MSMQ (solo Windows). Migrar a RabbitMQ, Azure Service Bus u otra cola multiplataforma.",
            "MSMQ (Windows only). Migrate to RabbitMQ, Azure Service Bus or another cross-platform queue."),
        new("System.Configuration.ConfigurationManager", false, LibraryStatus.Revisar, "Microsoft.Extensions.Configuration", "8.0.0", null, null,
            "app.config/ConfigurationManager (patrón .NET Framework). Migrar a Microsoft.Extensions.Configuration (appsettings.json).",
            "app.config/ConfigurationManager (.NET Framework pattern). Migrate to Microsoft.Extensions.Configuration (appsettings.json)."),
        new("System.Runtime.Caching", false, LibraryStatus.Revisar, "Microsoft.Extensions.Caching.Memory", "8.0.1", null, null,
            "MemoryCache clásico. Migrar a Microsoft.Extensions.Caching.Memory (IMemoryCache, multiplataforma).",
            "Classic MemoryCache. Migrate to Microsoft.Extensions.Caching.Memory (IMemoryCache, cross-platform)."),
        new("EntityFramework", false, LibraryStatus.Revisar, "Microsoft.EntityFrameworkCore", "8.0.10", null, null,
            "EF6 (apunta a .NET Framework). Migrar a Microsoft.EntityFrameworkCore (revisar cambios de API).",
            "EF6 (targets .NET Framework). Migrate to Microsoft.EntityFrameworkCore (review API changes)."),
        new("System.Web", true, LibraryStatus.Revisar, "Microsoft.AspNetCore.App", null, null, null,
            "ASP.NET clásico (System.Web, solo Windows/IIS). Reescribir sobre ASP.NET Core.",
            "Classic ASP.NET (System.Web, Windows/IIS only). Rewrite on ASP.NET Core."),
        new("Microsoft.Owin", true, LibraryStatus.Revisar, "Microsoft.AspNetCore.App", null, null, null,
            "OWIN/Katana. Reescribir el middleware sobre ASP.NET Core.",
            "OWIN/Katana. Rewrite the middleware on ASP.NET Core."),
        new("System.ServiceModel", true, LibraryStatus.Revisar, "CoreWCF", "1.6.0", null, null,
            "WCF (System.ServiceModel). Cliente: System.ServiceModel.* (paquetes). Servidor: CoreWCF o gRPC.",
            "WCF (System.ServiceModel). Client: System.ServiceModel.* packages. Server: CoreWCF or gRPC."),
        new("System.Runtime.Remoting", true, LibraryStatus.Revisar, "Grpc.AspNetCore", "2.66.0", null, null,
            ".NET Remoting (eliminado en .NET moderno). Migrar a gRPC o HTTP/REST.",
            ".NET Remoting (removed in modern .NET). Migrate to gRPC or HTTP/REST."),
        new("System.EnterpriseServices", true, LibraryStatus.Revisar, null, null, null, null,
            "COM+ / Enterprise Services (solo Windows). Reemplazar por transacciones/host multiplataforma.",
            "COM+ / Enterprise Services (Windows only). Replace with a cross-platform transaction/host model."),
        new("System.Windows.Forms.DataVisualization", true, LibraryStatus.Revisar, "ScottPlot", "5.0.47", null, null,
            "Gráficas de WinForms (solo Windows). Usar ScottPlot / LiveCharts u OxyPlot (multiplataforma).",
            "WinForms charts (Windows only). Use ScottPlot / LiveCharts or OxyPlot (cross-platform)."),
        new("CrystalDecisions", true, LibraryStatus.Revisar, "QuestPDF", "2024.10.0", null, null,
            "Crystal Reports (solo Windows). Usar QuestPDF, iText o el motor de informes que aplique (multiplataforma).",
            "Crystal Reports (Windows only). Use QuestPDF, iText or a suitable reporting engine (cross-platform)."),
        // Source: learn.microsoft.com/dotnet/core/porting/windows-compat-pack (~mitad de las APIs son solo-Windows).
        new("Microsoft.Windows.Compatibility", false, LibraryStatus.Revisar, null, null, null, null,
            "Meta-paquete de compatibilidad: ~la mitad de sus APIs son solo-Windows (Registro, WMI, EventLog...) y lanzan PlatformNotSupportedException. Revisar con el Platform Compatibility Analyzer y sustituir cada API solo-Windows por su equivalente portable.",
            "Compatibility meta-package: ~half of its APIs are Windows-only (Registry, WMI, EventLog...) and throw PlatformNotSupportedException. Review with the Platform Compatibility Analyzer and replace each Windows-only API with its portable equivalent."),
        // Source: dotnet/runtime#91729 (sin equivalente en netstandard; depende de System.Security.Principal.Windows).
        new("System.IO.FileSystem.AccessControl", false, LibraryStatus.Revisar, null, null, null, null,
            "ACLs de ficheros de Windows (solo Windows). En Linux usar permisos POSIX (Unix file mode) tras una interfaz por SO.",
            "Windows file ACLs (Windows only). On Linux use POSIX permissions (Unix file mode) behind a per-OS interface."),
        // Source: nuget.org/packages/WindowsAPICodePack-Shell + Microsoft Q&A 1345619 (solo Windows, sin mantener).
        new("WindowsAPICodePack", true, LibraryStatus.Revisar, null, null, null, null,
            "Windows API Code Pack (shell/tareas de Windows, solo Windows y sin mantenimiento). Aislar tras una interfaz; sin equivalente multiplataforma directo.",
            "Windows API Code Pack (Windows shell/taskbar, Windows only and unmaintained). Isolate behind an interface; no direct cross-platform equivalent."),
        new("Microsoft-WindowsAPICodePack", true, LibraryStatus.Revisar, null, null, null, null,
            "Windows API Code Pack (shell/tareas de Windows, solo Windows). Aislar tras una interfaz; sin equivalente multiplataforma directo.",
            "Windows API Code Pack (Windows shell/taskbar, Windows only). Isolate behind an interface; no direct cross-platform equivalent."),
        // PdfSharp clásico (build GDI+). El prefijo cae DESPUÉS del override exacto de PdfSharpCore de arriba.
        new("PdfSharp", true, LibraryStatus.Revisar, "PdfSharpCore", "1.3.67", null, null,
            "PdfSharp clásico (build GDI+, atado a System.Drawing en Windows). Usar PdfSharpCore o el build Core de PDFsharp 6 (multiplataforma).",
            "Classic PdfSharp (GDI+ build, tied to System.Drawing on Windows). Use PdfSharpCore or the PDFsharp 6 Core build (cross-platform)."),

        // --- WPF/WinForms UI toolkits: la GUI de escritorio solo corre en Windows (excepción GUI del objetivo).
        //     No se migran; en Linux se construyen solo las clases/métodos, no la capa gráfica. ---
        new("MahApps.Metro", true, LibraryStatus.Revisar, null, null, null, null,
            "Framework de UI para WPF (solo Windows). Forma parte de la capa gráfica, que no se migra (excepción GUI).",
            "UI framework for WPF (Windows only). Part of the graphical layer, which is not migrated (GUI exception)."),
        new("MaterialDesignThemes", true, LibraryStatus.Revisar, null, null, null, null,
            "Tema Material Design para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Material Design theme for WPF (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("MaterialDesignColors", true, LibraryStatus.Revisar, null, null, null, null,
            "Paleta de Material Design para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Material Design palette for WPF (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("Extended.Wpf.Toolkit", true, LibraryStatus.Revisar, null, null, null, null,
            "Controles extendidos para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Extended controls for WPF (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("Fluent.Ribbon", true, LibraryStatus.Revisar, null, null, null, null,
            "Ribbon para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Ribbon control for WPF (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("ControlzEx", true, LibraryStatus.Revisar, null, null, null, null,
            "Utilidades de UI para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "WPF UI helpers (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("Xceed.Wpf", true, LibraryStatus.Revisar, null, null, null, null,
            "Controles Xceed para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Xceed WPF controls (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("DevExpress", true, LibraryStatus.Revisar, null, null, null, null,
            "Suite DevExpress de escritorio (WPF/WinForms, solo Windows). Capa gráfica; para lógica reutilizable, aislarla del control visual.",
            "DevExpress desktop suite (WPF/WinForms, Windows only). Graphical layer; for reusable logic, isolate it from the visual control."),
        new("Telerik.Windows", true, LibraryStatus.Revisar, null, null, null, null,
            "Suite Telerik para WPF (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Telerik suite for WPF (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("Telerik.WinControls", true, LibraryStatus.Revisar, null, null, null, null,
            "Suite Telerik para WinForms (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Telerik suite for WinForms (Windows only). Graphical layer; not migrated (GUI exception)."),
        new("Infragistics", true, LibraryStatus.Revisar, null, null, null, null,
            "Suite Infragistics de escritorio (solo Windows). Capa gráfica; no se migra (excepción GUI).",
            "Infragistics desktop suite (Windows only). Graphical layer; not migrated (GUI exception)."),

        // --- Known CROSS-PLATFORM (no change required) ---
        new("Oracle.ManagedDataAccess.Core", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.Data.SqlClient", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.EntityFrameworkCore", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.Extensions.", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.AspNetCore.", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Newtonsoft.Json", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("System.Text.Json", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("System.IO.Ports", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma (SerialPort funciona en Linux).", "Already cross-platform (SerialPort works on Linux)."),
        new("System.Text.Encoding.CodePages", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma (habilita code pages heredados).", "Already cross-platform (enables legacy code pages)."),
        new("Microsoft.VisualBasic", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma (Microsoft.VisualBasic.Core).", "Already cross-platform (Microsoft.VisualBasic.Core)."),
        new("Serilog", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("NLog", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("log4net", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Dapper", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("AutoMapper", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("MediatR", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("FluentValidation", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Polly", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("RestSharp", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Refit", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Npgsql", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("MySqlConnector", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("MySql.Data", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("StackExchange.Redis", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("MongoDB.Driver", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Quartz", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Hangfire", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("ClosedXML", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("DocumentFormat.OpenXml", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("CsvHelper", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("QuestPDF", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("itext7", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("SkiaSharp", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("SixLabors.", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("xunit", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("NUnit", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Moq", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("FluentAssertions", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Shouldly", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("xunit", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("coverlet", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),

        // MVVM / patrones de presentación agnósticos de UI (funcionan sin la GUI).
        // Source: github.com/CommunityToolkit/dotnet (UI-agnóstico, netstandard2.0/2.1/net6).
        new("CommunityToolkit.Mvvm", false, LibraryStatus.Multiplataforma, null, null, null, null,
            "Ya multiplataforma (MVVM agnóstico de UI; netstandard2.0).", "Already cross-platform (UI-agnostic MVVM; netstandard2.0)."),
        new("CommunityToolkit.Diagnostics", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("CommunityToolkit.HighPerformance", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("ReactiveUI", false, LibraryStatus.Multiplataforma, null, null, null, null,
            "Núcleo multiplataforma (los adaptadores ReactiveUI.WPF/WinForms sí son de UI Windows).",
            "Cross-platform core (the ReactiveUI.WPF/WinForms adapters are Windows UI)."),

        // Contenedores de inyección de dependencias (todos multiplataforma).
        new("Autofac", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Ninject", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("SimpleInjector", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Castle.Core", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Castle.Windsor", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),

        // Hilos / sincronización / programación reactiva (BCL y librerías, todo multiplataforma).
        new("System.Reactive", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Nito.AsyncEx", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("System.Threading.Tasks.Dataflow", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("System.Threading.Channels", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),

        // Gráficas portables (alternativa a las de WinForms/WPF). OxyPlot.Core/LiveChartsCore son multiplataforma;
        // sus renderizadores WPF sí son de Windows.
        new("OxyPlot.Core", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma (el renderizador WPF sí es de Windows).", "Already cross-platform (the WPF renderer is Windows)."),
        new("LiveChartsCore", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma (el renderizador WPF sí es de Windows).", "Already cross-platform (the WPF renderer is Windows)."),

        // Excel / PDF portables. Source: epplussoftware.com (v5+ sin System.Drawing en Linux), github.com/ststeiger/PdfSharpCore.
        new("EPPlus", true, LibraryStatus.Multiplataforma, null, null, null, null,
            "Ya multiplataforma (v5+ sin dependencia de System.Drawing.Common en Linux; licencia Polyform no comercial).",
            "Already cross-platform (v5+ with no System.Drawing.Common dependency on Linux; Polyform noncommercial license)."),

        // HTTP / gRPC / serialización.
        new("Flurl", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Grpc", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Google.Protobuf", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Microsoft.Data.Sqlite", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),

        // Utilidades varias multiplataforma de uso frecuente.
        new("System.CommandLine", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("Humanizer", true, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
        new("HtmlAgilityPack", false, LibraryStatus.Multiplataforma, null, null, null, null, "Ya multiplataforma.", "Already cross-platform."),
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
        // Surface the proposed alternative for BOTH statuses: "Reemplazar" is a safe drop-in the rewriter
        // applies automatically; "Revisar" is a suggested cross-platform target for a manual migration.
        return new ReferencedLibrary(package, version, e.Status, e.Replacement, e.ReplacementVersion, e.NotaEs, e.NotaEn);
    }
}
