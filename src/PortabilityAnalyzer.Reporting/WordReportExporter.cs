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

        b.Append(Para("Informe de análisis multiplataforma (.NET 8) - Windows / Linux", bold: true, sizeHalfPt: 36));
        b.Append(Para($"Generado: {report.GeneratedAt:yyyy-MM-dd HH:mm}"));
        b.Append(Para($"Analizados: {report.AnalyzedCount} | Omitidos: {report.SkippedCount} | Con bloqueantes: {report.BlockerCount}"));
        b.Append(Para($"Esfuerzo total de desarrollo: optimista {report.TotalEffort.Optimista:0.#} h | media {report.TotalEffort.Media:0.#} h | pesimista {report.TotalEffort.Pesimista:0.#} h"));
        b.Append(Para("Cómo se estima (en horas-persona): cada dependencia se estima a tres puntos: O = optimista, M = más probable, P = pesimista. La media = (O + 4·M + P) / 6 (método PERT) es el valor esperado, es decir, la estimación más probable a efectos de planificación. El esfuerzo se cuenta una vez por regla y ensamblado (no por ocurrencia); a los terceros se les aplica un factor de incertidumbre; se añade un bucket de Pruebas y CI. La columna N (ocurr.) es el número de ocurrencias de esa dependencia (regla + evidencia)."));
        b.Append(Para(string.Empty));

        AppendBuildOrderSection(b, report);
        AppendBucketSummary(b, report.CostByBucket);
        AppendArchitectureSection(b, report);
        AppendSplitSection(b, report);
        AppendThirdPartySection(b, report);
        AppendImpactSection(b, report);
        AppendSourceSection(b, report);
        AppendCodeExamplesSection(b, report);

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
            b.Append(Para($"Severidad máxima: {asm.MaxSeverity} | Esfuerzo medio: {asm.Effort.Media:0.#} h{terceros}"));

            if (confirmed.Count > 0)
            {
                b.Append(BuildTable(
                    new[] { "Regla", "Severidad", "N (ocurr.)", "Esfuerzo (h)", "Ubicación (ejemplo)", "Estrategia", "Evidencia", "Alternativa Linux (reemplazo propuesto)", "Pasos de remediación" },
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
                b.Append(Para("Sin hallazgos confirmados (solo señales débiles, ver abajo)."));
            }

            if (manual.Count > 0)
            {
                var ocurrencias = manual.Sum(g => g.Count);
                b.Append(Para($"Revisión manual - señal débil, excluida del esfuerzo ({manual.Count} grupos / {ocurrencias} ocurrencias)",
                    bold: true, sizeHalfPt: 24));
                b.Append(BuildTable(
                    new[] { "Regla", "Severidad", "Confianza", "N (ocurr.)", "Evidencia", "Ubicación (ejemplo)" },
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

    /// <summary>Recomendacion de arquitectura destino y plan de migración (sintetizado del analisis).</summary>
    private static void AppendArchitectureSection(Body b, AnalysisReport report)
    {
        var plan = ArchitectureRecommendation.Build(report);

        b.Append(Para("Arquitectura destino recomendada y plan de migración", bold: true, sizeHalfPt: 28));
        b.Append(Para($"Objetivo: core .NET 8 común + WPF en Windows y Avalonia en Linux. Esfuerzo total estimado (con Pruebas y CI): {plan.TotalWithTesting.Media:0.#} h (optimista {plan.TotalWithTesting.Optimista:0.#} / pesimista {plan.TotalWithTesting.Pesimista:0.#}). Bloqueantes: {plan.Blockers}."));

        if (plan.RoleNotes.Count > 0)
        {
            b.Append(Para("Roles y restricciones", bold: true, sizeHalfPt: 24));
            foreach (var n in plan.RoleNotes)
                b.Append(Para($"- {n}"));
        }

        b.Append(Para("Estructura de proyectos propuesta", bold: true, sizeHalfPt: 24));
        b.Append(BuildTable(
            new[] { "Proyecto", "TFM", "Propósito" },
            new[] { 2.5, 1.5, 4.0 },
            plan.Projects.Select(p => new[] { p.Name, p.Tfm, p.Purpose })));

        if (plan.Abstractions.Count > 0)
        {
            b.Append(Para("Capa de abstracción (interfaces por plataforma)", bold: true, sizeHalfPt: 24));
            foreach (var a in plan.Abstractions)
                b.Append(Para($"- {a}"));
        }

        b.Append(Para("Plan de migración", bold: true, sizeHalfPt: 24));
        for (int i = 0; i < plan.MigrationSteps.Count; i++)
            b.Append(Para($"{i + 1}. {plan.MigrationSteps[i]}"));
        b.Append(Para(string.Empty));
    }

    /// <summary>Scaffold de división de los proyectos con rol divisiblePorUI.</summary>
    private static void AppendSplitSection(Body b, AnalysisReport report)
    {
        if (report.SplitResults.Count == 0) return;

        b.Append(Para("División de proyectos (scaffold generado)", bold: true, sizeHalfPt: 28));
        b.Append(Para("Para los proyectos con rol divisiblePorUI se han generado dos proyectos en la carpeta de salida: una parte multiplataforma (net8.0) y otra Windows (net8.0-windows). Es un punto de partida: revisar las referencias cruzadas y las acciones pendientes."));
        foreach (var s in report.SplitResults)
        {
            b.Append(Para($"{s.OriginalProject} -> {s.MultiProject} (net8.0) + {s.WindowsProject} (net8.0-windows)", bold: true, sizeHalfPt: 24));
            b.Append(Para($"Ficheros portables: {s.PortableFiles} · Ficheros Windows: {s.WindowsFiles}. Generados en: {s.OutputDir} (ver SPLIT-NOTES-*.md)."));
            if (s.CrossReferences.Count > 0)
            {
                b.Append(Para("Referencias cruzadas a resolver (introducir abstracción):", bold: true));
                foreach (var r in s.CrossReferences.Take(20)) b.Append(Para($"- {r}"));
            }
            b.Append(Para("Acciones manuales pendientes:", bold: true));
            foreach (var m in s.ManualNotes) b.Append(Para($"- {m}"));
        }
        b.Append(Para(string.Empty));
    }

    /// <summary>Apéndice con ejemplos de equivalencia Linux / compilación condicional por categoría.</summary>
    private static void AppendCodeExamplesSection(Body b, AnalysisReport report)
    {
        var cats = report.Assemblies.SelectMany(a => a.ConfirmedFindings()).Select(f => f.Categoria)
            .Concat(report.SourceFindings.Select(f => f.Categoria))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var examples = CodeExamples.ForCategories(cats);
        if (examples.Count == 0) return;

        b.Append(Para("Equivalencias Linux y compilación condicional (ejemplos)", bold: true, sizeHalfPt: 28));
        b.Append(Para("Ejemplos de código para cada tipo de dependencia detectada: la equivalencia multiplataforma o cómo aislarla por SO."));
        foreach (var e in examples)
        {
            b.Append(Para(e.Titulo, bold: true, sizeHalfPt: 24));
            foreach (var line in e.Codigo.Replace("\r", string.Empty).Split('\n'))
                b.Append(CodeLine(line));
            b.Append(Para(e.Nota));
        }
        b.Append(Para(string.Empty));
    }

    /// <summary>Línea de código en monoespaciado (Consolas), preservando la indentación.</summary>
    private static Paragraph CodeLine(string text)
    {
        var runProps = new RunProperties(
            new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
            new FontSize { Val = "18" });
        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var pp = new ParagraphProperties(new SpacingBetweenLines { After = "0", Before = "0" });
        return new Paragraph(pp, run);
    }

    /// <summary>Métrica de impacto: clases y ficheros afectados por proyecto.</summary>
    private static void AppendImpactSection(Body b, AnalysisReport report)
    {
        if (report.SourceFindings.Count == 0) return;

        b.Append(Para("Impacto por proyecto (clases y ficheros afectados)", bold: true, sizeHalfPt: 28));
        b.Append(Para("Métrica de tamaño del cambio (además de las horas): cuántas clases y ficheros de cada proyecto usan APIs de Windows."));

        var rows = report.SourceFindings
            .GroupBy(f => f.Project)
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var ficheros = g.Select(f => f.File).Distinct().OrderBy(x => x).ToList();
                var clases = g.Where(f => !string.IsNullOrEmpty(f.Clase)).Select(f => f.Clase!).Distinct().Count();
                return new[] { g.Key, ficheros.Count.ToString(), clases.ToString(), g.Count().ToString(), string.Join(", ", ficheros) };
            });

        b.Append(BuildTable(
            new[] { "Proyecto", "Ficheros afectados", "Clases afectadas", "Usos Windows", "Ficheros" },
            new[] { 2.0, 1.4, 1.4, 1.2, 4.0 },
            rows));
        b.Append(Para(string.Empty));
    }

    /// <summary>Analisis a nivel de codigo fuente: fichero/linea/segmento + como corregir.</summary>
    private static void AppendSourceSection(Body b, AnalysisReport report)
    {
        var findings = report.SourceFindings;
        if (findings.Count == 0) return;
        var files = findings.Select(f => f.File).Distinct().Count();

        b.Append(Para("Análisis de código fuente (dónde y cómo corregir)", bold: true, sizeHalfPt: 28));
        b.Append(Para($"Usos de APIs propias de Windows en el código fuente (Roslyn): {findings.Count} usos en {files} ficheros. Se indica fichero, línea, el segmento de código y la corrección multiplataforma."));

        b.Append(BuildTable(
            new[] { "Proyecto", "Fichero:línea", "Tipo", "Símbolo", "Clase / Método", "Segmento de código", "Cómo corregir" },
            new[] { 1.5, 2.2, 0.9, 1.8, 1.8, 3.2, 3.4 },
            findings.Select(f => new[]
            {
                f.Project, $"{f.File}:{f.Line}", f.Kind, f.Symbol,
                string.Join(" / ", new[] { f.Clase, f.Metodo }.Where(x => !string.IsNullOrEmpty(x))),
                f.Segmento, f.ComoCorregir
            })));
        b.Append(Para(string.Empty));
    }

    /// <summary>Analisis en profundidad de los ensamblados de terceros (sin fuentes).</summary>
    /// <summary>Orden correcto de compilacion de los proyectos (topologia de ProjectReference).</summary>
    private static void AppendBuildOrderSection(Body b, AnalysisReport report)
    {
        var bo = report.BuildOrder;
        if (bo.Steps.Count == 0 && !bo.HasCycle) return;

        b.Append(Para("Orden de compilación de los proyectos", bold: true, sizeHalfPt: 28));
        b.Append(Para("Orden derivado de las referencias de proyecto (ProjectReference): cada proyecto se compila después de aquellos a los que referencia. Los proyectos del mismo nivel no dependen entre sí y podrían compilarse en paralelo."));

        var i = 1;
        var rows = bo.Steps.Select(s => new[]
        {
            (i++).ToString(),
            s.Level.ToString(),
            s.Project,
            s.DependsOn.Count == 0 ? "— (sin dependencias internas)" : string.Join(", ", s.DependsOn)
        });
        b.Append(BuildTable(
            new[] { "#", "Nivel", "Proyecto", "Depende de" },
            new[] { 0.6, 0.9, 3.0, 4.0 },
            rows));

        if (bo.HasCycle)
            b.Append(Para($"AVISO: ciclo de referencias detectado entre {string.Join(", ", bo.CycleProjects)}. No existe un orden lineal para esos proyectos; hay que romper el ciclo (extraer un proyecto común o invertir una dependencia).", bold: true));
        b.Append(Para(string.Empty));
    }

    private static void AppendThirdPartySection(Body b, AnalysisReport report)
    {
        var profiles = ThirdPartyAnalysis.Analyze(report);
        if (profiles.Count == 0) return;

        b.Append(Para("Análisis de terceros (sin fuentes)", bold: true, sizeHalfPt: 28));
        b.Append(Para("Estos ensamblados son de terceros: no se dispone del código fuente ni control de su build. Verificar si el paquete tiene versión multiplataforma; si no, reemplazarlo o encapsular su uso tras una interfaz."));

        foreach (var p in profiles)
        {
            b.Append(Para($"{p.Assembly} (Severidad: {p.MaxSeverity})", bold: true, sizeHalfPt: 24));
            if (p.SuggestedReplacement is not null)
                b.Append(Para($"Reemplazo sugerido: {p.SuggestedReplacement}"));
            b.Append(Para($"APIs/referencias Windows gestionadas detectadas: {p.WindowsApiRules} regla(s)"));

            if (p.NativeDeps.Count > 0)
            {
                b.Append(Para("Dependencias nativas del SO (P/Invoke):", bold: true));
                b.Append(BuildTable(
                    new[] { "DLL nativa", "Sitios P/Invoke", "Tipo" },
                    new[] { 3.0, 1.5, 3.5 },
                    p.NativeDeps.Select(d => new[] { d.Dll, d.Sites.ToString(), ThirdPartyAnalysis.DependencyKind(d) })));
            }
            else
            {
                b.Append(Para("Sin dependencias nativas P/Invoke detectadas (revisar referencias gestionadas)."));
            }

            if (p.WindowsApis.Count > 0)
            {
                b.Append(Para("APIs Windows gestionadas (no P/Invoke):", bold: true));
                b.Append(BuildTable(
                    new[] { "Categoría", "API / tipo Windows", "Sitios", "Alternativa Linux / multiplataforma" },
                    new[] { 2.0, 3.0, 1.0, 4.0 },
                    p.WindowsApis.Select(a => new[] { a.Categoria, a.Api, a.Sites.ToString(), a.AlternativaLinux })));
            }
        }
        b.Append(Para(string.Empty));
    }

    /// <summary>Resumen ejecutivo del coste por bucket multiplataforma (incluye Pruebas y CI).</summary>
    private static void AppendBucketSummary(Body b, IReadOnlyList<BucketEffort> buckets)
    {
        if (buckets.Count == 0) return;
        var grand = buckets.Aggregate(EffortEstimate.Zero, (a, x) => a.Add(x.Effort));

        b.Append(Para("Coste por bucket (multiplataforma)", bold: true, sizeHalfPt: 28));

        var rows = new List<string[]>();
        foreach (var x in buckets)
        {
            var pct = grand.Media > 0 ? x.Effort.Media / grand.Media * 100 : 0;
            rows.Add(new[]
            {
                CostBuckets.Text(x.Bucket), x.Effort.Optimista.ToString("0.#"),
                x.Effort.Media.ToString("0.#"), x.Effort.Pesimista.ToString("0.#"), $"{pct:0} %"
            });
        }
        rows.Add(new[]
        {
            "Total (con Pruebas y CI)", grand.Optimista.ToString("0.#"),
            grand.Media.ToString("0.#"), grand.Pesimista.ToString("0.#"), "100 %"
        });

        b.Append(BuildTable(
            new[] { "Bucket", "Optimista", "Media", "Pesimista", "%" },
            new[] { 4.0, 1.2, 1.2, 1.2, 1.0 },
            rows));
        b.Append(Para("Modelo: esfuerzo una vez por regla y ensamblado (PERT); factor de terceros aplicado a sus ensamblados; Pruebas y CI como fracción del esfuerzo de desarrollo."));
        b.Append(Para(string.Empty));
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
