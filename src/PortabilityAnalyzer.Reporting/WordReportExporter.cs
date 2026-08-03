using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>
/// Exporta el informe como documento Word nativo (.docx) usando la SDK OpenXML: titulo, resumen y una
/// tabla por ensamblado con el esfuerzo de adaptacion y la alternativa Linux propuesta para cada
/// dependencia. Mismo contenido que el Markdown, pero en formato ofimatico.
/// </summary>
public sealed class WordReportExporter : IReportExporter
{
    public string Format => "word";

    public void Export(AnalysisReport report, string outputPath)
    {
        using var word = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = word.AddMainDocumentPart();
        mainPart.Document = new Document();
        var b = mainPart.Document.AppendChild(new Body());

        b.Append(Para("Informe de portabilidad Windows -> Linux", bold: true, sizeHalfPt: 36));
        b.Append(Para($"Generado: {report.GeneratedAt:yyyy-MM-dd HH:mm}"));
        b.Append(Para($"Analizados: {report.AnalyzedCount} | Omitidos: {report.SkippedCount} | Con bloqueantes: {report.BlockerCount}"));
        b.Append(Para($"Esfuerzo total (horas) -> optimista: {report.TotalEffort.Optimista:0.#} | media: {report.TotalEffort.Media:0.#} | pesimista: {report.TotalEffort.Pesimista:0.#}"));
        b.Append(Para(string.Empty));

        foreach (var asm in report.Assemblies.OrderByDescending(a => a.MaxSeverity))
        {
            b.Append(Para($"{asm.Classification.Name} ({asm.Classification.Kind})", bold: true, sizeHalfPt: 28));

            var confirmed = ReportGrouping.Group(asm.ConfirmedFindings());
            var manual = ReportGrouping.Group(asm.ManualReviewFindings());

            if (confirmed.Count == 0 && manual.Count == 0)
            {
                b.Append(Para(asm.Classification.Reason ?? "Sin hallazgos."));
                b.Append(Para(string.Empty));
                continue;
            }

            var terceros = asm.IsThirdParty ? " | Terceros (factor de incertidumbre aplicado)" : string.Empty;
            b.Append(Para($"Severidad maxima: {asm.MaxSeverity} | Esfuerzo medio: {asm.Effort.Media:0.#} h{terceros}"));

            if (confirmed.Count > 0)
            {
                b.Append(BuildTable(
                    new[] { "Regla", "Severidad", "Bloqueante", "N", "Esfuerzo (h)", "Evidencia", "Alternativa Linux (reemplazo propuesto)" },
                    confirmed.Select(g =>
                    {
                        var f = g.Representative;
                        return new[]
                        {
                            f.RuleId, f.Severidad.ToString(), f.EsBloqueante ? "Si" : "No",
                            g.Count.ToString(), f.Esfuerzo.Media.ToString("0.#"),
                            f.Evidencia ?? string.Empty, f.AlternativaLinux
                        };
                    })));
            }
            else
            {
                b.Append(Para("Sin hallazgos confirmados (solo senales debiles, ver abajo)."));
            }

            if (manual.Count > 0)
            {
                var ocurrencias = manual.Sum(g => g.Count);
                b.Append(Para($"Revision manual — senal debil, excluida del esfuerzo ({manual.Count} grupos / {ocurrencias} ocurrencias)",
                    bold: true, sizeHalfPt: 24));
                b.Append(BuildTable(
                    new[] { "Regla", "Severidad", "Confianza", "N", "Evidencia", "Ubicacion (ejemplo)" },
                    manual.Select(g =>
                    {
                        var f = g.Representative;
                        return new[]
                        {
                            f.RuleId, f.Severidad.ToString(), f.Confianza.ToString(),
                            g.Count.ToString(), f.Evidencia ?? string.Empty,
                            ReportGrouping.SampleLocation(f, g.Count)
                        };
                    })));
            }

            b.Append(Para(string.Empty));
        }

        mainPart.Document.Save();
    }

    private static Paragraph Para(string text, bool bold = false, int? sizeHalfPt = null)
    {
        var runProps = new RunProperties();
        if (bold) runProps.Append(new Bold());
        if (sizeHalfPt.HasValue) runProps.Append(new FontSize { Val = sizeHalfPt.Value.ToString() });

        var run = new Run();
        if (runProps.HasChildren) run.Append(runProps);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(run);
    }

    private static Table BuildTable(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        table.AppendChild(BuildRow(headers, bold: true));
        foreach (var r in rows)
            table.AppendChild(BuildRow(r, bold: false));
        return table;
    }

    private static TableRow BuildRow(string[] cells, bool bold)
    {
        var row = new TableRow();
        foreach (var c in cells)
            row.Append(new TableCell(Para(c ?? string.Empty, bold)));
        return row;
    }
}
