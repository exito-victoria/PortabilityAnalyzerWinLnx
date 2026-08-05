using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>
/// Exporta el informe como documento Word nativo (.docx) usando la SDK OpenXML: titulo, resumen y una
/// tabla por ensamblado con el esfuerzo de adaptacion y la alternativa Linux propuesta.
///
/// Ajuste de pagina: se usa orientacion <b>apaisada</b> (A4 landscape) y las tablas tienen
/// <b>layout fijo</b> con anchos de columna explicitos que suman el ancho util de la pagina, de modo
/// que NO se desbordan del margen (las celdas ajustan el texto por linea). La fuente de las celdas es
/// reducida para dar cabida a las columnas de evidencia y alternativa Linux.
/// </summary>
public sealed class WordReportExporter : IReportExporter
{
    public string Format => "word";

    // A4 apaisado (twips): 16838 x 11906. Margenes de 720 (0,5") a cada lado -> ancho util ~15398.
    private const int PageWidth = 16838;
    private const int PageHeight = 11906;
    private const int Margin = 720;
    private const int UsableWidth = PageWidth - 2 * Margin;

    private const int CellFontHalfPt = 18; // 9pt
    private const int HeaderFontHalfPt = 18;

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

        // Pesos relativos de columna (se convierten a anchos que suman el ancho util de la pagina).
        //                              Regla Sev  N   Esf  Ubic Estr Evid Alt  Pasos
        double[] confirmedWeights = { 1.7, 1.0, 0.5, 0.8, 2.2, 1.5, 2.1, 2.8, 3.4 };
        double[] manualWeights = { 2.0, 1.2, 1.2, 0.5, 3.0, 3.0 };

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
                    new[] { "Regla", "Severidad", "N", "Esfuerzo (h)", "Ubicacion (ejemplo)", "Estrategia", "Evidencia", "Alternativa Linux (reemplazo propuesto)", "Pasos de remediacion" },
                    confirmedWeights,
                    confirmed.Select(g =>
                    {
                        var f = g.Representative;
                        return new[]
                        {
                            f.RuleId, f.Severidad.ToString(), g.Count.ToString(),
                            f.Esfuerzo.Media.ToString("0.#"),
                            ReportGrouping.SampleLocation(f, g.Count),
                            ReportGrouping.StrategyText(f.EstrategiaSeparacion),
                            f.Evidencia ?? string.Empty,
                            ReportGrouping.AlternativeWithNote(f),
                            ReportGrouping.StepsInline(f.PasosRemediacion)
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
                    manualWeights,
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

        // La orientacion/margenes de la seccion deben ir al final del cuerpo.
        b.Append(new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)PageWidth, Height = (UInt32Value)(uint)PageHeight, Orient = PageOrientationValues.Landscape },
            new PageMargin { Top = Margin, Bottom = Margin, Left = (uint)Margin, Right = (uint)Margin, Header = 360, Footer = 360, Gutter = 0 }));

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

    /// <summary>Parrafo de celda: fuente reducida, sin espaciado extra, para que la tabla quepa en la pagina.</summary>
    private static Paragraph CellPara(string text, bool bold)
    {
        var runProps = new RunProperties(new FontSize { Val = (bold ? HeaderFontHalfPt : CellFontHalfPt).ToString() });
        if (bold) runProps.Append(new Bold());

        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var paraProps = new ParagraphProperties(new SpacingBetweenLines { After = "0", Before = "0" });
        return new Paragraph(paraProps, run);
    }

    private static Table BuildTable(string[] headers, double[] weights, IEnumerable<string[]> rows)
    {
        var widths = ColumnWidths(weights);

        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableWidth { Width = UsableWidth.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        var grid = new TableGrid();
        foreach (var w in widths)
            grid.Append(new GridColumn { Width = w.ToString() });
        table.AppendChild(grid);

        table.AppendChild(BuildRow(headers, widths, bold: true));
        foreach (var r in rows)
            table.AppendChild(BuildRow(r, widths, bold: false));
        return table;
    }

    private static TableRow BuildRow(string[] cells, int[] widths, bool bold)
    {
        var row = new TableRow();
        for (int i = 0; i < cells.Length; i++)
        {
            var props = new TableCellProperties(
                new TableCellWidth { Width = widths[i].ToString(), Type = TableWidthUnitValues.Dxa });
            row.Append(new TableCell(props, CellPara(cells[i] ?? string.Empty, bold)));
        }
        return row;
    }

    /// <summary>Convierte pesos relativos en anchos (twips) que suman el ancho util de la pagina.</summary>
    private static int[] ColumnWidths(double[] weights)
    {
        var total = weights.Sum();
        return weights.Select(w => (int)Math.Round(w / total * UsableWidth)).ToArray();
    }
}
