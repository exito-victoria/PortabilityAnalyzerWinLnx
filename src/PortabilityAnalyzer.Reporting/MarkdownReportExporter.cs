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
        sb.AppendLine($"Esfuerzo total (horas) -> optimista: {report.TotalEffort.Optimista:0.#} | media: {report.TotalEffort.Media:0.#} | pesimista: {report.TotalEffort.Pesimista:0.#}");
        sb.AppendLine();

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

    /// <summary>Tabla de hallazgos confirmados con esfuerzo de adaptacion y alternativa Linux propuesta.</summary>
    private static void AppendConfirmedTable(StringBuilder sb, IReadOnlyList<FindingGroup> groups)
    {
        sb.AppendLine("| Regla | Severidad | Bloqueante | N | Esfuerzo (h) | Evidencia | Alternativa Linux (reemplazo propuesto) |");
        sb.AppendLine("|-------|-----------|------------|---|--------------|-----------|------------------------------------------|");
        foreach (var g in groups)
        {
            var f = g.Representative;
            sb.AppendLine($"| {Cell(f.RuleId)} | {f.Severidad} | {(f.EsBloqueante ? "Si" : "No")} | {g.Count} | {f.Esfuerzo.Media:0.#} | {Cell(f.Evidencia)} | {Cell(f.AlternativaLinux)} |");
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
