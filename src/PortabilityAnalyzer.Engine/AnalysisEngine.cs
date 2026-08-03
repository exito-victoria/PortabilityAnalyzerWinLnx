using Mono.Cecil;
using PortabilityAnalyzer.Core;
using Serilog;

namespace PortabilityAnalyzer.Engine;

/// <summary>Orquesta la clasificacion, la carga con Cecil y la ejecucion de los detectores sobre un ensamblado.</summary>
public sealed class AnalysisEngine
{
    private readonly IAssemblyClassifier _classifier;
    private readonly IReadOnlyList<IAssemblyDetector> _detectors;
    private readonly IEffortEstimator _estimator;
    private readonly RuleCatalog _catalog;
    private readonly ILogger _log;

    public AnalysisEngine(
        IAssemblyClassifier classifier,
        IEnumerable<IAssemblyDetector> detectors,
        IEffortEstimator estimator,
        RuleCatalog catalog,
        ILogger log)
    {
        _classifier = classifier;
        _detectors = detectors.ToList();
        _estimator = estimator;
        _catalog = catalog;
        _log = log;
    }

    public AssemblyAnalysisResult AnalyzeAssembly(string path, bool isThirdParty)
    {
        var classification = _classifier.Classify(path);

        if (classification.Kind != AssemblyKind.Managed)
        {
            _log.Information("Omitido {Assembly}: {Kind} ({Reason})",
                classification.Name, classification.Kind, classification.Reason);
            return new AssemblyAnalysisResult { Classification = classification, IsThirdParty = isThirdParty };
        }

        try
        {
            var readerParameters = new ReaderParameters
            {
                ReadingMode = ReadingMode.Deferred,
                InMemory = true,
                ReadSymbols = false
            };

            // AssemblyContext toma la propiedad del AssemblyDefinition y lo dispone (ver AssemblyContext.Dispose).
            var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
            using var context = new AssemblyContext(assembly, path);

            var findings = new List<Finding>();
            foreach (var detector in _detectors)
            {
                var rules = _catalog.Reglas
                    .Where(r => detector.Handles.Contains(r.Patron.Tipo))
                    .ToList();
                if (rules.Count == 0) continue;

                try
                {
                    findings.AddRange(detector.Detect(context, rules));
                }
                catch (Exception ex)
                {
                    // Robustez: un detector que falla no aborta el analisis del ensamblado.
                    _log.Warning(ex, "El detector {Detector} fallo en {Assembly}",
                        detector.GetType().Name, context.Name);
                }
            }

            // El esfuerzo y la severidad se calculan solo con los hallazgos confirmados (confianza Alta/Media).
            // Los de confianza Baja (senal debil) se conservan en Findings para listarlos como "revision manual",
            // pero no inflan las metricas por su alta tasa de falsos positivos.
            var confirmed = findings.Where(f => f.Confianza != Confidence.Baja).ToList();
            var effort = _estimator.Aggregate(confirmed, isThirdParty);
            var maxSeverity = confirmed.Count == 0 ? Severity.Info : confirmed.Max(f => f.Severidad);

            return new AssemblyAnalysisResult
            {
                Classification = classification,
                Findings = findings,
                Effort = effort,
                MaxSeverity = maxSeverity,
                IsThirdParty = isThirdParty
            };
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "No se pudo analizar {Assembly}", classification.Name);
            return new AssemblyAnalysisResult
            {
                Classification = classification with { Kind = AssemblyKind.Unreadable, Reason = ex.Message },
                IsThirdParty = isThirdParty
            };
        }
    }
}
