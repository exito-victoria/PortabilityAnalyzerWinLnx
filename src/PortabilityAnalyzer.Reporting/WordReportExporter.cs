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

    private readonly Lang _lang;

    /// <summary>El informe general por defecto se emite en español; con <paramref name="lang"/> = En, en inglés.</summary>
    public WordReportExporter(Lang lang = Lang.Es) => _lang = lang;

    /// <summary>Elige texto español/inglés según el idioma del informe.</summary>
    private string T(string es, string en) => Loc.T(_lang, es, en);

    /// <summary>Formatea un número con la cultura del idioma (coma en ES, punto en EN).</summary>
    private string N(double v) => v.ToString("0.#", Loc.Cult(_lang));

    public void Export(AnalysisReport report, string outputPath)
    {
        using var word = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = word.AddMainDocumentPart();
        mainPart.Document = new Document();
        AddStyleDefinitions(mainPart);   // estilos Titulo / Encabezado 1-3 (para el Panel de navegacion y el TOC).
        AddUpdateFieldsOnOpen(mainPart);  // que Word actualice la Tabla de contenido al abrir el documento.
        var b = mainPart.Document.AppendChild(new Body());

        b.Append(Para(T("Informe de análisis multiplataforma (.NET 8) - portabilidad de Windows",
                        "Cross-platform analysis report (.NET 8) - Windows portability"), bold: true, sizeHalfPt: 36));
        if (!string.IsNullOrWhiteSpace(report.SourceName))
            b.Append(Para($"{T("Proyecto", "Project")}: {report.SourceName}", bold: true, sizeHalfPt: 26));
        b.Append(Para($"{T("Generado", "Generated")}: {report.GeneratedAt:yyyy-MM-dd HH:mm}"));
        b.Append(Para(T($"Analizados: {report.AnalyzedCount} | Omitidos: {report.SkippedCount} | Con bloqueantes: {report.BlockerCount}",
                        $"Analyzed: {report.AnalyzedCount} | Skipped: {report.SkippedCount} | With blockers: {report.BlockerCount}")));
        b.Append(Para(T($"Esfuerzo total de desarrollo: optimista {N(report.TotalEffort.Optimista)} h | media {N(report.TotalEffort.Media)} h | pesimista {N(report.TotalEffort.Pesimista)} h",
                        $"Total development effort: optimistic {N(report.TotalEffort.Optimista)} h | mean {N(report.TotalEffort.Media)} h | pessimistic {N(report.TotalEffort.Pesimista)} h")));
        b.Append(Para(T(
            "Cómo se estima (en horas-persona): cada dependencia se estima a tres puntos: O = optimista, M = más probable, P = pesimista. La media = (O + 4·M + P) / 6 (método PERT) es el valor esperado, es decir, la estimación más probable a efectos de planificación. El esfuerzo se cuenta una vez por regla y ensamblado (no por ocurrencia); a los terceros se les aplica un factor de incertidumbre; se añade un bucket de Pruebas y CI. La columna N (ocurr.) es el número de ocurrencias de esa dependencia (regla + evidencia).",
            "How it is estimated (in person-hours): each dependency is estimated with three points: O = optimistic, M = most likely, P = pessimistic. The mean = (O + 4·M + P) / 6 (PERT method) is the expected value, i.e. the most likely estimate for planning. Effort is counted once per rule and assembly (not per occurrence); third parties get an uncertainty factor; a Testing & CI bucket is added. The N (occ.) column is the number of occurrences of that dependency (rule + evidence).")));
        b.Append(Para(string.Empty));

        // Tabla de contenido: se rellena con los titulos (estilos Encabezado 1/2) al abrir/actualizar en Word.
        b.Append(Para(T("Tabla de contenido", "Table of contents"), bold: true, sizeHalfPt: 30));
        b.Append(BuildTocField());
        b.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

        AppendBuildOrderSection(b, report);
        AppendBucketSummary(b, report.CostByBucket);
        AppendArchitectureSection(b, report);
        AppendSplitSection(b, report);
        AppendThirdPartySection(b, report);
        AppendNonModifiableSection(b, report);
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
                b.Append(Para(asm.Classification.Reason ?? T("Sin hallazgos.", "No findings.")));
                b.Append(Para(string.Empty));
                continue;
            }

            var terceros = asm.IsThirdParty ? T(" | Terceros (factor de incertidumbre aplicado)", " | Third-party (uncertainty factor applied)") : string.Empty;
            b.Append(Para(T($"Severidad máxima: {Loc.Severity(asm.MaxSeverity, _lang)} | Esfuerzo medio: {N(asm.Effort.Media)} h{terceros}",
                            $"Max severity: {Loc.Severity(asm.MaxSeverity, _lang)} | Mean effort: {N(asm.Effort.Media)} h{terceros}")));

            if (confirmed.Count > 0)
            {
                b.Append(BuildTable(
                    new[]
                    {
                        T("Regla", "Rule"), T("Severidad", "Severity"), T("N (ocurr.)", "N (occ.)"), T("Esfuerzo (h)", "Effort (h)"),
                        T("Ubicación (ejemplo)", "Location (sample)"), T("Estrategia", "Strategy"), T("Evidencia", "Evidence"),
                        T("Alternativa portable / multiplataforma (reemplazo propuesto)", "Portable / cross-platform alternative (proposed replacement)"),
                        T("Pasos de remediación", "Remediation steps")
                    },
                    confirmedWeights,
                    confirmed.Select(g =>
                    {
                        var f = g.Representative;
                        return new[]
                        {
                            f.RuleId, Loc.Severity(f.Severidad, _lang), g.Count.ToString(),
                            N(f.Esfuerzo.Media),
                            ReportGrouping.SampleLocation(f, g.Count),
                            ReportGrouping.StrategyText(f.EstrategiaSeparacion, _lang),
                            f.Evidencia ?? string.Empty,
                            ReportGrouping.AlternativeWithNote(f, _lang),
                            ReportGrouping.StepsInline(f, _lang)
                        };
                    })));
            }
            else
            {
                b.Append(Para(T("Sin hallazgos confirmados (solo señales débiles, ver abajo).", "No confirmed findings (only weak signals, see below).")));
            }

            if (manual.Count > 0)
            {
                var ocurrencias = manual.Sum(g => g.Count);
                b.Append(Para(T($"Revisión manual - señal débil, excluida del esfuerzo ({manual.Count} grupos / {ocurrencias} ocurrencias)",
                                $"Manual review - weak signal, excluded from effort ({manual.Count} groups / {ocurrencias} occurrences)"),
                    bold: true, sizeHalfPt: 24));
                b.Append(BuildTable(
                    new[] { T("Regla", "Rule"), T("Severidad", "Severity"), T("Confianza", "Confidence"), T("N (ocurr.)", "N (occ.)"), T("Evidencia", "Evidence"), T("Ubicación (ejemplo)", "Location (sample)") },
                    manualWeights,
                    manual.Select(g =>
                    {
                        var f = g.Representative;
                        return new[]
                        {
                            f.RuleId, Loc.Severity(f.Severidad, _lang), Loc.Confidence(f.Confianza, _lang),
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
    private void AppendArchitectureSection(Body b, AnalysisReport report)
    {
        var plan = ArchitectureRecommendation.Build(report, _lang);

        b.Append(Para(T("Arquitectura destino recomendada y plan de migración", "Recommended target architecture and migration plan"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            $"Objetivo: núcleo .NET 8 portable lo más grande posible + lo obligatoriamente Windows aislado (Platform.Windows / #if), dejando el resto preparado para otro equipo. Esfuerzo total estimado (con Pruebas y CI): {N(plan.TotalWithTesting.Media)} h (optimista {N(plan.TotalWithTesting.Optimista)} / pesimista {N(plan.TotalWithTesting.Pesimista)}). Bloqueantes: {plan.Blockers}.",
            $"Goal: a .NET 8 portable core as large as possible + the strictly-Windows parts isolated (Platform.Windows / #if), leaving the rest ready for another team. Total estimated effort (with Testing & CI): {N(plan.TotalWithTesting.Media)} h (optimistic {N(plan.TotalWithTesting.Optimista)} / pessimistic {N(plan.TotalWithTesting.Pesimista)}). Blocking points: {plan.Blockers}.")));
        b.Append(Para(T(
            "Qué es un «seam» (costura): el punto de extensión —una interfaz— por el que el núcleo portable llama a una capacidad que depende del sistema operativo, sin conocer su implementación. Cada plataforma aporta su propia implementación de esa interfaz; así el núcleo se mantiene portable y lo específico de cada SO queda encapsulado y sustituible.",
            "What is a \"seam\": the extension point —an interface— through which the portable core calls an OS-dependent capability without knowing its implementation. Each platform provides its own implementation of that interface; thus the core stays portable and the OS-specific parts are encapsulated and replaceable.")));

        if (plan.RoleNotes.Count > 0)
        {
            b.Append(Para(T("Roles y restricciones", "Roles and constraints"), bold: true, sizeHalfPt: 24));
            foreach (var n in plan.RoleNotes)
                b.Append(Para($"- {n}"));
        }

        b.Append(Para(T("Estructura de proyectos propuesta", "Proposed project structure"), bold: true, sizeHalfPt: 24));
        b.Append(BuildTable(
            new[] { T("Proyecto", "Project"), "TFM", T("Propósito", "Purpose") },
            new[] { 2.5, 1.5, 4.0 },
            plan.Projects.Select(p => new[] { p.Name, p.Tfm, p.Purpose })));

        if (plan.Abstractions.Count > 0)
        {
            b.Append(Para(T("Capa de abstracción (interfaces por plataforma)", "Abstraction layer (per-platform interfaces)"), bold: true, sizeHalfPt: 24));
            foreach (var a in plan.Abstractions)
                b.Append(Para($"- {a}"));
        }

        b.Append(Para(T("Plan de migración", "Migration plan"), bold: true, sizeHalfPt: 24));
        for (int i = 0; i < plan.MigrationSteps.Count; i++)
            b.Append(Para($"{i + 1}. {plan.MigrationSteps[i]}"));
        if (!string.IsNullOrWhiteSpace(plan.WorkedExample))
            b.Append(Para(plan.WorkedExample!));
        b.Append(Para(string.Empty));
    }

    /// <summary>Scaffold de división de los proyectos con rol divisiblePorUI.</summary>
    private void AppendSplitSection(Body b, AnalysisReport report)
    {
        if (report.SplitResults.Count == 0) return;

        b.Append(Para(T("División de proyectos (scaffold generado)", "Project split (generated scaffold)"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            "Para los proyectos separables se han generado dos proyectos en la carpeta de salida: una parte multiplataforma (net8.0) y otra Windows (net8.0-windows). Es un punto de partida: revisar las referencias cruzadas y las acciones pendientes.",
            "For the separable projects, two projects were generated in the output folder: a cross-platform part (net8.0) and a Windows part (net8.0-windows). It is a starting point: review the cross-references and the pending actions.")));
        foreach (var s in report.SplitResults)
        {
            b.Append(Para($"{s.OriginalProject} -> {s.MultiProject} (net8.0) + {s.WindowsProject} (net8.0-windows)", bold: true, sizeHalfPt: 24));
            b.Append(Para(T(
                $"Ficheros portables: {s.PortableFiles} · Ficheros Windows: {s.WindowsFiles}. Generados en: {s.OutputDir} (ver SPLIT-NOTES-*.md).",
                $"Portable files: {s.PortableFiles} · Windows files: {s.WindowsFiles}. Generated in: {s.OutputDir} (see SPLIT-NOTES-*.md).")));
            if (s.CrossReferences.Count > 0)
            {
                b.Append(Para(T("Referencias cruzadas a resolver (introducir abstracción):", "Cross-references to resolve (introduce an abstraction):"), bold: true));
                foreach (var r in s.CrossReferences.Take(20)) b.Append(Para($"- {r}"));
            }
            b.Append(Para(T("Acciones manuales pendientes:", "Pending manual actions:"), bold: true));
            foreach (var m in s.ManualNotes) b.Append(Para($"- {m}"));
        }
        b.Append(Para(string.Empty));
    }

    /// <summary>Apéndice con ejemplos de equivalencia Linux / compilación condicional por categoría.</summary>
    private void AppendCodeExamplesSection(Body b, AnalysisReport report)
    {
        var cats = report.Assemblies.SelectMany(a => a.ConfirmedFindings()).Select(f => f.Categoria)
            .Concat(report.SourceFindings.Select(f => f.Categoria))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var examples = CodeExamples.ForCategories(cats, _lang);
        if (examples.Count == 0) return;

        b.Append(Para(T("Aislamiento por SO y equivalencias portables (ejemplos)", "OS isolation and portable equivalents (examples)"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            "Ejemplos de código para cada tipo de dependencia detectada: la equivalencia portable o cómo aislar lo que hoy exige Windows (OperatingSystem.IsWindows() / #if), dejando el hueco preparado. No se desarrolla la implementación de otra plataforma.",
            "Code examples for each detected dependency type: the portable equivalent or how to isolate what currently requires Windows (OperatingSystem.IsWindows() / #if), leaving the seam ready. The other platform's implementation is not developed here.")));
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
    /// <summary>Terceros no modificables (ACRA/XMA/Safran): restriccion + opciones viables detalladas.</summary>
    private void AppendNonModifiableSection(Body b, AnalysisReport report)
    {
        var providers = NonModifiableOptions.Analyze(report, _lang);
        if (providers.Count == 0) return;

        b.Append(Para(T("Terceros no modificables: restricción y opciones viables", "Non-modifiable third parties: constraint and viable options"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            "Estos componentes son de proveedores externos: no se pueden migrar ni modificar (lo debe hacer el proveedor) y su esfuerzo no se imputa a nuestro total. Para cada uno se detallan las vías viables para poder ejecutarlo en el entorno destino.",
            "These components come from external vendors: they cannot be migrated or modified (the vendor must do it) and their effort is not charged to our total. For each one, the viable ways to run it on the target environment are detailed.")));
        foreach (var p in providers)
        {
            b.Append(Para(p.Assembly, bold: true, sizeHalfPt: 24));
            b.Append(Para($"{T("Restricción", "Constraint")}. {p.Restriccion}"));
            foreach (var o in p.Opciones)
            {
                var marca = o.Recomendada ? T(" [RECOMENDADA]", " [RECOMMENDED]") : string.Empty;
                b.Append(Para($"• {o.Titulo}{marca}: {o.Detalle}"));
            }
        }
        b.Append(Para(string.Empty));
    }

    private void AppendImpactSection(Body b, AnalysisReport report)
    {
        if (report.SourceFindings.Count == 0) return;

        b.Append(Para(T("Impacto por proyecto (clases y ficheros afectados)", "Impact per project (affected classes and files)"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            "Métrica de tamaño del cambio (además de las horas): cuántas clases y ficheros de cada proyecto usan APIs de Windows.",
            "Change-size metric (besides the hours): how many classes and files of each project use Windows APIs.")));

        var rows = report.SourceFindings
            .GroupBy(f => f.Project)
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var ficheros = g.Select(f => f.File).Distinct().OrderBy(x => x).ToList();
                var clases = g.Where(f => !string.IsNullOrEmpty(f.Clase)).Select(f => f.Clase!).Distinct().OrderBy(x => x).ToList();
                var clasesTexto = clases.Count == 0 ? "—" : string.Join(", ", clases);
                return new[] { g.Key, ficheros.Count.ToString(), clases.Count.ToString(), g.Count().ToString(), clasesTexto, string.Join(", ", ficheros) };
            });

        b.Append(BuildTable(
            new[] { T("Proyecto", "Project"), T("Nº ficheros", "# files"), T("Nº clases", "# classes"), T("Usos Windows", "Windows uses"), T("Clases afectadas", "Affected classes"), T("Ficheros afectados", "Affected files") },
            new[] { 1.8, 1.0, 1.0, 1.1, 3.0, 3.0 },
            rows));
        b.Append(Para(string.Empty));
    }

    /// <summary>Analisis a nivel de codigo fuente: fichero/linea/segmento + como corregir.</summary>
    private void AppendSourceSection(Body b, AnalysisReport report)
    {
        var findings = report.SourceFindings;
        if (findings.Count == 0) return;
        var files = findings.Select(f => f.File).Distinct().Count();

        b.Append(Para(T("Análisis de código fuente (dónde y cómo corregir)", "Source-code analysis (where and how to fix)"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            $"Usos de APIs propias de Windows en el código fuente (Roslyn): {findings.Count} usos en {files} ficheros. Se indica fichero, línea, el segmento de código y la corrección multiplataforma.",
            $"Uses of Windows-only APIs in the source code (Roslyn): {findings.Count} uses in {files} files. It shows file, line, code segment and the cross-platform fix.")));

        b.Append(BuildTable(
            new[] { T("Proyecto", "Project"), T("Fichero:línea", "File:line"), T("Tipo", "Kind"), T("Símbolo", "Symbol"), T("Clase / Método", "Class / Method"), T("Segmento de código", "Code segment"), T("Cómo corregir", "How to fix") },
            new[] { 1.5, 2.2, 0.9, 1.8, 1.8, 3.2, 3.4 },
            findings.Select(f => new[]
            {
                f.Project, $"{f.File}:{f.Line}", f.Kind, f.Symbol,
                string.Join(" / ", new[] { f.Clase, f.Metodo }.Where(x => !string.IsNullOrEmpty(x))),
                f.Segmento, _lang == Lang.En ? Loc.FixEn(f.Categoria) : f.ComoCorregir
            })));
        b.Append(Para(string.Empty));
    }

    /// <summary>Analisis en profundidad de los ensamblados de terceros (sin fuentes).</summary>
    /// <summary>Orden correcto de compilacion de los proyectos (topologia de ProjectReference).</summary>
    private void AppendBuildOrderSection(Body b, AnalysisReport report)
    {
        var bo = report.BuildOrder;
        if (bo.Steps.Count == 0 && !bo.HasCycle) return;

        b.Append(Para(T("Orden de compilación de los proyectos", "Project build order"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            "Orden derivado de las referencias de proyecto (ProjectReference): cada proyecto se compila después de aquellos a los que referencia. Los proyectos del mismo nivel no dependen entre sí y podrían compilarse en paralelo.",
            "Order derived from project references (ProjectReference): each project builds after the ones it references. Projects on the same level do not depend on each other and could build in parallel.")));

        var noDeps = T("— (sin dependencias internas)", "— (no internal dependencies)");
        var i = 1;
        var rows = bo.Steps.Select(s => new[]
        {
            (i++).ToString(),
            s.Level.ToString(),
            s.Project,
            s.TargetFramework,
            s.DependsOn.Count == 0 ? noDeps : string.Join(", ", s.DependsOn)
        });
        b.Append(BuildTable(
            new[] { "#", T("Nivel", "Level"), T("Proyecto", "Project"), "Target Framework", T("Depende de", "Depends on") },
            new[] { 0.5, 0.8, 2.6, 1.9, 3.2 },
            rows));

        if (bo.HasCycle)
            b.Append(Para(T(
                $"AVISO: ciclo de referencias detectado entre {string.Join(", ", bo.CycleProjects)}. No existe un orden lineal para esos proyectos; hay que romper el ciclo (extraer un proyecto común o invertir una dependencia).",
                $"WARNING: reference cycle detected among {string.Join(", ", bo.CycleProjects)}. There is no linear order for those projects; the cycle must be broken (extract a shared project or invert a dependency)."), bold: true));
        b.Append(Para(string.Empty));
    }

    private void AppendThirdPartySection(Body b, AnalysisReport report)
    {
        var profiles = ThirdPartyAnalysis.Analyze(report);
        if (profiles.Count == 0) return;

        b.Append(Para(T("Análisis de terceros (sin fuentes)", "Third-party analysis (no sources)"), bold: true, sizeHalfPt: 28));
        b.Append(Para(T(
            "Estos ensamblados son de terceros: no se dispone del código fuente ni control de su build. Verificar si el paquete tiene versión multiplataforma; si no, reemplazarlo o encapsular su uso tras una interfaz.",
            "These assemblies are third-party: no source code or control over their build. Check whether the package has a cross-platform version; if not, replace it or encapsulate its use behind an interface.")));

        foreach (var p in profiles)
        {
            b.Append(Para($"{p.Assembly} ({T("Severidad", "Severity")}: {Loc.Severity(p.MaxSeverity, _lang)})", bold: true, sizeHalfPt: 24));
            if (p.SuggestedReplacement is not null)
                b.Append(Para($"{T("Reemplazo sugerido", "Suggested replacement")}: {p.SuggestedReplacement}"));
            b.Append(Para(T($"APIs/referencias Windows gestionadas detectadas: {p.WindowsApiRules} regla(s)",
                            $"Managed Windows APIs/references detected: {p.WindowsApiRules} rule(s)")));

            if (p.NativeDeps.Count > 0)
            {
                b.Append(Para(T("Dependencias nativas del SO (P/Invoke):", "Native OS dependencies (P/Invoke):"), bold: true));
                b.Append(BuildTable(
                    new[] { T("DLL nativa", "Native DLL"), T("Sitios P/Invoke", "P/Invoke sites"), T("Tipo", "Kind") },
                    new[] { 3.0, 1.5, 3.5 },
                    p.NativeDeps.Select(d => new[] { d.Dll, d.Sites.ToString(), ThirdPartyAnalysis.DependencyKind(d, _lang) })));
            }
            else
            {
                b.Append(Para(T("Sin dependencias nativas P/Invoke detectadas (revisar referencias gestionadas).",
                                "No native P/Invoke dependencies detected (review managed references).")));
            }

            if (p.WindowsApis.Count > 0)
            {
                b.Append(Para(T("APIs Windows gestionadas (no P/Invoke):", "Managed Windows APIs (non-P/Invoke):"), bold: true));
                b.Append(BuildTable(
                    new[] { T("Categoría", "Category"), T("API / tipo Windows", "API / Windows type"), T("Sitios", "Sites"), T("Alternativa portable / multiplataforma", "Portable / cross-platform alternative") },
                    new[] { 2.0, 3.0, 1.0, 4.0 },
                    p.WindowsApis.Select(a => new[] { Loc.Category(a.Categoria, _lang), a.Api, a.Sites.ToString(), a.AlternativaLinux })));
            }
        }
        b.Append(Para(string.Empty));
    }

    /// <summary>Resumen ejecutivo del coste por bucket multiplataforma (incluye Pruebas y CI).</summary>
    private void AppendBucketSummary(Body b, IReadOnlyList<BucketEffort> buckets)
    {
        if (buckets.Count == 0) return;
        var grand = buckets.Aggregate(EffortEstimate.Zero, (a, x) => a.Add(x.Effort));

        b.Append(Para(T("Coste por bucket (multiplataforma)", "Cost by bucket (cross-platform)"), bold: true, sizeHalfPt: 28));

        var rows = new List<string[]>();
        foreach (var x in buckets)
        {
            var pct = grand.Media > 0 ? x.Effort.Media / grand.Media * 100 : 0;
            rows.Add(new[]
            {
                Loc.Bucket(x.Bucket, _lang), N(x.Effort.Optimista), N(x.Effort.Media), N(x.Effort.Pesimista), $"{pct:0} %"
            });
        }
        rows.Add(new[]
        {
            T("Total (con Pruebas y CI)", "Total (with Testing & CI)"), N(grand.Optimista), N(grand.Media), N(grand.Pesimista), "100 %"
        });

        b.Append(BuildTable(
            new[] { "Bucket", T("Optimista", "Optimistic"), T("Media", "Mean"), T("Pesimista", "Pessimistic"), "%" },
            new[] { 4.0, 1.2, 1.2, 1.2, 1.0 },
            rows));
        b.Append(Para(T(
            "Modelo: esfuerzo una vez por regla y ensamblado (PERT); factor de terceros aplicado a sus ensamblados; Pruebas y CI como fracción del esfuerzo de desarrollo.",
            "Model: effort counted once per rule and assembly (PERT); uncertainty factor applied to third-party assemblies; Testing & CI as a fraction of the development effort.")));
        b.Append(Para(string.Empty));
    }

    /// <summary>Define los estilos con nombre Titulo / Encabezado 1-3 (con nivel de esquema) para que Word
    /// los reconozca en el Panel de navegacion y al generar la Tabla de contenido.</summary>
    private static void AddStyleDefinitions(MainDocumentPart mainPart)
    {
        var part = mainPart.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            HeadingStyle("Title", "Title", null, 40, "1F3864"),
            HeadingStyle("Heading1", "heading 1", 0, 32, "2F5496"),
            HeadingStyle("Heading2", "heading 2", 1, 28, "2F5496"),
            HeadingStyle("Heading3", "heading 3", 2, 24, "1F3864"));
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
            new StyleRunProperties(
                new Bold(),
                new Color { Val = colorHex },
                new FontSize { Val = sizeHalfPt.ToString() }))
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };
    }

    /// <summary>Hace que Word actualice los campos (incluida la Tabla de contenido) al abrir el documento.</summary>
    private static void AddUpdateFieldsOnOpen(MainDocumentPart mainPart)
    {
        var settingsPart = mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings = new Settings(new UpdateFieldsOnOpen { Val = true });
    }

    /// <summary>Parrafo con el campo TOC (niveles 1-3, hipervinculos). Word lo rellena al abrir/actualizar.</summary>
    private Paragraph BuildTocField()
    {
        return new Paragraph(
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" TOC \\o \"1-3\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(new Text(T("Tabla de contenido: clic derecho > Actualizar campos (F9) para rellenarla.",
                               "Table of contents: right-click > Update field (F9) to populate it.")) { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
    }

    private static Paragraph Para(string text, bool bold = false, int? sizeHalfPt = null)
    {
        var runProps = new RunProperties();
        if (bold) runProps.Append(new Bold());
        if (sizeHalfPt.HasValue) runProps.Append(new FontSize { Val = sizeHalfPt.Value.ToString() });

        var run = new Run();
        if (runProps.HasChildren) run.Append(runProps);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

        var para = new Paragraph();
        // Los titulos (negrita + tamano de titulo) se asocian a un estilo con nombre (Titulo/Encabezado 1/2),
        // para que Word los reconozca en el Panel de navegacion y al generar la Tabla de contenido.
        var styleId = HeadingStyleFor(bold, sizeHalfPt);
        if (styleId is not null)
            para.Append(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        para.Append(run);
        return para;
    }

    /// <summary>Mapea (negrita, tamano) al estilo de titulo de Word: 36->Titulo, 28->Encabezado 1, 24->Encabezado 2.</summary>
    private static string? HeadingStyleFor(bool bold, int? sizeHalfPt) =>
        (bold, sizeHalfPt) switch
        {
            (true, 36) => "Title",
            (true, 28) => "Heading1",
            (true, 24) => "Heading2",
            _ => null
        };

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

        table.AppendChild(BuildRow(headers, widths, bold: true, header: true));
        foreach (var r in rows)
            table.AppendChild(BuildRow(r, widths, bold: false));
        return table;
    }

    private static TableRow BuildRow(string[] cells, int[] widths, bool bold, bool header = false)
    {
        var row = new TableRow();
        // La fila de cabecera se marca con <w:tblHeader/> para que Word la REPITA en cada pagina
        // cuando la tabla se parte en varias hojas.
        if (header)
            row.Append(new TableRowProperties(new TableHeader()));
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
