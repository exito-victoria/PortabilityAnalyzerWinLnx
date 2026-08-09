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
/// Deriva del informe una arquitectura destino concreta (core net8.0 comun + WPF en Windows y Avalonia
/// en Linux, con capa de abstraccion por plataforma) y un plan de migracion por pasos. Las abstracciones
/// y los reemplazos salen de las estrategias de separacion detectadas en los hallazgos.
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

        var projects = new List<RecommendedProject>
        {
            new($"{baseName}.Core", "net8.0", "Lógica de negocio común (portable, sin dependencias de SO)."),
            new($"{baseName}.Abstractions", "net8.0", "Interfaces de las capacidades dependientes de plataforma."),
            new($"{baseName}.Platform.Windows", "net8.0-windows", "Implementación Windows de las abstracciones (Registro, identidad, P/Invoke)."),
            new($"{baseName}.Platform.Linux", "net8.0", "Implementación Linux de las abstracciones (config, LDAP/Kerberos, equivalentes o no-op).")
        };
        if (hasUi)
        {
            projects.Add(new($"{baseName}.App.Windows", "net8.0-windows", "UI actual en WPF (Windows)."));
            projects.Add(new($"{baseName}.App.Linux", "net8.0", "UI de Linux en Avalonia (MVVM), reutilizando ViewModels del core."));
        }
        else
        {
            projects.Add(new($"{baseName}.Host", "net8.0", "Ejecutable/servicio multiplataforma (host común)."));
        }

        var steps = new List<string>
        {
            $"Extraer la lógica de negocio a {baseName}.Core (net8.0), sin referencias a WPF/WinForms/Win32."
        };
        if (abstractions.Count > 0)
            steps.Add($"Definir las abstracciones en {baseName}.Abstractions e inyectarlas por DI: {string.Join("; ", abstractions)}.");
        if (replacements.Count > 0)
            steps.Add($"Reemplazar las dependencias no portables por su equivalente multiplataforma " +
                      $"(el detalle y los pasos están en 'Alternativa Linux' y 'Pasos de remediación' de cada dependencia): {string.Join("; ", replacements)}.");
        steps.Add($"Implementar {baseName}.Platform.Windows y {baseName}.Platform.Linux con la versión por SO de cada abstracción.");
        if (hasUi)
            steps.Add($"Mantener la UI WPF en {baseName}.App.Windows e implementar la UI de Linux en {baseName}.App.Linux con Avalonia, reutilizando ViewModels.");
        steps.Add("Configurar pruebas y CI que compilen y ejecuten en Windows y Linux (matriz de build).");

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
                    notes.Add($"{name} - API obligatoria multiplataforma (PRIORIDAD MAXIMA). Hoy no es API: debe convertirse en API multiplataforma. Bloqueantes a resolver: {bloqueantes}.");
                    break;
                case ProjectRole.NoModificable:
                    notes.Add($"{name} - proveedor externo, NO MODIFICABLE: la adaptacion la debe hacer el proveedor. Su esfuerzo no se imputa a nuestro total; verificar si existe version/soporte Linux del paquete.");
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
