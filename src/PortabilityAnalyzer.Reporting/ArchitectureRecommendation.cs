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
            .Take(10)
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
            steps.Add($"Reemplazar las dependencias no portables por equivalentes multiplataforma: {string.Join(", ", replacements)}.");
        steps.Add($"Implementar {baseName}.Platform.Windows y {baseName}.Platform.Linux con la versión por SO de cada abstracción.");
        if (hasUi)
            steps.Add($"Mantener la UI WPF en {baseName}.App.Windows e implementar la UI de Linux en {baseName}.App.Linux con Avalonia, reutilizando ViewModels.");
        steps.Add("Configurar pruebas y CI que compilen y ejecuten en Windows y Linux (matriz de build).");

        var totalWithTesting = report.CostByBucket.Aggregate(EffortEstimate.Zero, (a, b) => a.Add(b.Effort));

        return new ArchitecturePlan(projects, abstractions, replacements, steps, hasUi, totalWithTesting, report.BlockerCount);
    }

    /// <summary>Nombre simple de una evidencia (recorta el nombre completo de ensamblado antes de la coma).</summary>
    private static string ShortName(string evidence)
    {
        var comma = evidence.IndexOf(',');
        return (comma > 0 ? evidence[..comma] : evidence).Trim();
    }
}
