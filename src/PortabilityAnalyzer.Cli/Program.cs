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

            // 3) Descubrir y analizar. Si la entrada es un .sln, se resuelven los proyectos y se
            //    determina isThirdParty por ensamblado; si es un directorio o DLL, se usa la
            //    asuncion global (--assume-third-party).
            IReadOnlyList<AssemblyRef> assemblies;
            if (options.InputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && File.Exists(options.InputPath))
            {
                assemblies = new SolutionProjectDiscovery().Discover(options.InputPath);
                Log.Information("Descubrimiento por solucion: {Count} ensamblados ({ThirdParty} de terceros)",
                    assemblies.Count, assemblies.Count(a => a.IsThirdParty));
            }
            else
            {
                assemblies = AssemblyDiscovery.Discover(options.InputPath)
                    .Select(p => new AssemblyRef(p, options.AssumeThirdParty))
                    .ToList();
                Log.Information("Ensamblados encontrados: {Count}", assemblies.Count);
            }

            var results = new List<AssemblyAnalysisResult>();
            foreach (var asmRef in assemblies)
                results.Add(engine.AnalyzeAssembly(asmRef.Path, asmRef.IsThirdParty));

            // 4) Agregar y exportar.
            var analyzed = results.Where(r => r.Classification.Kind == AssemblyKind.Managed).ToList();
            var total = analyzed.Select(r => r.Effort)
                                .Aggregate(EffortEstimate.Zero, (acc, e) => acc.Add(e));

            var report = new AnalysisReport
            {
                GeneratedAt = DateTimeOffset.Now,
                Assemblies = results,
                TotalEffort = total,
                BlockerCount = results.Count(r => r.HasBlocker),
                AnalyzedCount = analyzed.Count,
                SkippedCount = results.Count - analyzed.Count
            };

            IReportExporter exporter = options.Format == "markdown"
                ? new MarkdownReportExporter()
                : new JsonReportExporter();
            exporter.Export(report, options.OutputPath);

            Log.Information("Informe escrito en {Path} (bloqueantes: {Blockers}, esfuerzo medio: {Media:0.#} h)",
                options.OutputPath, report.BlockerCount, report.TotalEffort.Media);
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
}
