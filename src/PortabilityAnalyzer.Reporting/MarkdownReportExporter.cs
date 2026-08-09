using System.Text;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Exporta un informe legible por humanos en Markdown (resumen + detalle por ensamblado).</summary>
public sealed class MarkdownReportExporter : IReportExporter
{
    public string Format => "markdown";

    public void Export(AnalysisReport report, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Informe de análisis multiplataforma (.NET 8) - Windows / Linux");
        sb.AppendLine();
        sb.AppendLine($"Generado: {report.GeneratedAt:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine($"Analizados: {report.AnalyzedCount} | Omitidos: {report.SkippedCount} | Con bloqueantes: {report.BlockerCount}  ");
        sb.AppendLine($"Esfuerzo total de desarrollo: optimista {report.TotalEffort.Optimista:0.#} h | media {report.TotalEffort.Media:0.#} h | pesimista {report.TotalEffort.Pesimista:0.#} h");
        sb.AppendLine();
        AppendEstimationNote(sb);

        AppendBucketSummary(sb, report.CostByBucket);
        AppendArchitectureSection(sb, report);
        AppendThirdPartySection(sb, report);
        AppendSourceSection(sb, report);

        foreach (var asm in report.Assemblies.OrderByDescending(a => a.MaxSeverity))
        {
            sb.AppendLine($"## {asm.Classification.Name} ({asm.Classification.Kind})");

            var confirmed = ReportGrouping.Group(asm.ConfirmedFindings());
            var manual = ReportGrouping.Group(asm.ManualReviewFindings());

            if (confirmed.Count == 0 && manual.Count == 0)
            {
                sb.AppendLine(asm.Classification.Reason ?? "Sin hallazgos.");
                sb.AppendLine();
                continue;
            }

            var terceros = asm.IsThirdParty ? " | Terceros (factor de incertidumbre aplicado)" : string.Empty;
            sb.AppendLine($"Severidad máxima: {asm.MaxSeverity} | Esfuerzo medio: {asm.Effort.Media:0.#} h{terceros}");
            sb.AppendLine();

            if (confirmed.Count > 0)
                AppendConfirmedTable(sb, confirmed);
            else
            {
                sb.AppendLine("Sin hallazgos confirmados (solo señales débiles, ver abajo).");
                sb.AppendLine();
            }

            if (manual.Count > 0)
            {
                var ocurrencias = manual.Sum(g => g.Count);
                sb.AppendLine($"### Revisión manual - señal débil, excluida del esfuerzo ({manual.Count} grupos / {ocurrencias} ocurrencias)");
                sb.AppendLine();
                AppendManualTable(sb, manual);
            }
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>Recomendacion de arquitectura destino y plan de migración (sintetizado del analisis).</summary>
    private static void AppendArchitectureSection(StringBuilder sb, AnalysisReport report)
    {
        var plan = ArchitectureRecommendation.Build(report);

        sb.AppendLine("## Arquitectura destino recomendada y plan de migración");
        sb.AppendLine();
        sb.AppendLine($"Objetivo: **core .NET 8 común** + **WPF en Windows** y **Avalonia en Linux**. Esfuerzo total estimado (con Pruebas y CI): **{plan.TotalWithTesting.Media:0.#} h** (optimista {plan.TotalWithTesting.Optimista:0.#} / pesimista {plan.TotalWithTesting.Pesimista:0.#}). Bloqueantes: **{plan.Blockers}**.");
        sb.AppendLine();

        if (plan.RoleNotes.Count > 0)
        {
            sb.AppendLine("### Roles y restricciones");
            sb.AppendLine();
            foreach (var n in plan.RoleNotes)
                sb.AppendLine($"- {Cell(n)}");
            sb.AppendLine();
        }

        sb.AppendLine("### Estructura de proyectos propuesta");
        sb.AppendLine();
        sb.AppendLine("| Proyecto | TFM | Propósito |");
        sb.AppendLine("|----------|-----|-----------|");
        foreach (var p in plan.Projects)
            sb.AppendLine($"| {Cell(p.Name)} | {p.Tfm} | {Cell(p.Purpose)} |");
        sb.AppendLine();

        if (plan.Abstractions.Count > 0)
        {
            sb.AppendLine("### Capa de abstracción (interfaces por plataforma)");
            sb.AppendLine();
            foreach (var a in plan.Abstractions)
                sb.AppendLine($"- {a}");
            sb.AppendLine();
        }

        sb.AppendLine("### Plan de migración");
        sb.AppendLine();
        for (int i = 0; i < plan.MigrationSteps.Count; i++)
            sb.AppendLine($"{i + 1}. {plan.MigrationSteps[i]}");
        sb.AppendLine();
    }

    /// <summary>Analisis a nivel de codigo fuente: fichero/linea/segmento + como corregir.</summary>
    private static void AppendSourceSection(StringBuilder sb, AnalysisReport report)
    {
        var findings = report.SourceFindings;
        if (findings.Count == 0) return;

        var files = findings.Select(f => f.File).Distinct().Count();
        sb.AppendLine("## Análisis de código fuente (dónde y cómo corregir)");
        sb.AppendLine();
        sb.AppendLine("> Usos de APIs propias de Windows localizados en el **código fuente** (Roslyn): fichero, línea, el segmento de código y cómo corregirlo para multiplataforma.");
        sb.AppendLine();
        sb.AppendLine($"Total: **{findings.Count}** usos en **{files}** ficheros.");
        sb.AppendLine();
        sb.AppendLine("| Proyecto | Fichero:línea | Tipo | Símbolo | Clase / Método | Segmento de código | Cómo corregir |");
        sb.AppendLine("|----------|---------------|------|---------|----------------|--------------------|---------------|");
        foreach (var f in findings)
        {
            var loc = string.Join(" / ", new[] { f.Clase, f.Metodo }.Where(x => !string.IsNullOrEmpty(x)));
            sb.AppendLine(
                $"| {Cell(f.Project)} | {Cell($"{f.File}:{f.Line}")} | {f.Kind} | {Cell(f.Symbol)} " +
                $"| {Cell(loc)} | {Cell(f.Segmento)} | {Cell(f.ComoCorregir)} |");
        }
        sb.AppendLine();
    }

    /// <summary>Analisis en profundidad de los ensamblados de terceros (sin fuentes): dependencias
    /// nativas del SO, APIs Windows gestionadas, riesgo y reemplazo sugerido.</summary>
    private static void AppendThirdPartySection(StringBuilder sb, AnalysisReport report)
    {
        var profiles = ThirdPartyAnalysis.Analyze(report);
        if (profiles.Count == 0) return;

        sb.AppendLine("## Análisis de terceros (sin fuentes)");
        sb.AppendLine();
        sb.AppendLine("> Estos ensamblados son de terceros: no se dispone del código fuente ni control de su build. Verificar si el paquete tiene versión multiplataforma; si no, reemplazarlo o encapsular su uso tras una interfaz.");
        sb.AppendLine();

        foreach (var p in profiles)
        {
            sb.AppendLine($"### {p.Assembly} (Severidad: {p.MaxSeverity})");
            if (p.SuggestedReplacement is not null)
                sb.AppendLine($"Reemplazo sugerido: **{Cell(p.SuggestedReplacement)}**  ");
            sb.AppendLine($"APIs/referencias Windows gestionadas detectadas: {p.WindowsApiRules} regla(s)  ");
            sb.AppendLine();

            if (p.NativeDeps.Count > 0)
            {
                sb.AppendLine("| DLL nativa | Sitios P/Invoke | Tipo |");
                sb.AppendLine("|------------|-----------------|------|");
                foreach (var d in p.NativeDeps)
                    sb.AppendLine($"| {Cell(d.Dll)} | {d.Sites} | {ThirdPartyAnalysis.DependencyKind(d)} |");
            }
            else
            {
                sb.AppendLine("Sin dependencias nativas P/Invoke detectadas (revisar referencias gestionadas).");
            }
            sb.AppendLine();
        }
    }

    /// <summary>Nota de cabecera: explica la estrategia de estimación (en horas) y la columna N.</summary>
    private static void AppendEstimationNote(StringBuilder sb)
    {
        sb.AppendLine("> **Cómo se estima (horas-persona).** Cada dependencia se estima a tres puntos: optimista (O), más probable (M) y pesimista (P); la **media = (O + 4*M + P) / 6** (PERT). El esfuerzo se cuenta **una vez por regla y ensamblado** (no por cada ocurrencia); a los **terceros** se les aplica un factor de incertidumbre; y se añade un bucket transversal de **Pruebas y CI**. El rango O-P es amplio a propósito (refleja la incertidumbre). La columna **N (ocurr.)** de las tablas es el **número de ocurrencias** de esa misma dependencia (regla + evidencia).");
        sb.AppendLine();
    }

    /// <summary>Resumen ejecutivo del coste por bucket multiplataforma (incluye Pruebas y CI).</summary>
    private static void AppendBucketSummary(StringBuilder sb, IReadOnlyList<BucketEffort> buckets)
    {
        if (buckets.Count == 0) return;
        var grand = buckets.Aggregate(EffortEstimate.Zero, (a, b) => a.Add(b.Effort));

        sb.AppendLine("## Coste por bucket (multiplataforma)");
        sb.AppendLine();
        sb.AppendLine("| Bucket | Optimista | Media | Pesimista | % |");
        sb.AppendLine("|--------|-----------|-------|-----------|---|");
        foreach (var b in buckets)
        {
            var pct = grand.Media > 0 ? b.Effort.Media / grand.Media * 100 : 0;
            sb.AppendLine($"| {CostBuckets.Text(b.Bucket)} | {b.Effort.Optimista:0.#} | {b.Effort.Media:0.#} | {b.Effort.Pesimista:0.#} | {pct:0} % |");
        }
        sb.AppendLine($"| **Total (con Pruebas y CI)** | {grand.Optimista:0.#} | {grand.Media:0.#} | {grand.Pesimista:0.#} | 100 % |");
        sb.AppendLine();
        sb.AppendLine("> Modelo: esfuerzo contado una vez por regla y ensamblado (PERT O/M/P), con factor de incertidumbre a los ensamblados de terceros. Los buckets de desarrollo se derivan de la estrategia de separación de cada regla; Pruebas y CI es una fracción transversal del esfuerzo de desarrollo.");
        sb.AppendLine();
    }

    /// <summary>Tabla de hallazgos confirmados: donde se encontro (ubicacion), esfuerzo, estrategia de
    /// separacion, alternativa Linux y pasos de remediacion. La columna Bloqueante se omite por ser
    /// redundante con Severidad (un bloqueante tiene severidad Bloqueante).</summary>
    private static void AppendConfirmedTable(StringBuilder sb, IReadOnlyList<FindingGroup> groups)
    {
        sb.AppendLine("| Regla | Severidad | N (ocurr.) | Esfuerzo (h) | Ubicación (ejemplo) | Estrategia | Evidencia | Alternativa Linux (reemplazo propuesto) | Pasos de remediación |");
        sb.AppendLine("|-------|-----------|------------|--------------|---------------------|------------|-----------|------------------------------------------|----------------------|");
        foreach (var g in groups)
        {
            var f = g.Representative;
            sb.AppendLine(
                $"| {Cell(f.RuleId)} | {f.Severidad} | {g.Count} | {f.Esfuerzo.Media:0.#} | {Cell(ReportGrouping.SampleLocation(f, g.Count))} " +
                $"| {Cell(ReportGrouping.StrategyText(f.EstrategiaSeparacion))} | {Cell(f.Evidencia)} " +
                $"| {Cell(ReportGrouping.AlternativeWithNote(f))} | {Cell(ReportGrouping.StepsInline(f.PasosRemediacion))} |");
        }
        sb.AppendLine();
    }

    /// <summary>Tabla de senal debil (confianza Baja): no cuenta esfuerzo; muestra una ubicacion de ejemplo.</summary>
    private static void AppendManualTable(StringBuilder sb, IReadOnlyList<FindingGroup> groups)
    {
        sb.AppendLine("| Regla | Severidad | Confianza | N (ocurr.) | Evidencia | Ubicación (ejemplo) |");
        sb.AppendLine("|-------|-----------|-----------|------------|-----------|---------------------|");
        foreach (var g in groups)
        {
            var f = g.Representative;
            var loc = ReportGrouping.SampleLocation(f, g.Count);
            sb.AppendLine($"| {Cell(f.RuleId)} | {f.Severidad} | {f.Confianza} | {g.Count} | {Cell(f.Evidencia)} | {Cell(loc)} |");
        }
        sb.AppendLine();
    }

    /// <summary>Escapa el contenido de una celda para no romper la tabla Markdown (pipes y saltos de linea).</summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\\", "\\\\")
                    .Replace("|", "\\|")
                    .Replace("\r", " ")
                    .Replace("\n", " ");
    }
}
