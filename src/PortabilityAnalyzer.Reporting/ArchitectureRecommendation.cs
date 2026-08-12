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
    int Blockers);

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

        var replacements = confirmed
            .Where(f => f.EstrategiaSeparacion == SeparationStrategy.ReemplazarDependencia && !string.IsNullOrEmpty(f.Evidencia))
            .Select(f => ShortName(f.Evidencia!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(src => TargetFor(src) is { } tgt ? $"{src} -> {tgt}" : $"{src} (buscar equivalente multiplataforma)")
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
            steps.Add($"Reemplazar las dependencias no portables por su equivalente multiplataforma " +
                      $"(el detalle y los pasos están en 'Alternativa portable / multiplataforma' y 'Pasos de remediación' de cada dependencia): {string.Join("; ", replacements)}.");
        steps.Add($"Implementar en {baseName}.Platform.Windows solo la parte obligatoriamente Windows de cada abstracción; alternativamente, aislarla en el propio código con OperatingSystem.IsWindows() / #if.");
        steps.Add($"Dejar preparado el seam: la implementación NO-Windows de las abstracciones queda pendiente y a cargo de otro equipo (este análisis no la desarrolla ni prescribe la plataforma destino).");
        if (hasUi)
            steps.Add($"Mantener la UI WPF en {baseName}.App.Windows y trasladar los ViewModels/lógica al núcleo portable para reutilizarlos cuando otro equipo aporte la UI no-Windows.");
        steps.Add("Configurar pruebas y CI que compilen el núcleo portable de forma multiplataforma (matriz de build).");

        var roleNotes = BuildRoleNotes(report);

        var totalWithTesting = report.CostByBucket.Aggregate(EffortEstimate.Zero, (a, b) => a.Add(b.Effort));

        return new ArchitecturePlan(projects, abstractions, replacements, steps, roleNotes, hasUi, totalWithTesting, report.BlockerCount);
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
