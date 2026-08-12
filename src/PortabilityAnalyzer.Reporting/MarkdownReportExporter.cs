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
        sb.AppendLine("# Informe de análisis multiplataforma (.NET 8) - portabilidad de Windows");
        sb.AppendLine();
        sb.AppendLine($"Generado: {report.GeneratedAt:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine($"Analizados: {report.AnalyzedCount} | Omitidos: {report.SkippedCount} | Con bloqueantes: {report.BlockerCount}  ");
        sb.AppendLine($"Esfuerzo total de desarrollo: optimista {report.TotalEffort.Optimista:0.#} h | media {report.TotalEffort.Media:0.#} h | pesimista {report.TotalEffort.Pesimista:0.#} h");
        sb.AppendLine();
        AppendEstimationNote(sb);

        AppendBuildOrderSection(sb, report);
        AppendBucketSummary(sb, report.CostByBucket);
        AppendArchitectureSection(sb, report);
        AppendSplitSection(sb, report);
        AppendThirdPartySection(sb, report);
        AppendNonModifiableSection(sb, report);
        AppendImpactSection(sb, report);
        AppendSourceSection(sb, report);
        AppendCodeExamplesSection(sb, report);

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
        sb.AppendLine($"Objetivo: **núcleo .NET 8 portable** lo más grande posible + **lo obligatoriamente Windows aislado** (Platform.Windows / `#if`), dejando el resto **preparado para otro equipo**. Esfuerzo total estimado (con Pruebas y CI): **{plan.TotalWithTesting.Media:0.#} h** (optimista {plan.TotalWithTesting.Optimista:0.#} / pesimista {plan.TotalWithTesting.Pesimista:0.#}). Bloqueantes: **{plan.Blockers}**.");
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

    /// <summary>Scaffold de división de los proyectos con rol divisiblePorUI (dos proyectos generados).</summary>
    private static void AppendSplitSection(StringBuilder sb, AnalysisReport report)
    {
        if (report.SplitResults.Count == 0) return;

        sb.AppendLine("## División de proyectos (scaffold generado)");
        sb.AppendLine();
        sb.AppendLine("> Para los proyectos con rol `divisiblePorUI` se han generado **dos proyectos** en la carpeta de salida: una parte **multiplataforma** (net8.0) y otra **Windows** (net8.0-windows). Es un **punto de partida**: revisar las referencias cruzadas y las acciones pendientes.");
        sb.AppendLine();
        foreach (var s in report.SplitResults)
        {
            sb.AppendLine($"### {Cell(s.OriginalProject)} → {Cell(s.MultiProject)} (net8.0) + {Cell(s.WindowsProject)} (net8.0-windows)");
            sb.AppendLine($"- Ficheros portables: **{s.PortableFiles}** · Ficheros Windows: **{s.WindowsFiles}**  ");
            sb.AppendLine($"- Generados en: `{Cell(s.OutputDir)}` (ver `SPLIT-NOTES-*.md`)  ");
            sb.AppendLine();
            if (s.CrossReferences.Count > 0)
            {
                sb.AppendLine("**Referencias cruzadas a resolver (introducir abstracción):**");
                foreach (var r in s.CrossReferences.Take(20)) sb.AppendLine($"- {Cell(r)}");
                if (s.CrossReferences.Count > 20) sb.AppendLine($"- … y {s.CrossReferences.Count - 20} más");
                sb.AppendLine();
            }
            sb.AppendLine("**Acciones manuales pendientes:**");
            foreach (var m in s.ManualNotes) sb.AppendLine($"- {Cell(m)}");
            sb.AppendLine();
        }
    }

    /// <summary>Apéndice con ejemplos de equivalencia Linux / compilación condicional por categoría.</summary>
    private static void AppendCodeExamplesSection(StringBuilder sb, AnalysisReport report)
    {
        var cats = report.Assemblies.SelectMany(a => a.ConfirmedFindings()).Select(f => f.Categoria)
            .Concat(report.SourceFindings.Select(f => f.Categoria))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var examples = CodeExamples.ForCategories(cats);
        if (examples.Count == 0) return;

        sb.AppendLine("## Aislamiento por SO y equivalencias portables (ejemplos)");
        sb.AppendLine();
        sb.AppendLine("> Ejemplos de código para cada tipo de dependencia detectada: la equivalencia portable o cómo aislar lo que hoy exige Windows (`OperatingSystem.IsWindows()` / `#if`), dejando el hueco preparado. No se desarrolla la implementación de otra plataforma.");
        sb.AppendLine();
        foreach (var e in examples)
        {
            sb.AppendLine($"### {e.Titulo}");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(e.Codigo);
            sb.AppendLine("```");
            sb.AppendLine($"> {e.Nota}");
            sb.AppendLine();
        }
    }

    /// <summary>Terceros no modificables (ACRA/XMA/Safran): restricción + opciones viables detalladas.</summary>
    private static void AppendNonModifiableSection(StringBuilder sb, AnalysisReport report)
    {
        var providers = NonModifiableOptions.Analyze(report);
        if (providers.Count == 0) return;

        sb.AppendLine("## Terceros no modificables: restricción y opciones viables");
        sb.AppendLine();
        sb.AppendLine("> Estos componentes son de proveedores externos: **no se pueden migrar ni modificar** (lo debe hacer el proveedor) y su esfuerzo **no se imputa** a nuestro total. Para cada uno se detallan las **vías viables** para poder ejecutarlo en el entorno destino.");
        sb.AppendLine();
        foreach (var p in providers)
        {
            sb.AppendLine($"### {p.Assembly}");
            sb.AppendLine();
            sb.AppendLine($"**Restricción.** {p.Restriccion}");
            sb.AppendLine();
            foreach (var o in p.Opciones)
            {
                var marca = o.Recomendada ? " ✅ (recomendada)" : string.Empty;
                sb.AppendLine($"- **{o.Titulo}**{marca}: {o.Detalle}");
            }
            sb.AppendLine();
        }
    }

    /// <summary>Métrica de impacto: clases y ficheros afectados por proyecto (además de las horas).</summary>
    private static void AppendImpactSection(StringBuilder sb, AnalysisReport report)
    {
        if (report.SourceFindings.Count == 0) return;

        sb.AppendLine("## Impacto por proyecto (clases y ficheros afectados)");
        sb.AppendLine();
        sb.AppendLine("> Métrica de tamaño del cambio (además de las horas): cuántas clases y ficheros de cada proyecto usan APIs de Windows.");
        sb.AppendLine();
        sb.AppendLine("| Proyecto | Nº ficheros | Nº clases | Usos Windows | Clases afectadas | Ficheros afectados |");
        sb.AppendLine("|----------|-------------|-----------|--------------|------------------|--------------------|");
        foreach (var g in report.SourceFindings.GroupBy(f => f.Project).OrderByDescending(g => g.Count()))
        {
            var ficheros = g.Select(f => f.File).Distinct().OrderBy(x => x).ToList();
            var clases = g.Where(f => !string.IsNullOrEmpty(f.Clase)).Select(f => f.Clase!).Distinct().OrderBy(x => x).ToList();
            var clasesTexto = clases.Count == 0 ? "—" : string.Join(", ", clases);
            sb.AppendLine($"| {Cell(g.Key)} | {ficheros.Count} | {clases.Count} | {g.Count()} | {Cell(clasesTexto)} | {Cell(string.Join(", ", ficheros))} |");
        }
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
    /// <summary>Orden correcto de compilacion de los proyectos (topologia de ProjectReference).</summary>
    private static void AppendBuildOrderSection(StringBuilder sb, AnalysisReport report)
    {
        var bo = report.BuildOrder;
        if (bo.Steps.Count == 0 && !bo.HasCycle) return;

        sb.AppendLine("## Orden de compilación de los proyectos");
        sb.AppendLine();
        sb.AppendLine("> Orden derivado de las referencias de proyecto (`ProjectReference`): cada proyecto se compila **después** de aquellos a los que referencia. Los proyectos del **mismo nivel** no dependen entre sí y podrían compilarse en paralelo.");
        sb.AppendLine();
        sb.AppendLine("| # | Nivel | Proyecto | Depende de |");
        sb.AppendLine("|---|-------|----------|------------|");
        var i = 1;
        foreach (var s in bo.Steps)
        {
            var dep = s.DependsOn.Count == 0 ? "— (sin dependencias internas)" : string.Join(", ", s.DependsOn);
            sb.AppendLine($"| {i++} | {s.Level} | {Cell(s.Project)} | {Cell(dep)} |");
        }
        sb.AppendLine();

        if (bo.HasCycle)
        {
            sb.AppendLine($"> ⚠️ **Ciclo de referencias detectado** entre: {Cell(string.Join(", ", bo.CycleProjects))}. No existe un orden lineal para esos proyectos; hay que romper el ciclo (extraer un proyecto común o invertir una dependencia).");
            sb.AppendLine();
        }
    }

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
                sb.AppendLine("**Dependencias nativas del SO (P/Invoke):**");
                sb.AppendLine();
                sb.AppendLine("| DLL nativa | Sitios P/Invoke | Tipo |");
                sb.AppendLine("|------------|-----------------|------|");
                foreach (var d in p.NativeDeps)
                    sb.AppendLine($"| {Cell(d.Dll)} | {d.Sites} | {ThirdPartyAnalysis.DependencyKind(d)} |");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("Sin dependencias nativas P/Invoke detectadas (revisar referencias gestionadas).");
                sb.AppendLine();
            }

            if (p.WindowsApis.Count > 0)
            {
                sb.AppendLine("**APIs Windows gestionadas (no P/Invoke):**");
                sb.AppendLine();
                sb.AppendLine("| Categoría | API / tipo Windows | Sitios | Alternativa portable / multiplataforma |");
                sb.AppendLine("|-----------|--------------------|--------|-------------------------------------|");
                foreach (var a in p.WindowsApis)
                    sb.AppendLine($"| {Cell(a.Categoria)} | {Cell(a.Api)} | {a.Sites} | {Cell(a.AlternativaLinux)} |");
                sb.AppendLine();
            }
        }
    }

    /// <summary>Nota de cabecera: explica la estrategia de estimación (en horas) y la columna N.</summary>
    private static void AppendEstimationNote(StringBuilder sb)
    {
        sb.AppendLine("> **Cómo se estima (en horas-persona).** Cada dependencia se estima a tres puntos: **O = optimista**, **M = más probable**, **P = pesimista**. La **media = (O + 4·M + P) / 6** (método PERT) es el **valor esperado**, es decir, la **estimación más probable** a efectos de planificación. El esfuerzo se cuenta **una vez por regla y ensamblado** (no por cada ocurrencia); a los **terceros** se les aplica un factor de incertidumbre; y se añade un bucket transversal de **Pruebas y CI**. La columna **N (ocurr.)** de las tablas es el **número de ocurrencias** de esa misma dependencia (regla + evidencia).");
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
        sb.AppendLine("| Regla | Severidad | N (ocurr.) | Esfuerzo (h) | Ubicación (ejemplo) | Estrategia | Evidencia | Alternativa portable / multiplataforma (reemplazo propuesto) | Pasos de remediación |");
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
