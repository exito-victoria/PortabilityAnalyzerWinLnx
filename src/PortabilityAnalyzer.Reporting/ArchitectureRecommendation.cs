using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Proyecto sugerido de la arquitectura destino.</summary>
public sealed record RecommendedProject(string Name, string Tfm, string Purpose);

/// <summary>Recomendacion de arquitectura destino y plan de migracion, sintetizada del analisis.</summary>
public sealed record ArchitecturePlan(
    IReadOnlyList<RecommendedProject> Projects,
    IReadOnlyList<string> Abstractions,
    IReadOnlyList<string> Replacements,
    IReadOnlyList<string> MigrationSteps,
    IReadOnlyList<string> RoleNotes,
    bool HasUi,
    EffortEstimate TotalWithTesting,
    int Blockers,
    string? WorkedExample);

/// <summary>
/// Deriva del informe una arquitectura destino PORTABLE-FIRST. Localizable (ES/EN) mediante el parametro
/// <c>lang</c> (por defecto ES, para no cambiar el informe Markdown/JSON).
/// </summary>
public static class ArchitectureRecommendation
{
    private static readonly HashSet<string> AbstractCategories = new(StringComparer.OrdinalIgnoreCase)
    { "Registry", "Identity", "Threading", "PInvoke", "ProcessInvocation", "COM", "Cryptography", "Misc" };

    private static string AbstractionText(string cat, Lang lang) => cat switch
    {
        "Registry" => Loc.T(lang, "ISettingsStore - configuración externa en vez del Registro", "ISettingsStore - external configuration instead of the Registry"),
        "Identity" => Loc.T(lang, "IUserIdentity / IAuthenticationService - identidad y autenticación", "IUserIdentity / IAuthenticationService - identity and authentication"),
        "Threading" => Loc.T(lang, "IInterProcessLock - sincronización/señalización entre procesos", "IInterProcessLock - inter-process synchronization/signaling"),
        "PInvoke" => Loc.T(lang, "INativePlatform - llamadas nativas específicas por SO", "INativePlatform - OS-specific native calls"),
        "ProcessInvocation" => Loc.T(lang, "IProcessRunner - ejecución de comandos del SO", "IProcessRunner - OS command execution"),
        "COM" => Loc.T(lang, "Interfaz del servicio COM afectado", "Interface for the affected COM service"),
        "Cryptography" => Loc.T(lang, "IProtectedDataStore - protección de secretos (si usa DPAPI)", "IProtectedDataStore - secret protection (if using DPAPI)"),
        _ => Loc.T(lang, "Abstracción específica según el caso", "Case-specific abstraction")
    };

    public static ArchitecturePlan Build(AnalysisReport report, Lang lang = Lang.Es)
    {
        var confirmed = report.Assemblies.SelectMany(a => a.ConfirmedFindings()).ToList();
        var hasUi = confirmed.Any(f => f.EstrategiaSeparacion == SeparationStrategy.RedisenoUI);

        var abstractions = confirmed
            .Where(f => f.EstrategiaSeparacion == SeparationStrategy.AbstraerPorPlataforma)
            .Select(f => f.Categoria)
            .Where(AbstractCategories.Contains)
            .Distinct()
            .Select(c => AbstractionText(c, lang))
            .ToList();

        var why = Loc.T(lang, "Por qué", "Why");
        var replacements = confirmed
            .Where(f => f.EstrategiaSeparacion == SeparationStrategy.ReemplazarDependencia && !string.IsNullOrEmpty(f.Evidencia))
            .Select(f => ShortName(f.Evidencia!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(src =>
            {
                var tgt = TargetFor(src, lang) ?? Loc.T(lang, "buscar un equivalente multiplataforma gestionado", "find a managed cross-platform equivalent");
                return $"{src} -> {tgt}. {why}: {WhyFor(src, lang).TrimEnd('.')}";
            })
            .Take(12)
            .ToList();

        var baseName = (report.Assemblies
            .Where(a => !a.IsThirdParty && a.Classification.Kind == AssemblyKind.Managed)
            .Select(a => a.Classification.Name)
            .FirstOrDefault() ?? "App").Replace(" ", string.Empty);

        var projects = new List<RecommendedProject>
        {
            new($"{baseName}.Core", "net8.0", Loc.T(lang,
                "Núcleo multiplataforma: toda la lógica portable (sin dependencias de SO). Objetivo: que sea lo más grande posible.",
                "Cross-platform core: all portable logic (no OS dependencies). Goal: make it as large as possible.")),
            new($"{baseName}.Abstractions", "net8.0", Loc.T(lang,
                "Interfaces (seam) de las capacidades que dependen del SO; el núcleo solo depende de estas interfaces.",
                "Interfaces (the seam) for OS-dependent capabilities; the core depends only on these interfaces.")),
            new($"{baseName}.Platform.Windows", "net8.0-windows", Loc.T(lang,
                "Única pieza atada a Windows: implementación de las abstracciones que hoy requieren Windows (Registro, identidad, P/Invoke).",
                "The only Windows-bound piece: implementation of the abstractions that currently require Windows (Registry, identity, P/Invoke)."))
        };
        if (hasUi)
            projects.Add(new($"{baseName}.App.Windows", "net8.0-windows", Loc.T(lang,
                "UI actual en WPF (Windows). Los ViewModels/lógica se llevan al núcleo portable para poder reutilizarse.",
                "Current WPF UI (Windows). ViewModels/logic are moved to the portable core so they can be reused.")));
        else
            projects.Add(new($"{baseName}.Host", "net8.0", Loc.T(lang,
                "Ejecutable/servicio multiplataforma (host portable).", "Cross-platform executable/service (portable host).")));

        var steps = new List<string>
        {
            Loc.T(lang,
                $"PORTABLE-FIRST: llevar toda la lógica posible a {baseName}.Core (net8.0), sin referencias a WPF/WinForms/Win32; que el núcleo compile y corra sin ataduras de SO.",
                $"PORTABLE-FIRST: move as much logic as possible to {baseName}.Core (net8.0), with no references to WPF/WinForms/Win32; the core should compile and run without OS ties.")
        };
        if (abstractions.Count > 0)
            steps.Add(Loc.T(lang,
                $"Aislar lo que hoy exige Windows tras interfaces en {baseName}.Abstractions e inyectarlas por DI (seam): {string.Join("; ", abstractions)}.",
                $"Isolate what currently requires Windows behind interfaces in {baseName}.Abstractions and inject them via DI (seam): {string.Join("; ", abstractions)}."));
        if (replacements.Count > 0)
            steps.Add(Loc.T(lang,
                $"Sustituir cada dependencia no portable por el equivalente multiplataforma sugerido (con el porqué; el detalle y los pasos están en 'Alternativa portable / multiplataforma' y 'Pasos de remediación', y hay ejemplos de código en el apéndice): {string.Join(" | ", replacements)}.",
                $"Replace each non-portable dependency with the suggested cross-platform equivalent (with the why; details and steps are in 'Portable / cross-platform alternative' and 'Remediation steps', and there are code examples in the appendix): {string.Join(" | ", replacements)}."));
        steps.Add(Loc.T(lang,
            $"Implementar en {baseName}.Platform.Windows solo la parte obligatoriamente Windows de cada abstracción; alternativamente, aislarla en el propio código con OperatingSystem.IsWindows() / #if.",
            $"In {baseName}.Platform.Windows implement only the strictly-Windows part of each abstraction; alternatively, isolate it in the code itself with OperatingSystem.IsWindows() / #if."));
        steps.Add(Loc.T(lang,
            "Dejar preparado el seam: la implementación NO-Windows de las abstracciones queda pendiente y a cargo de otro equipo (este análisis no la desarrolla ni prescribe la plataforma destino).",
            "Leave the seam ready: the non-Windows implementation of the abstractions is pending and up to another team (this analysis neither develops it nor prescribes the target platform)."));
        if (hasUi)
            steps.Add(Loc.T(lang,
                $"Mantener la UI WPF en {baseName}.App.Windows y trasladar los ViewModels/lógica al núcleo portable para reutilizarlos cuando otro equipo aporte la UI no-Windows.",
                $"Keep the WPF UI in {baseName}.App.Windows and move the ViewModels/logic to the portable core to reuse them when another team provides the non-Windows UI."));
        steps.Add(Loc.T(lang,
            "Configurar pruebas y CI que compilen el núcleo portable de forma multiplataforma (matriz de build).",
            "Set up tests and CI that compile the portable core cross-platform (build matrix)."));

        var totalWithTesting = report.CostByBucket.Aggregate(EffortEstimate.Zero, (a, b) => a.Add(b.Effort));

        return new ArchitecturePlan(projects, abstractions, replacements, steps, BuildRoleNotes(report, lang), hasUi,
            totalWithTesting, report.BlockerCount, BuildWorkedExample(report, baseName, lang));
    }

    /// <summary>Ejemplo practico, con datos reales del proyecto, de como quedaria resuelto un caso tipico.</summary>
    private static string? BuildWorkedExample(AnalysisReport report, string baseName, Lang lang)
    {
        var rep = report.SourceFindings
            .GroupBy(f => f.Categoria, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?
            .OrderBy(f => string.IsNullOrEmpty(f.Clase) ? 1 : 0)
            .FirstOrDefault();
        if (rep is null) return null;

        var clase = string.IsNullOrEmpty(rep.Clase)
            ? Loc.T(lang, "la clase afectada", "the affected class")
            : Loc.T(lang, $"la clase '{rep.Clase}'", $"class '{rep.Clase}'");
        var sym = string.IsNullOrEmpty(rep.Symbol) ? Loc.T(lang, "una API de Windows", "a Windows API") : $"'{rep.Symbol}'";
        var loc = $"{rep.File}:{rep.Line}";

        return rep.Categoria.ToUpperInvariant() switch
        {
            "UI" => Loc.T(lang,
                $"Ejemplo práctico (caso UI). En {loc}, {clase} usa {sym} (interfaz de usuario de Windows). Cómo queda resuelto: (1) se mueve la lógica y los ViewModels de {clase} a {baseName}.Core (portable, sin WPF/WinForms); (2) la ventana/controles ({sym}) quedan solo en el proyecto Windows ({baseName}.App.Windows); (3) donde el núcleo necesite mostrar algo al usuario, se llama a una interfaz (p. ej. INotificador) que en Windows muestra el diálogo y cuya versión no-Windows se deja preparada. Resultado: el núcleo compila sin depender de la UI de Windows.",
                $"Worked example (UI case). At {loc}, {clase} uses {sym} (Windows user interface). How it is solved: (1) move the logic and ViewModels of {clase} to {baseName}.Core (portable, no WPF/WinForms); (2) the window/controls ({sym}) stay only in the Windows project ({baseName}.App.Windows); (3) wherever the core needs to show something to the user, it calls an interface (e.g. INotifier) that shows the dialog on Windows and whose non-Windows version is left ready. Result: the core compiles without depending on the Windows UI."),
            "DATABASE" => Loc.T(lang,
                $"Ejemplo práctico (caso datos). En {loc}, {clase} usa {sym} (cliente de base de datos atado a Windows). Cómo queda resuelto: se cambia el paquete por Oracle.ManagedDataAccess.Core (gestionado y portable); la API es casi idéntica, solo hay que revisar la cadena de conexión. El código de {clase} apenas cambia y pasa a compilar en cualquier SO.",
                $"Worked example (data case). At {loc}, {clase} uses {sym} (a Windows-bound database client). How it is solved: switch the package to Oracle.ManagedDataAccess.Core (managed and portable); the API is almost identical, only the connection string needs review. The code of {clase} barely changes and now compiles on any OS."),
            "REGISTRY" => Loc.T(lang,
                $"Ejemplo práctico (caso configuración). En {loc}, {clase} lee del Registro de Windows con {sym}. Cómo queda resuelto: (1) se define una interfaz ISettingsStore (el «seam») en {baseName}.Abstractions; (2) {clase} pasa a leer de ISettingsStore en vez del Registro; (3) en Windows se implementa con el Registro y, de forma portable, con appsettings.json/variables de entorno. El núcleo deja de depender del Registro.",
                $"Worked example (configuration case). At {loc}, {clase} reads from the Windows Registry with {sym}. How it is solved: (1) define an ISettingsStore interface (the \"seam\") in {baseName}.Abstractions; (2) {clase} now reads from ISettingsStore instead of the Registry; (3) on Windows it is implemented with the Registry and, portably, with appsettings.json/environment variables. The core no longer depends on the Registry."),
            _ => Loc.T(lang,
                $"Ejemplo práctico. En {loc}, {clase} usa {sym}, que solo funciona en Windows. Cómo queda resuelto: (1) se mueve la lógica de {clase} a {baseName}.Core; (2) el uso de {sym} se sustituye por una interfaz (el «seam») en {baseName}.Abstractions; (3) la implementación con {sym} queda en {baseName}.Platform.Windows y la versión no-Windows se deja preparada tras la interfaz. Así el núcleo compila sin ataduras de SO y lo específico de Windows queda encapsulado y sustituible.",
                $"Worked example. At {loc}, {clase} uses {sym}, which only works on Windows. How it is solved: (1) move the logic of {clase} to {baseName}.Core; (2) replace the use of {sym} with an interface (the \"seam\") in {baseName}.Abstractions; (3) the implementation with {sym} stays in {baseName}.Platform.Windows and the non-Windows version is left ready behind the interface. Thus the core compiles without OS ties and the Windows-specific parts are encapsulated and replaceable.")
        };
    }

    /// <summary>Explicacion sencilla del porque una dependencia no es portable.</summary>
    private static string WhyFor(string src, Lang lang)
    {
        var s = src.ToLowerInvariant();
        if (s.Contains("oracleclient") || s.Contains("oracle.dataaccess")) return Loc.T(lang, "el cliente clásico usa código nativo de Windows; la variante gestionada (.Core) es 100% portable.", "the classic client uses native Windows code; the managed variant (.Core) is 100% portable.");
        if (s.Contains("sqlclient")) return Loc.T(lang, "System.Data.SqlClient quedó atado a Windows; Microsoft.Data.SqlClient es su sucesor multiplataforma.", "System.Data.SqlClient became Windows-bound; Microsoft.Data.SqlClient is its cross-platform successor.");
        if (s.Contains("gdi32") || s.Contains("system.drawing")) return Loc.T(lang, "es una librería de gráficos NATIVA de Windows; en multiplataforma se usa una librería de gráficos gestionada (SkiaSharp/ImageSharp).", "it is a NATIVE Windows graphics library; cross-platform uses a managed graphics library (SkiaSharp/ImageSharp).");
        if (s.Contains("user32") || s.Contains("kernel32") || s.Contains("advapi32") || s.EndsWith(".dll")) return Loc.T(lang, "es una DLL nativa del sistema Windows (P/Invoke); no existe en otros SO, hay que usar la API gestionada equivalente o aislarla por SO.", "it is a native Windows system DLL (P/Invoke); it does not exist on other OSes, use the managed equivalent or isolate it per OS.");
        if (s.Contains("eventlog")) return Loc.T(lang, "el Visor de eventos es exclusivo de Windows; un framework de logging portable escribe a consola/fichero.", "the Event Viewer is Windows-only; a portable logging framework writes to console/file.");
        if (s.Contains("performancecounter")) return Loc.T(lang, "los contadores de rendimiento son de Windows; EventCounters/Metrics son la alternativa portable.", "performance counters are Windows-specific; EventCounters/Metrics are the portable alternative.");
        if (s.Contains("messaging")) return Loc.T(lang, "MSMQ es de Windows; una cola multiplataforma (RabbitMQ/Service Bus) cumple la misma función.", "MSMQ is Windows-specific; a cross-platform queue (RabbitMQ/Service Bus) serves the same purpose.");
        if (s.Contains("servicemodel")) return Loc.T(lang, "WCF clásico es de Windows; CoreWCF o gRPC/ASP.NET Core son portables.", "classic WCF is Windows-specific; CoreWCF or gRPC/ASP.NET Core are portable.");
        if (s.Contains("directoryservices")) return Loc.T(lang, "la integración nativa con Active Directory es de Windows; un cliente LDAP portable la sustituye.", "native Active Directory integration is Windows-specific; a portable LDAP client replaces it.");
        if (s.Contains("protecteddata")) return Loc.T(lang, "DPAPI es exclusivo de Windows; se cifra con AES y una clave gestionada externamente.", "DPAPI is Windows-only; encrypt with AES and an externally-managed key.");
        return Loc.T(lang, "no es portable a otros sistemas operativos; se sustituye por una alternativa gestionada multiplataforma o se aísla por SO.", "it is not portable to other operating systems; replace it with a managed cross-platform alternative or isolate it per OS.");
    }

    /// <summary>Notas segun el rol configurado de cada proyecto (API obligatoria, no modificable, divisible).</summary>
    private static IReadOnlyList<string> BuildRoleNotes(AnalysisReport report, Lang lang)
    {
        var notes = new List<string>();
        if (report.Roles.IsEmpty) return notes;

        foreach (var a in report.Assemblies.Where(x => x.Classification.Kind == AssemblyKind.Managed))
        {
            var name = a.Classification.Name;
            var confirmados = a.ConfirmedFindings().Count();
            var bloqueantes = a.ConfirmedFindings().Count(f => f.EsBloqueante);
            switch (report.Roles.RoleOf(name))
            {
                case ProjectRole.ObligatorioMultiplataforma:
                    notes.Add(Loc.T(lang,
                        $"{name} - debe ser multiplataforma (PRIORIDAD MAXIMA). Ya convertida en API en otra rama; aqui se analizan los CAMBIOS necesarios para que sea multiplataforma. Bloqueantes a resolver: {bloqueantes}.",
                        $"{name} - must be cross-platform (TOP PRIORITY). Already turned into an API in another branch; here we analyze the CHANGES needed to make it cross-platform. Blocking points to resolve: {bloqueantes}."));
                    break;
                case ProjectRole.NoModificable:
                    notes.Add(Loc.T(lang,
                        $"{name} - proveedor externo, NO MODIFICABLE: la adaptacion la debe hacer el proveedor. Su esfuerzo no se imputa a nuestro total; verificar si existe version/soporte multiplataforma del paquete (ver 'Terceros no modificables: restriccion y opciones viables').",
                        $"{name} - external vendor, NON-MODIFIABLE: the vendor must adapt it. Its effort is not charged to our total; check whether a cross-platform version/support of the package exists (see 'Non-modifiable third parties: constraint and viable options')."));
                    break;
                case ProjectRole.DivisiblePorUI:
                    notes.Add(Loc.T(lang,
                        $"{name} - DIVIDIR: extraer lo dependiente de Windows a un proyecto nuevo (p. ej. {name}.Windows) - {confirmados} usos Windows detectados - y dejar {name} limpio/multiplataforma.",
                        $"{name} - SPLIT: extract the Windows-dependent parts to a new project (e.g. {name}.Windows) - {confirmados} Windows uses detected - and keep {name} clean/cross-platform."));
                    break;
            }
        }
        return notes;
    }

    private static string ShortName(string evidence)
    {
        var comma = evidence.IndexOf(',');
        return (comma > 0 ? evidence[..comma] : evidence).Trim();
    }

    // Equivalente multiplataforma conocido por dependencia (origen -> destino ES / EN).
    private static readonly (string Src, string Es, string En)[] ReplacementTargets =
    {
        ("System.Data.OracleClient", "Oracle.ManagedDataAccess.Core (ODP.NET gestionado)", "Oracle.ManagedDataAccess.Core (managed ODP.NET)"),
        ("Oracle.DataAccess", "Oracle.ManagedDataAccess.Core", "Oracle.ManagedDataAccess.Core"),
        ("Oracle.ManagedDataAccess", "Oracle.ManagedDataAccess.Core", "Oracle.ManagedDataAccess.Core"),
        ("System.Data.SqlClient", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient"),
        ("System.Drawing.Common", "SkiaSharp o ImageSharp", "SkiaSharp or ImageSharp"),
        ("gdi32", "SkiaSharp o ImageSharp (gráficos multiplataforma)", "SkiaSharp or ImageSharp (cross-platform graphics)"),
        ("System.Diagnostics.EventLog", "Serilog / Microsoft.Extensions.Logging (fichero/consola)", "Serilog / Microsoft.Extensions.Logging (file/console)"),
        ("System.Diagnostics.PerformanceCounter", "EventCounters / System.Diagnostics.Metrics", "EventCounters / System.Diagnostics.Metrics"),
        ("System.Messaging", "RabbitMQ o Azure Service Bus", "RabbitMQ or Azure Service Bus"),
        ("System.ServiceModel", "CoreWCF o gRPC / ASP.NET Core", "CoreWCF or gRPC / ASP.NET Core"),
        ("System.DirectoryServices", "System.DirectoryServices.Protocols o Novell.Directory.Ldap", "System.DirectoryServices.Protocols or Novell.Directory.Ldap"),
        ("System.Speech", "servicio de voz multiplataforma (motor externo/cloud)", "cross-platform speech service (external/cloud engine)"),
        ("System.Security.Cryptography.ProtectedData", "AES con clave externa / gestor de secretos", "AES with an external key / secrets manager")
    };

    /// <summary>Equivalente multiplataforma sugerido para una dependencia, o null si no se conoce.</summary>
    private static string? TargetFor(string src, Lang lang)
    {
        foreach (var t in ReplacementTargets)
            if (src.Equals(t.Src, StringComparison.OrdinalIgnoreCase) || src.StartsWith(t.Src, StringComparison.OrdinalIgnoreCase))
                return lang == Lang.En ? t.En : t.Es;
        return null;
    }
}
