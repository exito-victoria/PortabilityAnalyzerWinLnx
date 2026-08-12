using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>
/// Informe EJECUTIVO (Word, vertical): resumen breve y en lenguaje de negocio de los hallazgos, con la
/// estimacion por proyecto (o del unico proyecto si la entrada es un .csproj), las cifras clave, el coste
/// por bloque, los hallazgos principales, las restricciones de terceros y la recomendacion. Es el resumen
/// que ve el cliente. Se genera solo si se pide con el flag --executive y se guarda como
/// <c>InformeEjec_&lt;nombreproyecto&gt;.docx</c> en la misma carpeta del informe general.
/// </summary>
public sealed class ExecutiveWordExporter : IReportExporter
{
    // A4 vertical (twips): 11906 x 16838. Margenes de 1134 (~2 cm) -> ancho util ~9638.
    private const int PageWidth = 11906;
    private const int PageHeight = 16838;
    private const int Margin = 1134;
    private const int UsableWidth = PageWidth - 2 * Margin;

    private readonly string _projectName;

    public ExecutiveWordExporter(string projectName) => _projectName = projectName;

    public string Format => "executive";

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

        // Bloqueantes de NUESTRO trabajo: hallazgos bloqueantes de los proyectos imputables (excluye terceros
        // no modificables, cuya adaptacion corresponde al proveedor).
        var ourBlockers = managed
            .Where(a => report.Roles.RoleOf(a.Classification.Name) != ProjectRole.NoModificable)
            .Sum(a => a.ConfirmedFindings().Count(f => f.EsBloqueante));

        // Portada.
        b.Append(Para("Informe ejecutivo", bold: true, sizeHalfPt: 44, style: "Title"));
        b.Append(Para($"Análisis de portabilidad multiplataforma (.NET 8) — {_projectName}", bold: true, sizeHalfPt: 26));
        b.Append(Para($"Generado: {report.GeneratedAt:yyyy-MM-dd HH:mm}"));
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

    private static void AppendResumen(Body b, AnalysisReport report, List<AssemblyAnalysisResult> managed, int ourBlockers)
    {
        b.Append(Heading("Resumen ejecutivo", 1));
        var e = report.TotalEffort;
        var alcance = managed.Count == 1
            ? $"el proyecto analizado"
            : $"los {managed.Count} proyectos/ensamblados analizados";
        b.Append(Para(
            $"Este informe resume el trabajo necesario para hacer multiplataforma (portable a .NET 8) {alcance}. " +
            "El enfoque es portable-first: llevar todo lo posible a un núcleo portable y aislar únicamente lo que " +
            "depende obligatoriamente de Windows, dejándolo preparado para que otro equipo aporte la parte no-Windows."));
        b.Append(Para(
            $"Esfuerzo estimado total: {e.Media:0.#} horas-persona como valor más probable " +
            $"(rango {e.Optimista:0.#}–{e.Pesimista:0.#} h), con {ourBlockers} hallazgo(s) bloqueante(s) a resolver en nuestros proyectos. " +
            "Las cifras son una estimación de planificación y deben calibrarse con datos reales del equipo."));
    }

    private static void AppendCifrasClave(Body b, AnalysisReport report, int proyectos, int ourBlockers, int ficheros, int clases)
    {
        b.Append(Heading("Cifras clave", 1));
        var e = report.TotalEffort;
        var rows = new List<string[]>
        {
            new[] { "Proyectos/ensamblados analizados", proyectos.ToString() },
            new[] { "Hallazgos bloqueantes (nuestros proyectos)", ourBlockers.ToString() },
            new[] { "Ficheros afectados (código fuente)", ficheros.ToString() },
            new[] { "Clases afectadas", clases.ToString() },
            new[] { "Esfuerzo optimista (h)", $"{e.Optimista:0.#}" },
            new[] { "Esfuerzo más probable (h)", $"{e.Media:0.#}" },
            new[] { "Esfuerzo pesimista (h)", $"{e.Pesimista:0.#}" },
        };
        b.Append(BuildTable(new[] { "Métrica", "Valor" }, new[] { 4.0, 1.5 }, rows));
    }

    private static void AppendEstimacionPorProyecto(Body b, AnalysisReport report, List<AssemblyAnalysisResult> managed, int ourBlockers)
    {
        if (managed.Count == 0) return;
        b.Append(Heading("Estimación por proyecto", 1));
        b.Append(Para("Esfuerzo estimado (horas) por proyecto/ensamblado. Los de proveedores externos (no modificables) " +
                      "no imputan esfuerzo a nuestro total: su adaptación corresponde al proveedor."));

        var rows = new List<string[]>();
        foreach (var a in managed.OrderByDescending(a => a.Effort.Media))
        {
            var role = report.Roles.RoleOf(a.Classification.Name);
            var tipo = role switch
            {
                ProjectRole.NoModificable => "Tercero (no modificable)",
                ProjectRole.ObligatorioMultiplataforma => "Obligatorio multiplataforma",
                ProjectRole.DivisiblePorUI => "Divisible (UI)",
                _ => a.IsThirdParty ? "Tercero" : "Propio"
            };
            var blockers = a.ConfirmedFindings().Count(f => f.EsBloqueante);
            var esfuerzo = role == ProjectRole.NoModificable
                ? "no imputado"
                : $"{a.Effort.Optimista:0.#} / {a.Effort.Media:0.#} / {a.Effort.Pesimista:0.#}";
            rows.Add(new[] { a.Classification.Name, tipo, a.MaxSeverity.ToString(), blockers.ToString(), esfuerzo });
        }
        var e = report.TotalEffort;
        rows.Add(new[] { "TOTAL (imputado)", "", "", ourBlockers.ToString(),
            $"{e.Optimista:0.#} / {e.Media:0.#} / {e.Pesimista:0.#}" });

        b.Append(BuildTable(
            new[] { "Proyecto", "Tipo", "Severidad", "Bloq.", "Esfuerzo O / M / P (h)" },
            new[] { 3.0, 2.2, 1.4, 0.9, 2.4 },
            rows));
    }

    private static void AppendCostePorBloque(Body b, AnalysisReport report)
    {
        if (report.CostByBucket.Count == 0) return;
        b.Append(Heading("Coste por bloque de trabajo", 1));
        var grand = report.CostByBucket.Aggregate(EffortEstimate.Zero, (a, x) => a.Add(x.Effort));
        var rows = report.CostByBucket.Select(x =>
        {
            var pct = grand.Media > 0 ? x.Effort.Media / grand.Media * 100 : 0;
            return new[] { CostBuckets.Text(x.Bucket), $"{x.Effort.Media:0.#}", $"{pct:0} %" };
        }).ToList();
        rows.Add(new[] { "Total (con Pruebas y CI)", $"{grand.Media:0.#}", "100 %" });
        b.Append(BuildTable(new[] { "Bloque", "Media (h)", "%" }, new[] { 4.0, 1.4, 1.0 }, rows));
    }

    private static void AppendHallazgosPrincipales(Body b, AnalysisReport report)
    {
        // Categorias de dependencia mas frecuentes: primero por codigo fuente; si no hay, por ensamblado.
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

        b.Append(Heading("Hallazgos principales", 1));
        b.Append(Para("Tipos de dependencia de Windows más frecuentes (a resolver o aislar):"));
        b.Append(BuildTable(
            new[] { "Tipo de dependencia", "Nº de usos" },
            new[] { 4.0, 1.5 },
            top.Select(x => new[] { CategoriaTexto(x.Cat), x.Count.ToString() })));
    }

    private static void AppendRestricciones(Body b, AnalysisReport report, List<AssemblyAnalysisResult> managed)
    {
        var noMod = managed
            .Where(a => report.Roles.RoleOf(a.Classification.Name) == ProjectRole.NoModificable)
            .Select(a => a.Classification.Name)
            .ToList();
        if (noMod.Count == 0) return;

        b.Append(Heading("Restricciones (componentes de terceros)", 1));
        b.Append(Para(
            $"Los siguientes componentes son de proveedores externos y NO se pueden migrar ni modificar por nuestra parte " +
            $"(es responsabilidad del proveedor); su esfuerzo no se imputa a nuestro total: {string.Join(", ", noMod)}. " +
            "Para ejecutarlos en el entorno destino existen vías viables (versión multiplataforma del proveedor, " +
            "aislarlos en un host Windows con un contrato de servicio, capa de compatibilidad o sustitución), detalladas " +
            "en el informe general."));
    }

    private static void AppendRecomendacion(Body b)
    {
        b.Append(Heading("Recomendación", 1));
        b.Append(Para(
            "Adoptar una arquitectura portable-first: un núcleo .NET 8 multiplataforma lo más grande posible, una capa de " +
            "interfaces (seam) para lo que dependa del sistema operativo, y una única pieza aislada con lo obligatoriamente " +
            "Windows. Priorizar la resolución de los puntos bloqueantes y de los proyectos marcados como obligatorios. La " +
            "implementación de la plataforma no-Windows queda preparada tras las interfaces, para que otro equipo la desarrolle."));
    }

    private static string CategoriaTexto(string cat) => cat switch
    {
        "UI" => "Interfaz de usuario (WPF/WinForms)",
        "Database" => "Acceso a datos (Oracle/SQL nativo)",
        "Registry" => "Registro de Windows",
        "Identity" => "Identidad / autenticación de Windows",
        "PInvoke" => "Llamadas nativas (P/Invoke)",
        "COM" => "Componentes COM",
        "WMI" => "WMI (información del sistema)",
        "Cryptography" => "Criptografía (DPAPI/CNG)",
        "EventLog" => "Registro de eventos de Windows",
        "ServiceProcess" => "Servicios de Windows",
        "Threading" => "Sincronización / hilos de UI",
        _ => cat
    };

    // --- Infraestructura Word (autocontenida) ---

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
