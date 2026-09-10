using PortabilityAnalyzer.Core;
using PortabilityAnalyzer.Engine;
using PortabilityAnalyzer.Engine.Detectors;
using PortabilityAnalyzer.Reporting;
using PortabilityAnalyzer.Rules;
using Serilog;

namespace PortabilityAnalyzer.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options is null)
        {
            CliOptions.PrintUsage();
            return 1;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            // 1) Cargar y validar el catalogo contra el esquema.
            var loader = new RuleCatalogJsonLoader(options.SchemaPath);
            var catalog = loader.Load(options.RulesPath);
            Log.Information("Catalogo cargado: {Count} reglas", catalog.Reglas.Count);

            // 2) Registrar detectores (uno por tipo de patron; ver IAssemblyDetector.Handles).
            IReadOnlyList<IAssemblyDetector> detectors = new IAssemblyDetector[]
            {
                new PInvokeDetector(),
                new AssemblyReferenceDetector(),
                new AttributeDetector(),
                new TypeReferenceDetector(),
                new ApiCallDetector(),
                new StringLiteralDetector()
            };

            var engine = new AnalysisEngine(
                new AssemblyClassifier(),
                detectors,
                new PertEffortEstimator(options.ThirdPartyFactor),
                catalog,
                Log.Logger);

            // 3) Descubrir y analizar. Si la entrada es un .sln o un .csproj, se resuelven los
            //    proyectos y se determina isThirdParty por ensamblado; si es un directorio o DLL/EXE,
            //    se usa la asuncion global (--assume-third-party).
            IReadOnlyList<AssemblyRef> discovered;
            if (ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath))
            {
                discovered = new ProjectDiscovery().Discover(options.InputPath);
                Log.Information("Descubrimiento por solucion/proyecto: {Count} ensamblados ({ThirdParty} de terceros)",
                    discovered.Count, discovered.Count(a => a.IsThirdParty));
            }
            else
            {
                discovered = AssemblyDiscovery.Discover(options.InputPath)
                    .Select(p => new AssemblyRef(p, options.AssumeThirdParty))
                    .ToList();
                Log.Information("Ensamblados encontrados: {Count}", discovered.Count);
            }

            // Deduplicar por nombre de ensamblado: una misma DLL descubierta en varios sitios se
            // analiza (y aparece en el informe) una sola vez.
            var assemblies = discovered
                .GroupBy(a => Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // Analisis a nivel de codigo fuente (solo si la entrada es .sln/.csproj y hay .cs).
            IReadOnlyList<SourceFinding> sourceFindings = Array.Empty<SourceFinding>();
            if (ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath))
            {
                var projects = new ProjectDiscovery().GetProjects(options.InputPath);
                sourceFindings = new SourceCodeAnalyzer().AnalyzeProjects(projects);
                Log.Information("Analisis de codigo fuente: {Count} hallazgos en {Files} ficheros",
                    sourceFindings.Count, sourceFindings.Select(f => f.File).Distinct().Count());
            }

            // Orden de compilacion (solo para .sln/.csproj): topologia de ProjectReference.
            var buildOrder = BuildOrder.Empty;
            if (ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath))
            {
                buildOrder = new ProjectDiscovery().ResolveBuildOrder(options.InputPath);
                if (buildOrder.Steps.Count > 0 || buildOrder.HasCycle)
                    Log.Information("Orden de compilacion: {Steps} proyecto(s){Cycle}",
                        buildOrder.Steps.Count, buildOrder.HasCycle ? $", CICLO en {buildOrder.CycleProjects.Count}" : string.Empty);
            }

            // Roles de proyecto (opcional): API obligatoria, no modificables, divisibles por UI.
            var roles = new ProjectRoles();
            if (options.RolesPath is null)
                Log.Information("Sin fichero de roles (--roles): no se generaran proyectos separados.");
            else if (!File.Exists(options.RolesPath))
                Log.Warning("No se encuentra el fichero de roles '{Path}': no se generaran proyectos separados.", options.RolesPath);
            else
            {
                try
                {
                    roles = System.Text.Json.JsonSerializer.Deserialize<ProjectRoles>(
                        File.ReadAllText(options.RolesPath),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ProjectRoles();
                    static string L(IReadOnlyList<string> xs) => xs.Count == 0 ? "(vacio)" : string.Join(", ", xs);
                    Log.Information("Roles cargados de {Path} -> obligatorioMultiplataforma: [{O}]; divisiblePorUI: [{D}]; separables: [{S}]; noModificables: [{N}]",
                        options.RolesPath, L(roles.ObligatorioMultiplataforma), L(roles.DivisiblePorUI), L(roles.Separables), L(roles.NoModificables));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "No se pudieron cargar los roles de {Path}: {Error}", options.RolesPath, ex.Message);
                }
            }

            // Carpeta de salida de los informes: crearla si no existe (p. ej. si el usuario la borro para
            // regenerarla). Sin esto, la escritura del informe fallaria con DirectoryNotFoundException.
            var reportDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)) ?? ".";
            Directory.CreateDirectory(reportDir);

            // Generar el scaffold de division (Windows/Multi) para los proyectos separables:
            // divisiblePorUI, obligatorioMultiplataforma y los listados en "separables". Los proyectos
            // generados se escriben en una SUBCARPETA dedicada ('proyectos-separados') para que NUNCA
            // colisionen con las carpetas de codigo originales (evita borrar el codigo fuente del usuario).
            // Si se pide la REESCRITURA COMPLETA (--rewrite), NO se genera el scaffold antiguo por-proyecto
            // (proyectos-separados/ + SPLIT-NOTES): la reescritura lo sustituye por la solucion completa.
            var splitResults = new List<SplitResult>();
            if (options.RewriteDir is null && !roles.IsEmpty && ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath))
            {
                var splitBaseDir = Path.Combine(reportDir, "proyectos-separados");
                Directory.CreateDirectory(splitBaseDir);

                var proyectos = new ProjectDiscovery().GetProjects(options.InputPath);
                var separables = proyectos.Where(p => roles.IsSeparable(p.Name)).Select(p => p.Name).ToList();
                Log.Information("Proyectos descubiertos ({N}): {Proyectos}", proyectos.Count, string.Join(", ", proyectos.Select(p => p.Name)));
                if (separables.Count == 0)
                    Log.Warning("Ningun proyecto coincide con los roles separables (divisiblePorUI / obligatorioMultiplataforma / separables). " +
                                "Revisa que los NOMBRES del fichero de roles coincidan con los nombres de proyecto listados arriba. No se generaran proyectos separados.");
                else
                    Log.Information("Proyectos a separar: {Separables}", string.Join(", ", separables));

                foreach (var (pname, pdir) in proyectos)
                {
                    if (!roles.IsSeparable(pname)) continue;
                    if (!Directory.Exists(pdir))
                    {
                        Log.Warning("No se puede separar {Proj}: no existe su carpeta de proyecto '{Dir}'.", pname, pdir);
                        continue;
                    }
                    try
                    {
                        var r = new ProjectSplitter().Split(pname, pdir, sourceFindings, splitBaseDir);
                        splitResults.Add(r);
                        Log.Information("Split de {Proj}: {Multi} ({P} ficheros) + {Win} ({W} ficheros) en {Dir}",
                            pname, r.MultiProject, r.PortableFiles, r.WindowsProject, r.WindowsFiles, splitBaseDir);
                    }
                    catch (Exception ex) { Log.Warning(ex, "No se pudo dividir {Proj}: {Error}", pname, ex.Message); }
                }
            }

            // REESCRITURA COMPLETA A MULTIPLATAFORMA (--rewrite <dir>): genera una solucion nueva hermana
            // con todos los proyectos separados en net8.0 (portable) + net8.0-windows. No toca el original.
            if (options.RewriteDir is not null && ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath))
            {
                var proyectos = new ProjectDiscovery().GetProjects(options.InputPath);
                var slnName = Path.GetFileNameWithoutExtension(options.InputPath) + "-multiplataforma";
                try
                {
                    var rewrite = new SolutionRewriter().Rewrite(slnName, proyectos, sourceFindings, Path.GetFullPath(options.RewriteDir));
                    Log.Information("Reescritura multiplataforma: {N} proyecto(s) -> {Portable} portable, {Sep} separable(s), {Win} solo-Windows en {Dir}",
                        rewrite.Projects.Count,
                        rewrite.Projects.Count(p => p.Kind == "Portable"),
                        rewrite.Projects.Count(p => p.Kind == "Separable"),
                        rewrite.Projects.Count(p => p.Kind == "SoloWindows"),
                        rewrite.OutputDir);
                    Log.Information("Solucion reescrita: {Sln}", rewrite.SolutionFile);
                    foreach (var w in rewrite.Warnings) Log.Warning("Reescritura: {Aviso}", w);
                }
                catch (Exception ex) { Log.Error(ex, "No se pudo reescribir la solucion: {Error}", ex.Message); }
            }

            var results = new List<AssemblyAnalysisResult>();
            foreach (var asmRef in assemblies)
                results.Add(engine.AnalyzeAssembly(asmRef.Path, asmRef.IsThirdParty));

            // 4) Agregar y exportar.
            var analyzed = results.Where(r => r.Classification.Kind == AssemblyKind.Managed).ToList();

            // El esfuerzo NO incluye los ensamblados con rol "no modificable" (proveedor externo):
            // su adaptacion la debe hacer el proveedor, no se imputa a nuestro total.
            var imputables = results
                .Where(r => roles.RoleOf(r.Classification.Name) != ProjectRole.NoModificable)
                .ToList();
            var total = imputables
                .Where(r => r.Classification.Kind == AssemblyKind.Managed)
                .Select(r => r.Effort)
                .Aggregate(EffortEstimate.Zero, (acc, e) => acc.Add(e));

            // Desglose del coste por bucket multiplataforma (incluye Pruebas y CI transversal).
            var costByBucket = new CostBucketEstimator(options.ThirdPartyFactor, options.TestingFactor)
                .Compute(imputables);

            var report = new AnalysisReport
            {
                GeneratedAt = DateTimeOffset.Now,
                Assemblies = results,
                TotalEffort = total,
                BlockerCount = results.Count(r => r.HasBlocker),
                AnalyzedCount = analyzed.Count,
                SkippedCount = results.Count - analyzed.Count,
                CostByBucket = costByBucket,
                SourceFindings = sourceFindings,
                Roles = roles,
                SplitResults = splitResults,
                BuildOrder = buildOrder,
                SourceName = ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath)
                    ? Path.GetFileNameWithoutExtension(options.InputPath)
                    : Path.GetFileNameWithoutExtension(options.OutputPath),
                ProjectNames = ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath)
                    ? new ProjectDiscovery().GetProjects(options.InputPath).Select(p => p.Name).ToList()
                    : new List<string>()
            };

            // Una sola ejecucion puede generar varios informes (p. ej. Word + Markdown). Con un unico
            // formato se respeta --output tal cual; con varios se deriva la extension por formato.
            var single = options.Formats.Count == 1;
            foreach (var fmt in options.Formats)
            {
                var path = single ? options.OutputPath : OutputPathFor(options.OutputPath, fmt);
                if (fmt == "word")
                {
                    // El informe general de Word se genera en ESPAÑOL e INGLÉS (informe.docx e informe_EN.docx).
                    foreach (var lang in new[] { Lang.Es, Lang.En })
                    {
                        var langPath = lang == Lang.En ? WithSuffix(path, "_EN") : path;
                        new WordReportExporter(lang).Export(report, langPath);
                        Log.Information("Informe word ({Lang}) escrito en {Path}", lang, langPath);
                    }
                }
                else
                {
                    IReportExporter exporter = fmt == "markdown" ? new MarkdownReportExporter() : new JsonReportExporter();
                    exporter.Export(report, path);
                    Log.Information("Informe {Format} escrito en {Path}", fmt, path);
                }
            }

            // Informe EJECUTIVO (solo con --executive): se genera en ESPAÑOL e INGLÉS (Word) en la carpeta
            // del informe general -> InformeEjec_<proyecto>.docx (ES) e InformeEjec_<proyecto>_EN.docx (EN).
            if (options.Executive)
            {
                var projName = ProjectDiscovery.Handles(options.InputPath) && File.Exists(options.InputPath)
                    ? Path.GetFileNameWithoutExtension(options.InputPath)
                    : Path.GetFileNameWithoutExtension(options.OutputPath);
                foreach (var texts in new[] { ExecTexts.Spanish, ExecTexts.English })
                {
                    var execPath = Path.Combine(reportDir, $"InformeEjec_{projName}{texts.FileSuffix}.docx");
                    new ExecutiveWordExporter(projName, texts).Export(report, execPath);
                    Log.Information("Informe ejecutivo escrito en {Path}", execPath);
                }
            }

            Log.Information("Analisis completado (bloqueantes: {Blockers}, esfuerzo medio: {Media:0.#} h)",
                report.BlockerCount, report.TotalEffort.Media);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Analisis abortado");
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Deriva la ruta de salida de un formato a partir de la base de --output y su extension
    /// canonica (.json/.md/.docx). Se usa cuando se piden varios formatos en una misma ejecucion.</summary>
    private static string OutputPathFor(string basePath, string format)
    {
        var dir = Path.GetDirectoryName(basePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = format switch { "markdown" => ".md", "word" => ".docx", _ => ".json" };
        return Path.Combine(dir, name + ext);
    }

    /// <summary>Inserta un sufijo antes de la extension (informe.docx + "_EN" -> informe_EN.docx).</summary>
    private static string WithSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, name + suffix + ext);
    }
}
