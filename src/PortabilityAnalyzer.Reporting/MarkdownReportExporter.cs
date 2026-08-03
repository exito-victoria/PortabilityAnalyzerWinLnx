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

            var confirmed = asm.ConfirmedFindings().OrderByDescending(x => x.Severidad).ToList();
            var manual = asm.ManualReviewFindings().OrderByDescending(x => x.Severidad).ToList();

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
                AppendTable(sb, confirmed);
            else
            {
                sb.AppendLine("Sin hallazgos confirmados (solo senales debiles, ver abajo).");
                sb.AppendLine();
            }

            if (manual.Count > 0)
            {
                var grupos = manual.GroupBy(f => (f.RuleId, f.Evidencia)).Count();
                sb.AppendLine($"### Revision manual — senal debil, excluida del esfuerzo ({grupos} grupos / {manual.Count} ocurrencias)");
                sb.AppendLine();
                AppendTable(sb, manual);
            }
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>
    /// Escribe una tabla de hallazgos agrupando las ocurrencias identicas (misma regla y misma
    /// evidencia) en una sola fila con su recuento; muestra una ubicacion de ejemplo. Reduce el ruido
    /// cuando una dependencia se repite en decenas de metodos (p. ej. P/Invoke de un driver nativo).
    /// El informe JSON conserva cada ocurrencia por separado.
    /// </summary>
    private static void AppendTable(StringBuilder sb, IReadOnlyList<Finding> findings)
    {
        sb.AppendLine("| Regla | Severidad | Confianza | Bloqueante | N | Ubicacion (ejemplo) | Evidencia |");
        sb.AppendLine("|-------|-----------|-----------|------------|---|---------------------|-----------|");

        var groups = findings
            .GroupBy(f => (f.RuleId, f.Evidencia))
            .OrderByDescending(g => g.First().Severidad)
            .ThenByDescending(g => g.Count());

        foreach (var g in groups)
        {
            var f = g.First();
            var count = g.Count();
            var loc = string.Join(" / ", new[] { f.Type, f.Method }.Where(x => !string.IsNullOrEmpty(x)));
            if (count > 1 && loc.Length > 0) loc += " …";
            sb.AppendLine($"| {Cell(f.RuleId)} | {f.Severidad} | {f.Confianza} | {(f.EsBloqueante ? "Si" : "No")} | {count} | {Cell(loc)} | {Cell(f.Evidencia)} |");
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
