using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>
/// Informe EJECUTIVO (Word, vertical): resumen breve y en lenguaje de negocio de los hallazgos. Es el
/// resumen que ve el cliente. Se localiza mediante <see cref="ExecTexts"/> (ES/EN) para poder generar el
/// mismo informe en varios idiomas. Se genera solo con el flag --executive.
/// </summary>
public sealed class ExecutiveWordExporter : IReportExporter
{
    // A4 vertical (twips): 11906 x 16838. Margenes de 1134 (~2 cm) -> ancho util ~9638.
    private const int PageWidth = 11906;
    private const int PageHeight = 16838;
    private const int Margin = 1134;
    private const int UsableWidth = PageWidth - 2 * Margin;

    private readonly string _projectName;
    private readonly ExecTexts _t;

    public ExecutiveWordExporter(string projectName, ExecTexts texts)
    {
        _projectName = projectName;
        _t = texts;
    }

    public string Format => "executive";

    /// <summary>Formatea un numero con la cultura del idioma (coma en ES, punto en EN).</summary>
    private string N(double v) => v.ToString("0.#", _t.Culture);

    public void Export(AnalysisReport report, string outputPath)
    {
        using var word = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = word.AddMainDocumentPart();
        mainPart.Document = new Document();
        AddStyleDefinitions(mainPart);
        var b = mainPart.Document.AppendChild(new Body());

        var managed = report.Assemblies.Where(a => a.Classification.Kind == AssemblyKind.Managed).ToList();
        var filesAffected = report.SourceFindings.Select(f => f.File).Distinct().Count();
        var clasesAffected = report.SourceFindings.Where(f => !string.IsNullOrEmpty(f.Clase)).Select(f => f.Clase!).Distinct().Count();

        // Bloqueantes de NUESTRO trabajo (excluye terceros no modificables, cuya adaptacion es del proveedor).
        var ourBlockers = managed
            .Where(a => report.Roles.RoleOf(a.Classification.Name) != ProjectRole.NoModificable)
            .Sum(a => a.ConfirmedFindings().Count(f => f.EsBloqueante));

        // Portada.
        b.Append(Para(_t.ReportTitle, bold: true, sizeHalfPt: 44, style: "Title"));
        b.Append(Para(_t.Subtitle(_projectName), bold: true, sizeHalfPt: 26));
        b.Append(Para($"{_t.GeneratedLabel}: {report.GeneratedAt:yyyy-MM-dd HH:mm}"));
        b.Append(Para(string.Empty));

        AppendResumen(b, report, managed, ourBlockers);
        AppendCifrasClave(b, report, managed.Count, ourBlockers, filesAffected, clasesAffected);
        AppendEstimacionPorProyecto(b, report, managed, ourBlockers);
        AppendCostePorBloque(b, report);
        AppendHallazgosPrincipales(b, report);
        AppendRestricciones(b, report, managed);
        AppendRecomendacion(b);

        b.Append(new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)PageWidth, Height = (UInt32Value)(uint)PageHeight, Orient = PageOrientationValues.Portrait },
            new PageMargin { Top = Margin, Bottom = Margin, Left = (uint)Margin, Right = (uint)Margin, Header = 720, Footer = 720, Gutter = 0 }));
    }

    // --- Secciones ---

    private void AppendResumen(Body b, AnalysisReport report, List<AssemblyAnalysisResult> managed, int ourBlockers)
    {
        b.Append(Heading(_t.HSummary, 1));
        var e = report.TotalEffort;
        b.Append(Para(_t.SummaryPara1(_t.Scope(managed.Count))));
        b.Append(Para(_t.SummaryPara2(N(e.Media), N(e.Optimista), N(e.Pesimista), ourBlockers)));
    }

    private void AppendCifrasClave(Body b, AnalysisReport report, int proyectos, int ourBlockers, int ficheros, int clases)
    {
        b.Append(Heading(_t.HKeyFigures, 1));
        var e = report.TotalEffort;
        var rows = new List<string[]>
        {
            new[] { _t.KFProjects, proyectos.ToString() },
            new[] { _t.KFBlockers, ourBlockers.ToString() },
            new[] { _t.KFFiles, ficheros.ToString() },
            new[] { _t.KFClasses, clases.ToString() },
            new[] { _t.KFOptimistic, N(e.Optimista) },
            new[] { _t.KFMostLikely, N(e.Media) },
            new[] { _t.KFPessimistic, N(e.Pesimista) },
        };
        b.Append(BuildTable(new[] { _t.ColMetric, _t.ColValue }, new[] { 4.0, 1.5 }, rows));
    }

    private void AppendEstimacionPorProyecto(Body b, AnalysisReport report, List<AssemblyAnalysisResult> managed, int ourBlockers)
    {
        if (managed.Count == 0) return;
        b.Append(Heading(_t.HEstimate, 1));
        b.Append(Para(_t.EstimateIntro));

        var projectSet = report.ProjectNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<string[]>();
        foreach (var a in managed.OrderByDescending(a => a.Effort.Media))
        {
            var role = report.Roles.RoleOf(a.Classification.Name);
            var esProyecto = projectSet.Contains(a.Classification.Name)
                             || role is ProjectRole.ObligatorioMultiplataforma or ProjectRole.DivisiblePorUI;
            var clase = esProyecto ? _t.ClsProject : _t.ClsThirdParty;
            var origen = esProyecto
                ? role switch
                {
                    ProjectRole.ObligatorioMultiplataforma => _t.OrigOwnObligatorio,
                    ProjectRole.DivisiblePorUI => _t.OrigOwnDivisible,
                    _ => _t.OrigOwn
                }
                : role == ProjectRole.NoModificable ? _t.OrigNoMod(AuthorOf(a.Classification.Path)) : AuthorOf(a.Classification.Path);
            var blockers = a.ConfirmedFindings().Count(f => f.EsBloqueante);
            var esfuerzo = role == ProjectRole.NoModificable
                ? _t.NotCharged
                : $"{N(a.Effort.Optimista)} / {N(a.Effort.Media)} / {N(a.Effort.Pesimista)}";
            rows.Add(new[] { a.Classification.Name, clase, origen, _t.Sev(a.MaxSeverity), blockers.ToString(), esfuerzo });
        }
        var e = report.TotalEffort;
        rows.Add(new[] { _t.TotalRow, "", "", "", ourBlockers.ToString(), $"{N(e.Optimista)} / {N(e.Media)} / {N(e.Pesimista)}" });

        b.Append(BuildTable(
            new[] { _t.ColProjectDll, _t.ColClass, _t.ColAuthorRole, _t.ColSev, _t.ColBlk, _t.ColEffort },
            new[] { 2.5, 1.3, 2.5, 1.0, 0.8, 2.2 },
            rows));
    }

    /// <summary>Autor/empresa de una DLL a partir de sus metadatos (AssemblyCompany / version del fichero).</summary>
    private string AuthorOf(string assemblyPath)
    {
        try
        {
            if (File.Exists(assemblyPath))
            {
                var company = System.Diagnostics.FileVersionInfo.GetVersionInfo(assemblyPath).CompanyName?.Trim();
                if (!string.IsNullOrWhiteSpace(company)) return company!;
            }
        }
        catch { /* sin metadatos legibles */ }
        return _t.UnknownThirdParty;
    }

    private void AppendCostePorBloque(Body b, AnalysisReport report)
    {
        if (report.CostByBucket.Count == 0) return;
        b.Append(Heading(_t.HCost, 1));
        b.Append(Para(_t.CostIntro));
        var grand = report.CostByBucket.Aggregate(EffortEstimate.Zero, (a, x) => a.Add(x.Effort));
        var rows = report.CostByBucket.Select(x =>
        {
            var pct = grand.Media > 0 ? x.Effort.Media / grand.Media * 100 : 0;
            return new[] { _t.Bucket(x.Bucket), N(x.Effort.Optimista), N(x.Effort.Media), $"{pct:0} %" };
        }).ToList();
        rows.Add(new[] { _t.CostTotal, N(grand.Optimista), N(grand.Media), "100 %" });
        b.Append(BuildTable(new[] { _t.ColBlock, _t.ColOptimistic, _t.ColMostLikely, _t.ColPct }, new[] { 3.6, 1.3, 1.3, 0.9 }, rows));
    }

    private void AppendHallazgosPrincipales(Body b, AnalysisReport report)
    {
        var porCategoria = report.SourceFindings
            .GroupBy(f => f.Categoria, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Cat: g.Key, Count: g.Count()));
        if (!porCategoria.Any())
            porCategoria = report.Assemblies
                .SelectMany(a => a.ConfirmedFindings())
                .GroupBy(f => f.Categoria, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Cat: g.Key, Count: g.Count()));

        var top = porCategoria.OrderByDescending(x => x.Count).Take(8).ToList();
        if (top.Count == 0) return;

        b.Append(Heading(_t.HFindings, 1));
        b.Append(Para(_t.FindingsIntro));
        b.Append(BuildTable(
            new[] { _t.ColDependencyType, _t.ColUses },
            new[] { 4.0, 1.5 },
            top.Select(x => new[] { _t.Category(x.Cat), x.Count.ToString() })));
    }

    private void AppendRestricciones(Body b, AnalysisReport report, List<AssemblyAnalysisResult> managed)
    {
        var noMod = managed
            .Where(a => report.Roles.RoleOf(a.Classification.Name) == ProjectRole.NoModificable)
            .Select(a => a.Classification.Name)
            .ToList();
        if (noMod.Count == 0) return;

        b.Append(Heading(_t.HConstraints, 1));
        b.Append(Para(_t.ConstraintsIntro));
        foreach (var name in noMod)
            b.Append(Bullet($"{name} — {_t.AuthorWord}: {AuthorOfByName(managed, name)}."));
        b.Append(Para(_t.ConstraintsOptions));
    }

    private string AuthorOfByName(List<AssemblyAnalysisResult> managed, string name)
    {
        var a = managed.FirstOrDefault(x => string.Equals(x.Classification.Name, name, StringComparison.OrdinalIgnoreCase));
        return a is null ? _t.UnknownShort : AuthorOf(a.Classification.Path);
    }

    private void AppendRecomendacion(Body b)
    {
        b.Append(Heading(_t.HRecommendation, 1));
        b.Append(Para(_t.RecoPara1));
        b.Append(Para(_t.RecoPara2));
    }

    // --- Infraestructura Word (autocontenida, agnostica del idioma) ---

    private static void AddStyleDefinitions(MainDocumentPart mainPart)
    {
        var part = mainPart.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            HeadingStyle("Title", "Title", null, 44, "1F3864"),
            HeadingStyle("Heading1", "heading 1", 0, 28, "2F5496"),
            HeadingStyle("Heading2", "heading 2", 1, 24, "2F5496"));
    }

    private static Style HeadingStyle(string styleId, string name, int? outlineLevel, int sizeHalfPt, string colorHex)
    {
        var pPr = new StyleParagraphProperties(
            new KeepNext(), new KeepLines(),
            new SpacingBetweenLines { Before = "240", After = "60" });
        if (outlineLevel is int lvl) pPr.Append(new OutlineLevel { Val = lvl });

        return new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new UIPriority { Val = 9 },
            new PrimaryStyle(),
            pPr,
            new StyleRunProperties(new Bold(), new Color { Val = colorHex }, new FontSize { Val = sizeHalfPt.ToString() }))
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };
    }

    private static Paragraph Heading(string text, int level) =>
        Para(text, bold: true, sizeHalfPt: level == 1 ? 28 : 24, style: level == 1 ? "Heading1" : "Heading2");

    /// <summary>Parrafo con vineta (lista simple, con sangria).</summary>
    private static Paragraph Bullet(string text)
    {
        var run = new Run(new Text("•  " + text) { Space = SpaceProcessingModeValues.Preserve });
        var paraProps = new ParagraphProperties(new Indentation { Left = "360" });
        return new Paragraph(paraProps, run);
    }

    private static Paragraph Para(string text, bool bold = false, int? sizeHalfPt = null, string? style = null)
    {
        var runProps = new RunProperties();
        if (bold) runProps.Append(new Bold());
        if (sizeHalfPt.HasValue) runProps.Append(new FontSize { Val = sizeHalfPt.Value.ToString() });

        var run = new Run();
        if (runProps.HasChildren) run.Append(runProps);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

        var para = new Paragraph();
        if (style is not null)
            para.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        para.Append(run);
        return para;
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
        foreach (var w in widths) grid.Append(new GridColumn { Width = w.ToString() });
        table.AppendChild(grid);

        table.AppendChild(BuildRow(headers, widths, bold: true, header: true));
        foreach (var r in rows) table.AppendChild(BuildRow(r, widths, bold: false));
        return table;
    }

    private static TableRow BuildRow(string[] cells, int[] widths, bool bold, bool header = false)
    {
        var row = new TableRow();
        if (header) row.Append(new TableRowProperties(new TableHeader())); // repetir cabecera al partir en paginas.
        for (int i = 0; i < cells.Length; i++)
        {
            var props = new TableCellProperties(new TableCellWidth { Width = widths[i].ToString(), Type = TableWidthUnitValues.Dxa });
            var runProps = new RunProperties(new FontSize { Val = "20" });
            if (bold) runProps.Append(new Bold());
            var run = new Run(runProps, new Text(cells[i] ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
            var paraProps = new ParagraphProperties(new SpacingBetweenLines { After = "0", Before = "0" });
            row.Append(new TableCell(props, new Paragraph(paraProps, run)));
        }
        return row;
    }

    private static int[] ColumnWidths(double[] weights)
    {
        var total = weights.Sum();
        return weights.Select(w => (int)Math.Round(w / total * UsableWidth)).ToArray();
    }
}
