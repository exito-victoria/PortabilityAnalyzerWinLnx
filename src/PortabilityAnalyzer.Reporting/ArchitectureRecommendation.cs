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
/// Deriva del informe una arquitectura destino PORTABLE-FIRST: un nucleo net8.0 multiplataforma lo mas
/// grande posible, una capa de abstracciones (seam) y una unica pieza atada a Windows (Platform.Windows)
/// con lo que hoy exige Windows. NO prescribe la implementacion de otra plataforma: deja el seam
/// preparado para que otro equipo lo desarrolle. Las abstracciones y los reemplazos salen de las
/// estrategias de separacion detectadas en los hallazgos.
/// </summary>
public static class ArchitectureRecommendation
{
    private static readonly Dictionary<string, string> AbstractionByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Registry"] = "ISettingsStore - configuración externa en vez del Registro",
        ["Identity"] = "IUserIdentity / IAuthenticationService - identidad y autenticación",
        ["Threading"] = "IInterProcessLock - sincronización/señalización entre procesos",
        ["PInvoke"] = "INativePlatform - llamadas nativas específicas por SO",
        ["ProcessInvocation"] = "IProcessRunner - ejecución de comandos del SO",
        ["COM"] = "Interfaz del servicio COM afectado",
        ["Cryptography"] = "IProtectedDataStore - protección de secretos (si usa DPAPI)",
        ["Misc"] = "Abstracción específica según el caso"
    };

    public static ArchitecturePlan Build(AnalysisReport report)
    {
        var confirmed = report.Assemblies.SelectMany(a => a.ConfirmedFindings()).ToList();
        var hasUi = confirmed.Any(f => f.EstrategiaSeparacion == SeparationStrategy.RedisenoUI);

        var abstractions = confirmed
            .Where(f => f.EstrategiaSeparacion == SeparationStrategy.AbstraerPorPlataforma)
            .Select(f => f.Categoria)
            .Where(c => AbstractionByCategory.ContainsKey(c))
            .Select(c => AbstractionByCategory[c])
            .Distinct()
            .ToList();

        // Cada reemplazo indica el equivalente multiplataforma SUGERIDO y el PORQUE en lenguaje sencillo.
        var replacements = confirmed
            .Where(f => f.EstrategiaSeparacion == SeparationStrategy.ReemplazarDependencia && !string.IsNullOrEmpty(f.Evidencia))
            .Select(f => ShortName(f.Evidencia!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(src =>
            {
                var tgt = TargetFor(src) ?? "buscar un equivalente multiplataforma gestionado";
                return $"{src} -> {tgt}. Por qué: {WhyFor(src).TrimEnd('.')}";
            })
            .Take(12)
            .ToList();

        var baseName = (report.Assemblies
            .Where(a => !a.IsThirdParty && a.Classification.Kind == AssemblyKind.Managed)
            .Select(a => a.Classification.Name)
            .FirstOrDefault() ?? "App").Replace(" ", string.Empty);

        // Arquitectura PORTABLE-FIRST: todo lo que se pueda va al nucleo multiplataforma; solo lo que sea
        // obligatoriamente Windows queda aislado en Platform.Windows. La implementacion NO-Windows de las
        // abstracciones NO se disena aqui: se deja un "seam" (contrato) preparado para que otro equipo la
        // aporte mas adelante. No se prescribe UI ni implementacion de otra plataforma.
        var projects = new List<RecommendedProject>
        {
            new($"{baseName}.Core", "net8.0", "Núcleo multiplataforma: toda la lógica portable (sin dependencias de SO). Objetivo: que sea lo más grande posible."),
            new($"{baseName}.Abstractions", "net8.0", "Interfaces (seam) de las capacidades que dependen del SO; el núcleo solo depende de estas interfaces."),
            new($"{baseName}.Platform.Windows", "net8.0-windows", "Única pieza atada a Windows: implementación de las abstracciones que hoy requieren Windows (Registro, identidad, P/Invoke).")
        };
        if (hasUi)
            projects.Add(new($"{baseName}.App.Windows", "net8.0-windows", "UI actual en WPF (Windows). Los ViewModels/lógica se llevan al núcleo portable para poder reutilizarse."));
        else
            projects.Add(new($"{baseName}.Host", "net8.0", "Ejecutable/servicio multiplataforma (host portable)."));

        var steps = new List<string>
        {
            $"PORTABLE-FIRST: llevar toda la lógica posible a {baseName}.Core (net8.0), sin referencias a WPF/WinForms/Win32; que el núcleo compile y corra sin ataduras de SO."
        };
        if (abstractions.Count > 0)
            steps.Add($"Aislar lo que hoy exige Windows tras interfaces en {baseName}.Abstractions e inyectarlas por DI (seam): {string.Join("; ", abstractions)}.");
        if (replacements.Count > 0)
            steps.Add($"Sustituir cada dependencia no portable por el equivalente multiplataforma sugerido (con el porqué; " +
                      $"el detalle y los pasos están en 'Alternativa portable / multiplataforma' y 'Pasos de remediación', y hay ejemplos de código en el apéndice): {string.Join(" | ", replacements)}.");
        steps.Add($"Implementar en {baseName}.Platform.Windows solo la parte obligatoriamente Windows de cada abstracción; alternativamente, aislarla en el propio código con OperatingSystem.IsWindows() / #if.");
        steps.Add($"Dejar preparado el seam: la implementación NO-Windows de las abstracciones queda pendiente y a cargo de otro equipo (este análisis no la desarrolla ni prescribe la plataforma destino).");
        if (hasUi)
            steps.Add($"Mantener la UI WPF en {baseName}.App.Windows y trasladar los ViewModels/lógica al núcleo portable para reutilizarlos cuando otro equipo aporte la UI no-Windows.");
        steps.Add("Configurar pruebas y CI que compilen el núcleo portable de forma multiplataforma (matriz de build).");

        var roleNotes = BuildRoleNotes(report);

        var totalWithTesting = report.CostByBucket.Aggregate(EffortEstimate.Zero, (a, b) => a.Add(b.Effort));

        return new ArchitecturePlan(projects, abstractions, replacements, steps, roleNotes, hasUi, totalWithTesting,
            report.BlockerCount, BuildWorkedExample(report, baseName));
    }

    /// <summary>Ejemplo practico, con datos reales del proyecto, de como quedaria resuelto un caso tipico.</summary>
    private static string? BuildWorkedExample(AnalysisReport report, string baseName)
    {
        // Categoria mas frecuente en el codigo fuente (mas concreto) y un hallazgo representativo.
        var rep = report.SourceFindings
            .GroupBy(f => f.Categoria, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?
            .OrderBy(f => string.IsNullOrEmpty(f.Clase) ? 1 : 0)
            .FirstOrDefault();
        if (rep is null) return null;

        var clase = string.IsNullOrEmpty(rep.Clase) ? "la clase afectada" : $"la clase '{rep.Clase}'";
        var sym = string.IsNullOrEmpty(rep.Symbol) ? "una API de Windows" : $"'{rep.Symbol}'";
        var loc = $"{rep.File}:{rep.Line}";

        return rep.Categoria.ToUpperInvariant() switch
        {
            "UI" =>
                $"Ejemplo práctico (caso UI). En {loc}, {clase} usa {sym} (interfaz de usuario de Windows). " +
                $"Cómo queda resuelto: (1) se mueve la lógica y los ViewModels de {clase} a {baseName}.Core (portable, sin WPF/WinForms); " +
                $"(2) la ventana/controles ({sym}) quedan solo en el proyecto Windows ({baseName}.App.Windows); " +
                $"(3) donde el núcleo necesite mostrar algo al usuario, se llama a una interfaz (p. ej. INotificador) que en Windows muestra el diálogo y cuya versión no-Windows se deja preparada. Resultado: el núcleo compila sin depender de la UI de Windows.",
            "DATABASE" =>
                $"Ejemplo práctico (caso datos). En {loc}, {clase} usa {sym} (cliente de base de datos atado a Windows). " +
                $"Cómo queda resuelto: se cambia el paquete por Oracle.ManagedDataAccess.Core (gestionado y portable); la API es casi idéntica, solo hay que revisar la cadena de conexión. El código de {clase} apenas cambia y pasa a compilar en cualquier SO.",
            "REGISTRY" =>
                $"Ejemplo práctico (caso configuración). En {loc}, {clase} lee del Registro de Windows con {sym}. " +
                $"Cómo queda resuelto: (1) se define una interfaz ISettingsStore (el «seam») en {baseName}.Abstractions; (2) {clase} pasa a leer de ISettingsStore en vez del Registro; (3) en Windows se implementa con el Registro y, de forma portable, con appsettings.json/variables de entorno. El núcleo deja de depender del Registro.",
            _ =>
                $"Ejemplo práctico. En {loc}, {clase} usa {sym}, que solo funciona en Windows. " +
                $"Cómo queda resuelto: (1) se mueve la lógica de {clase} a {baseName}.Core; (2) el uso de {sym} se sustituye por una interfaz (el «seam») en {baseName}.Abstractions; (3) la implementación con {sym} queda en {baseName}.Platform.Windows y la versión no-Windows se deja preparada tras la interfaz. Así el núcleo compila sin ataduras de SO y lo específico de Windows queda encapsulado y sustituible."
        };
    }

    /// <summary>Explicacion sencilla del porque una dependencia no es portable.</summary>
    private static string WhyFor(string src)
    {
        var s = src.ToLowerInvariant();
        if (s.Contains("oracleclient") || s.Contains("oracle.dataaccess")) return "el cliente clásico usa código nativo de Windows; la variante gestionada (.Core) es 100% portable.";
        if (s.Contains("sqlclient")) return "System.Data.SqlClient quedó atado a Windows; Microsoft.Data.SqlClient es su sucesor multiplataforma.";
        if (s.Contains("gdi32") || s.Contains("system.drawing")) return "es una librería de gráficos NATIVA de Windows; en multiplataforma se usa una librería de gráficos gestionada (SkiaSharp/ImageSharp).";
        if (s.Contains("user32") || s.Contains("kernel32") || s.Contains("advapi32") || s.EndsWith(".dll")) return "es una DLL nativa del sistema Windows (P/Invoke); no existe en otros SO, hay que usar la API gestionada equivalente o aislarla por SO.";
        if (s.Contains("eventlog")) return "el Visor de eventos es exclusivo de Windows; un framework de logging portable escribe a consola/fichero.";
        if (s.Contains("performancecounter")) return "los contadores de rendimiento son de Windows; EventCounters/Metrics son la alternativa portable.";
        if (s.Contains("messaging")) return "MSMQ es de Windows; una cola multiplataforma (RabbitMQ/Service Bus) cumple la misma función.";
        if (s.Contains("servicemodel")) return "WCF clásico es de Windows; CoreWCF o gRPC/ASP.NET Core son portables.";
        if (s.Contains("directoryservices")) return "la integración nativa con Active Directory es de Windows; un cliente LDAP portable la sustituye.";
        if (s.Contains("protecteddata")) return "DPAPI es exclusivo de Windows; se cifra con AES y una clave gestionada externamente.";
        return "no es portable a otros sistemas operativos; se sustituye por una alternativa gestionada multiplataforma o se aísla por SO.";
    }

    /// <summary>Notas segun el rol configurado de cada proyecto (API obligatoria, no modificable, divisible).</summary>
    private static IReadOnlyList<string> BuildRoleNotes(AnalysisReport report)
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
                    notes.Add($"{name} - debe ser multiplataforma (PRIORIDAD MAXIMA). Ya convertida en API en otra rama; aqui se analizan los CAMBIOS necesarios para que sea multiplataforma. Bloqueantes a resolver: {bloqueantes}.");
                    break;
                case ProjectRole.NoModificable:
                    notes.Add($"{name} - proveedor externo, NO MODIFICABLE: la adaptacion la debe hacer el proveedor. Su esfuerzo no se imputa a nuestro total; verificar si existe version/soporte multiplataforma del paquete (ver 'Terceros no modificables: restriccion y opciones viables').");
                    break;
                case ProjectRole.DivisiblePorUI:
                    notes.Add($"{name} - DIVIDIR: extraer lo dependiente de Windows a un proyecto nuevo (p. ej. {name}.Windows) - {confirmados} usos Windows detectados - y dejar {name} limpio/multiplataforma.");
                    break;
            }
        }
        return notes;
    }

    /// <summary>Nombre simple de una evidencia (recorta el nombre completo de ensamblado antes de la coma).</summary>
    private static string ShortName(string evidence)
    {
        var comma = evidence.IndexOf(',');
        return (comma > 0 ? evidence[..comma] : evidence).Trim();
    }

    // Equivalente multiplataforma conocido por dependencia (origen -> destino sugerido).
    private static readonly (string Src, string Target)[] ReplacementTargets =
    {
        ("System.Data.OracleClient", "Oracle.ManagedDataAccess.Core (ODP.NET gestionado)"),
        ("Oracle.DataAccess", "Oracle.ManagedDataAccess.Core"),
        ("Oracle.ManagedDataAccess", "Oracle.ManagedDataAccess.Core"),
        ("System.Data.SqlClient", "Microsoft.Data.SqlClient"),
        ("System.Drawing.Common", "SkiaSharp o ImageSharp"),
        ("gdi32", "SkiaSharp o ImageSharp (gráficos multiplataforma)"),
        ("System.Diagnostics.EventLog", "Serilog / Microsoft.Extensions.Logging (fichero/syslog)"),
        ("System.Diagnostics.PerformanceCounter", "EventCounters / System.Diagnostics.Metrics"),
        ("System.Messaging", "RabbitMQ o Azure Service Bus"),
        ("System.ServiceModel", "CoreWCF o gRPC / ASP.NET Core"),
        ("System.DirectoryServices", "System.DirectoryServices.Protocols o Novell.Directory.Ldap"),
        ("System.Speech", "servicio de voz multiplataforma (motor externo/cloud)"),
        ("System.Security.Cryptography.ProtectedData", "AES con clave externa / gestor de secretos")
    };

    /// <summary>Equivalente multiplataforma sugerido para una dependencia, o null si no se conoce.</summary>
    private static string? TargetFor(string src) =>
        ReplacementTargets.FirstOrDefault(t =>
            src.Equals(t.Src, StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith(t.Src, StringComparison.OrdinalIgnoreCase)).Target;
}
