namespace PortabilityAnalyzer.Core;

/// <summary>Carga (y opcionalmente valida) el catalogo de reglas.</summary>
public interface IRuleCatalogLoader
{
    RuleCatalog Load(string catalogPath);
}

/// <summary>Clasifica un ensamblado antes de decompilarlo.</summary>
public interface IAssemblyClassifier
{
    AssemblyClassification Classify(string assemblyPath);
}

/// <summary>Agrega el esfuerzo de un conjunto de hallazgos (una vez por regla).</summary>
public interface IEffortEstimator
{
    EffortEstimate Aggregate(IEnumerable<Finding> findings, bool isThirdParty);
}

/// <summary>Exporta el informe en un formato concreto.</summary>
public interface IReportExporter
{
    string Format { get; }
    void Export(AnalysisReport report, string outputPath);
}
