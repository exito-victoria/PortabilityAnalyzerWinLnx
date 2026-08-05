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
        sb.AppendLine("# Informe de portabilidad Windows -> Linux");
        sb.AppendLine();
        sb.AppendLine($"Generado: {report.GeneratedAt:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine($"Analizados: {report.AnalyzedCount} | Omitidos: {report.SkippedCount} | Con bloqueantes: {report.BlockerCount}  ");
        sb.AppendLine($"Esfuerzo total de desarrollo (horas) -> optimista: {report.TotalEffort.Optimista:0.#} | media: {report.TotalEffort.Media:0.#} | pesimista: {report.TotalEffort.Pesimista:0.#}");
        sb.AppendLine();

        AppendBucketSummary(sb, report.CostByBucket);

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
            sb.AppendLine($"Severidad maxima: {asm.MaxSeverity} | Esfuerzo medio: {asm.Effort.Media:0.#} h{terceros}");
            sb.AppendLine();

            if (confirmed.Count > 0)
                AppendConfirmedTable(sb, confirmed);
            else
            {
                sb.AppendLine("Sin hallazgos confirmados (solo senales debiles, ver abajo).");
                sb.AppendLine();
            }

            if (manual.Count > 0)
            {
                var ocurrencias = manual.Sum(g => g.Count);
                sb.AppendLine($"### Revision manual — senal debil, excluida del esfuerzo ({manual.Count} grupos / {ocurrencias} ocurrencias)");
                sb.AppendLine();
                AppendManualTable(sb, manual);
            }
        }

        File.WriteAllText(outputPath, sb.ToString());
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
        sb.AppendLine("> Modelo: esfuerzo contado una vez por regla y ensamblado (PERT O/M/P), con factor de incertidumbre a los ensamblados de terceros. Los buckets de desarrollo se derivan de la estrategia de separacion de cada regla; Pruebas y CI es una fraccion transversal del esfuerzo de desarrollo.");
        sb.AppendLine();
    }

    /// <summary>Tabla de hallazgos confirmados: donde se encontro (ubicacion), esfuerzo, estrategia de
    /// separacion, alternativa Linux y pasos de remediacion. La columna Bloqueante se omite por ser
    /// redundante con Severidad (un bloqueante tiene severidad Bloqueante).</summary>
    private static void AppendConfirmedTable(StringBuilder sb, IReadOnlyList<FindingGroup> groups)
    {
        sb.AppendLine("| Regla | Severidad | N | Esfuerzo (h) | Ubicacion (ejemplo) | Estrategia | Evidencia | Alternativa Linux (reemplazo propuesto) | Pasos de remediacion |");
        sb.AppendLine("|-------|-----------|---|--------------|---------------------|------------|-----------|------------------------------------------|----------------------|");
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
        sb.AppendLine("| Regla | Severidad | Confianza | N | Evidencia | Ubicacion (ejemplo) |");
        sb.AppendLine("|-------|-----------|-----------|---|-----------|---------------------|");
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
