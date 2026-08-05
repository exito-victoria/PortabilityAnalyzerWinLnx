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

            var results = new List<AssemblyAnalysisResult>();
            foreach (var asmRef in assemblies)
                results.Add(engine.AnalyzeAssembly(asmRef.Path, asmRef.IsThirdParty));

            // 4) Agregar y exportar.
            var analyzed = results.Where(r => r.Classification.Kind == AssemblyKind.Managed).ToList();
            var total = analyzed.Select(r => r.Effort)
                                .Aggregate(EffortEstimate.Zero, (acc, e) => acc.Add(e));

            // Desglose del coste por bucket multiplataforma (incluye Pruebas y CI transversal).
            var costByBucket = new CostBucketEstimator(options.ThirdPartyFactor, options.TestingFactor)
                .Compute(results);

            var report = new AnalysisReport
            {
                GeneratedAt = DateTimeOffset.Now,
                Assemblies = results,
                TotalEffort = total,
                BlockerCount = results.Count(r => r.HasBlocker),
                AnalyzedCount = analyzed.Count,
                SkippedCount = results.Count - analyzed.Count,
                CostByBucket = costByBucket
            };

            // Una sola ejecucion puede generar varios informes (p. ej. Word + Markdown). Con un unico
            // formato se respeta --output tal cual; con varios se deriva la extension por formato.
            var single = options.Formats.Count == 1;
            foreach (var fmt in options.Formats)
            {
                IReportExporter exporter = fmt switch
                {
                    "markdown" => new MarkdownReportExporter(),
                    "word" => new WordReportExporter(),
                    _ => new JsonReportExporter()
                };
                var path = single ? options.OutputPath : OutputPathFor(options.OutputPath, fmt);
                exporter.Export(report, path);
                Log.Information("Informe {Format} escrito en {Path}", fmt, path);
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
}
